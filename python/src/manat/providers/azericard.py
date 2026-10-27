"""Azericard (form POST, RSA-signed).
Each message is signed over the length-prefixed values of a fixed field list (mac_fields).
The callback signature does not cover ORDER or ACTION, so only a signed APPROVAL counts as approved."""

from __future__ import annotations

import re
import secrets
from datetime import datetime, timedelta, timezone
from typing import Any, Callable, Dict, List, Mapping, Optional, Sequence

from .. import _crypto, _form
from .._http import Http, Transport
from .._values import text
from ..errors import INVALID_REQUEST, INVALID_SIGNATURE, PROVIDER_ERROR, ManatError
from ..money import CURRENCIES, Money, currency_from_numeric, from_decimal
from ..types import (CreatePaymentRequest, CreatePaymentResult, IncomingCallback, NextAction, PaymentAction, PaymentEvent,
                     PaymentState, RefundRequest, RefundResult)

NAME = "azericard"
_GATEWAY_URLS = {"test": "https://testmpi.3dsecure.az/cgi-bin/cgi_link", "production": "https://mpi.3dsecure.az/cgi-bin/cgi_link"}

#: MAC field orders per message type.
MAC_FIELDS: Dict[str, List[str]] = {
    "purchase": ["AMOUNT", "CURRENCY", "TERMINAL", "TRTYPE", "TIMESTAMP", "NONCE", "MERCH_URL"],
    "completion": ["AMOUNT", "CURRENCY", "TERMINAL", "TRTYPE", "ORDER", "RRN", "INT_REF"],
    "reversal": ["AMOUNT", "CURRENCY", "TERMINAL", "TRTYPE", "ORDER", "RRN", "INT_REF"],
    "callback": ["AMOUNT", "TERMINAL", "APPROVAL", "RRN", "INT_REF"],
    "status": ["ORDER", "TERMINAL", "TRTYPE", "TIMESTAMP", "NONCE"],
}

_ACTION_MESSAGES = {
    "0": "Approved",
    "1": "Duplicate transaction",
    "2": "Transaction declined",
    "3": "Transaction processing error",
    "6": "Repeat of a declined transaction",
    "7": "Repeat of a transaction with an authentication error",
    "8": "Repeat of a transaction that terminated without a response",
}
_ORDER = re.compile(r"^\d{6,32}$")
_HEX = re.compile(r"^(?:[0-9a-fA-F]{2})+$")
_DAY_SECONDS = 86400


def mac_source(fields: Mapping[str, Optional[str]], order: Sequence[str]) -> str:
    """Length-prefixed MAC source: "11.48" → "511.48", empty or missing → "-". Lengths are in bytes."""
    out = []
    for name in order:
        value = fields.get(name) or ""
        out.append("-" if value == "" else f"{len(value.encode('utf-8'))}{value}")
    return "".join(out)


def timestamp(moment: datetime) -> str:
    """GMT timestamp YYYYMMDDHHMMSS."""
    return moment.astimezone(timezone.utc).strftime("%Y%m%d%H%M%S")


def map_status_response(data: Mapping[str, Any], tran_trtype: str) -> str:
    """Status of one transaction type from a TRTYPE 90 reply."""
    if _approved(data):
        return "authorized" if tran_trtype == "0" else "paid"
    return "pending" if data.get("responseCode") == "-40" else "failed"  # -40: form in progress


class Azericard:
    name = NAME
    capabilities = frozenset({"createPayment", "verifyCallback", "getStatus", "refund", "capture", "cancel"})

    def __init__(self, terminal: str, private_key: str, merchant_name: str, merchant_url: str, *, gateway_public_key: Optional[str] = None,
                 environment: str = "production", gateway_url: Optional[str] = None, email: Optional[str] = None,
                 country: Optional[str] = None, merchant_gmt: Optional[str] = None, mac_fields: Optional[Mapping[str, List[str]]] = None,
                 clock: Optional[Callable[[], datetime]] = None, nonce: Optional[Callable[[], str]] = None,
                 transport: Optional[Transport] = None, timeout: float = 30.0) -> None:
        """private_key and gateway_public_key are PEM text; clock and nonce are for tests."""
        if not terminal:
            raise ManatError(INVALID_REQUEST, "azericard: terminal is required")
        if not merchant_name or not merchant_url:
            raise ManatError(INVALID_REQUEST, "azericard: merchant_name and merchant_url are required")
        if not private_key:
            raise ManatError(INVALID_REQUEST, "azericard: private_key (PEM) is required")
        try:
            self._private_key = _crypto.private_key(private_key)
        except (ValueError, IndexError) as error:
            raise ManatError(INVALID_REQUEST, "azericard: private_key is not a valid PEM key") from error
        if self._private_key.bits < 2048:
            raise ManatError(INVALID_REQUEST, "azericard: private_key must be an RSA key of at least 2048 bits")
        self._gateway_key = None
        if gateway_public_key:
            try:
                self._gateway_key = _crypto.public_key(gateway_public_key)
            except (ValueError, IndexError) as error:
                raise ManatError(INVALID_REQUEST, "azericard: gateway_public_key is not a valid PEM key") from error
        self._terminal = terminal
        self._merchant_name = merchant_name
        self._merchant_url = merchant_url
        self._email, self._country, self._merchant_gmt = email, country, merchant_gmt
        self._url = gateway_url or _GATEWAY_URLS[environment]
        self._mac_fields = {**MAC_FIELDS, **(mac_fields or {})}
        self._clock = clock or (lambda: datetime.now(timezone.utc))
        self._nonce = nonce or (lambda: secrets.token_hex(16))
        self._http = Http(NAME, transport, timeout)

    def create_payment(self, request: CreatePaymentRequest) -> CreatePaymentResult:
        order_id = request.order_id
        if not _ORDER.match(order_id):
            raise ManatError(INVALID_REQUEST, f'azericard: order_id must be 6–32 digits (got "{order_id}"); its last 6 digits must be unique per terminal per day')
        amount = request.amount
        candidates = [
            ("AMOUNT", amount.to_decimal_string()),
            ("CURRENCY", amount.currency),
            ("ORDER", order_id),
            ("DESC", (request.description if request.description is not None else f"Order {order_id}")[:50]),
            ("MERCH_NAME", self._merchant_name[:50]),
            ("MERCH_URL", self._merchant_url),
            ("TERMINAL", self._terminal),
            ("EMAIL", self._email),
            ("TRTYPE", "0" if request.intent == "authorize" else "1"),
            ("COUNTRY", self._country),
            ("MERCH_GMT", self._merchant_gmt),
            ("TIMESTAMP", timestamp(self._clock())),
            ("NONCE", self._nonce()),
            ("BACKREF", request.callback_url or request.return_url),
            ("LANG", request.language or "az"),
        ]
        fields = {name: value for name, value in candidates if value}
        fields["P_SIGN"] = self._sign(fields, "purchase")
        return CreatePaymentResult(order_id, "pending", NextAction("form", self._url, fields), {"fields": dict(fields)})

    def verify_callback(self, callback: IncomingCallback) -> PaymentEvent:
        if self._gateway_key is None:
            raise ManatError(INVALID_REQUEST, "azericard: gateway_public_key is required to verify callbacks; never accept them unverified")
        fields = _read_fields(callback)
        signature = fields.get("P_SIGN", "")
        if not _HEX.match(signature):
            raise ManatError(INVALID_SIGNATURE, "azericard: callback has no valid P_SIGN", provider=NAME, raw=fields)
        if not self._gateway_key.verify_sha256(mac_source(fields, self._mac_fields["callback"]), bytes.fromhex(signature)):
            raise ManatError(INVALID_SIGNATURE, "azericard: callback P_SIGN does not match", provider=NAME, raw=fields)
        terminal = fields.get("TERMINAL", "")
        if terminal != self._terminal:
            raise ManatError(INVALID_SIGNATURE, f"azericard: callback is for terminal {terminal}, not {self._terminal}", provider=NAME, raw=fields)
        order = fields.get("ORDER")
        if not order:
            raise ManatError(INVALID_SIGNATURE, "azericard: callback has no ORDER", provider=NAME, raw=fields)
        amount = _parse_amount(fields.get("AMOUNT"), fields.get("CURRENCY"))
        # ACTION is unsigned; the signed APPROVAL code is what proves an approval.
        approved = fields.get("ACTION") == "0" and bool(fields.get("APPROVAL"))
        provider_ref = None
        if approved and fields.get("RRN") and fields.get("INT_REF") and amount is not None and fields.get("TIMESTAMP"):
            provider_ref = _encode_ref(fields["RRN"], fields["INT_REF"], amount, fields["TIMESTAMP"], fields.get("TRTYPE", "1"))
        status = _status_for_trtype(fields.get("TRTYPE")) if approved else "failed"
        return PaymentEvent(order, status, order, amount, provider_ref, fields)

    def get_status(self, payment_id: str) -> PaymentState:
        """TRTYPE 90 answers for one transaction type, so the purchase (or pre-auth), completion and reversals are each queried."""
        if not _ORDER.match(payment_id):
            raise ManatError(INVALID_REQUEST, f'azericard: payment_id must be the numeric ORDER, got "{payment_id}"')
        trtype = "1"
        data = self._query_status(payment_id, trtype)
        if data.get("responseCode") == "-24":
            trtype = "0"
            data = self._query_status(payment_id, trtype)
        status = map_status_response(data, trtype)
        amount = _parse_amount(text(data.get("amount")), text(data.get("currency")))
        raw: Dict[str, Any] = {trtype: data}
        if status not in ("paid", "authorized") or amount is None:
            return PaymentState(payment_id, status, amount, raw=raw)

        approved: Dict[str, dict] = {}
        for kind in (("21", "22", "24") if trtype == "0" else ("22", "24")):
            raw[kind] = self._query_status(payment_id, kind)
            if _approved(raw[kind]):
                approved[kind] = raw[kind]
        if status == "authorized" and "21" in approved:
            status = "paid"
        refunded = _approved_amount(approved, "22", amount.currency) + _approved_amount(approved, "24", amount.currency)
        refunded_amount = None
        if refunded > 0:
            refunded_amount = Money(refunded, amount.currency)
            status = "cancelled" if status == "authorized" else ("refunded" if refunded >= amount.amount else "partially_refunded")
        provider_ref = None
        paid_at = _baku_time_to_gmt(text(data["tranDate"])) if data.get("tranDate") is not None else text(data.get("timestamp"))
        rrn, int_ref = text(data.get("rrn")), text(data.get("intRef"))
        if rrn and int_ref and paid_at:
            provider_ref = _encode_ref(rrn, int_ref, amount, paid_at, "21" if "21" in approved else trtype)
        return PaymentState(payment_id, status, amount, refunded_amount, provider_ref, raw)

    def capture(self, action: PaymentAction) -> PaymentState:
        ref = _require_ref(action.provider_ref)
        amount = action.amount or ref["amount"]
        self._send("completion", "21", action.payment_id, ref, amount)
        return PaymentState(action.payment_id, "paid", amount, None, _encode_ref(ref["rrn"], ref["int_ref"], amount, ref["timestamp"], "21"))

    def cancel(self, action: PaymentAction) -> PaymentState:
        ref = _require_ref(action.provider_ref)
        self._send("reversal", "22", action.payment_id, ref, ref["amount"])
        return PaymentState(action.payment_id, "cancelled", ref["amount"])

    def refund(self, request: RefundRequest) -> RefundResult:
        ref = _require_ref(request.provider_ref)
        amount = request.amount or ref["amount"]
        if amount.currency != ref["amount"].currency or amount.amount > ref["amount"].amount:
            raise ManatError(INVALID_REQUEST, "azericard: refund amount exceeds the paid amount or uses another currency")
        # Online reversal (22) works once, before settlement; otherwise it is offline (24).
        paid_at = _parse_timestamp(ref["timestamp"])
        trtype = "24"
        if paid_at is not None and self._clock().timestamp() - paid_at.timestamp() < _DAY_SECONDS:
            trtype = "24" if _approved(self._query_status(request.payment_id, "22")) else "22"
        action = self._send("reversal", trtype, request.payment_id, ref, amount)
        status = "partially_refunded" if amount.amount < ref["amount"].amount else "refunded"
        return RefundResult(request.payment_id, status, raw={"trtype": trtype, "action": action})

    def _query_status(self, order: str, tran_trtype: str) -> dict:
        fields = {"TRAN_TRTYPE": tran_trtype, "ORDER": order, "TERMINAL": self._terminal, "TRTYPE": "90",
                  "TIMESTAMP": timestamp(self._clock()), "NONCE": self._nonce()}
        fields["P_SIGN"] = self._sign(fields, "status")
        status, data = self._http.json("POST", self._url, None, None, fields)
        if status >= 400 or not isinstance(data, dict):
            raise ManatError(PROVIDER_ERROR, f"azericard: status request failed (HTTP {status})", provider=NAME, raw=data)
        return data

    def _send(self, message: str, trtype: str, order: str, ref: dict, amount: Money) -> str:
        fields = {"AMOUNT": amount.to_decimal_string(), "CURRENCY": amount.currency, "ORDER": order, "RRN": ref["rrn"],
                  "INT_REF": ref["int_ref"], "TERMINAL": self._terminal, "TRTYPE": trtype, "TIMESTAMP": timestamp(self._clock()),
                  "NONCE": self._nonce()}
        fields["P_SIGN"] = self._sign(fields, message)
        response = self._http.send("POST", self._url, None, None, fields)
        action = response.text.strip()
        if response.status >= 400:
            raise ManatError(PROVIDER_ERROR, f"azericard: HTTP {response.status}", provider=NAME, raw=response.text)
        if action != "0":
            message_text = _ACTION_MESSAGES.get(action, f'Unexpected response "{action[:100]}"')
            raise ManatError(PROVIDER_ERROR, f"azericard: {message_text}", provider=NAME, provider_code=action[:20],
                             provider_message=message_text, raw=response.text)
        return action

    def _sign(self, fields: Mapping[str, str], message: str) -> str:
        return self._private_key.sign_sha256(mac_source(fields, self._mac_fields[message])).hex()


def _approved(data: Mapping[str, Any]) -> bool:
    return data.get("actionCode") == "0" and data.get("responseCode") == "00"


def _approved_amount(approved: Mapping[str, dict], kind: str, currency: str) -> int:
    if kind not in approved:
        return 0
    data = approved[kind]
    code = text(data["currency"]) if data.get("currency") is not None else currency
    found = _parse_amount(text(data.get("amount")), code)
    return found.amount if found is not None and found.currency == currency else 0


def _baku_time_to_gmt(value: Optional[str]) -> Optional[str]:
    """"2026.10.26 19:06:40" in Baku time (UTC+4, no DST) → "20261026150640" GMT."""
    try:
        return (datetime.strptime(value or "", "%Y.%m.%d %H:%M:%S") - timedelta(hours=4)).strftime("%Y%m%d%H%M%S")
    except ValueError:
        return None


def _status_for_trtype(trtype: Optional[str]) -> str:
    if trtype == "0":
        return "authorized"
    return "refunded" if trtype in ("22", "24") else "paid"


def _encode_ref(rrn: str, int_ref: str, amount: Money, stamp: str, trtype: str) -> str:
    return "|".join(["az1", rrn, int_ref, amount.to_decimal_string(), amount.currency, stamp, trtype])


def _require_ref(value: Optional[str]) -> dict:
    parts = value.split("|") if value else []
    if len(parts) != 7 or parts[0] != "az1" or not parts[1] or not parts[2]:
        raise ManatError(INVALID_REQUEST, "azericard: provider_ref from the payment callback is required (store event.provider_ref with the order)")
    _, rrn, int_ref, amount, currency, stamp, _trtype = parts
    return {"rrn": rrn, "int_ref": int_ref, "amount": from_decimal(amount, _currency(currency)), "timestamp": stamp}


def _parse_amount(amount: Optional[str], currency: Optional[str]) -> Optional[Money]:
    if not amount or not currency:
        return None
    letters = currency_from_numeric(currency) if re.fullmatch(r"\d{3}", currency) else currency
    return None if letters is None else from_decimal(amount, _currency(letters))


def _currency(code: str) -> str:
    if code in CURRENCIES:
        return code
    raise ManatError(PROVIDER_ERROR, f'azericard: unsupported currency "{code}"', provider=NAME)


def _parse_timestamp(value: str) -> Optional[datetime]:
    try:
        return datetime.strptime(value, "%Y%m%d%H%M%S").replace(tzinfo=timezone.utc)
    except ValueError:
        return None


def _read_fields(callback: IncomingCallback) -> Dict[str, str]:
    """Upper-case keys, trimmed values; query first, body after."""
    out: Dict[str, str] = {}

    def add(key: Any, value: Any) -> None:
        if isinstance(value, (list, tuple)):
            value = value[0] if value else None
        if isinstance(value, str):
            out[str(key).upper()] = value.strip()

    for key, value in callback.query.items():
        add(key, value)
    body = _form.decode(callback.body) if isinstance(callback.body, str) else (callback.body or {})
    for key, value in body.items():
        add(key, value)
    return out


