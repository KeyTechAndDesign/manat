"""Yığım (MAGNET).
The webhook carries only `reference` and is unsigned, so verify_callback re-reads the payment.
09 (issuer timeout) and S7 are not final: the money may have moved, so they stay pending until cancelled."""

from __future__ import annotations

import re
from typing import Any, Dict, Optional

from .. import _form
from .._http import Http, Transport
from .._values import money, text
from ..errors import INVALID_REQUEST, INVALID_SIGNATURE, PROVIDER_ERROR, ManatError
from ..money import Money, currency_from_numeric
from ..types import (CreatePaymentRequest, CreatePaymentResult, IncomingCallback, NextAction, PaymentAction, PaymentEvent,
                     PaymentState, RefundRequest, RefundResult)

NAME = "yigim"
_BASE_URLS = {"test": "https://sandbox.api.pay.yigim.az", "production": "https://api.pay.yigim.az"}
_OK = 0
_REFERENCE = re.compile(r"^[^\s\x00-\x1f]{1,255}$")
_STATUSES = {
    "S0": "pending", "S2": "pending", "S7": "pending", "09": "pending",  # waiting for card data, in progress, malfunction, issuer timeout
    "S1": "authorized",  # DMS hold: charge or cancel within 30 days
    "00": "paid",
    "S4": "cancelled",  # reversed
    "S3": "failed", "01": "failed", "02": "failed", "03": "failed", "04": "failed",
    "05": "failed", "06": "failed", "07": "failed", "08": "failed",
}


def map_status(status: Optional[str]) -> str:
    """Maps a payment status code."""
    if status not in _STATUSES:
        raise ManatError(PROVIDER_ERROR, f'yigim: unknown payment status "{status}"', provider=NAME)
    return _STATUSES[status]


class Yigim:
    name = NAME
    capabilities = frozenset({"createPayment", "verifyCallback", "getStatus", "refund", "capture", "cancel"})

    def __init__(self, merchant: str, api_key: str, biller: str, *, template: str = "TPL0001", environment: str = "production",
                 base_url: Optional[str] = None, transport: Optional[Transport] = None, timeout: float = 30.0) -> None:
        for field, value in (("merchant", merchant), ("api_key", api_key), ("biller", biller)):
            if not value:
                raise ManatError(INVALID_REQUEST, f"yigim: {field} is required")
        self._headers = {"x-merchant": merchant, "x-api-key": api_key, "x-type": "JSON"}
        self._biller = biller
        self._template = template or "TPL0001"
        self._base_url = (base_url or _BASE_URLS[environment]).rstrip("/")
        self._http = Http(NAME, transport, timeout)

    def create_payment(self, request: CreatePaymentRequest) -> CreatePaymentResult:
        _require_reference(request.order_id)
        # The return URL travels in `extra` (name=value;name=value), which the gateway also cuts at `&`.
        if "&" in request.return_url or ";" in request.return_url:
            raise ManatError(INVALID_REQUEST, 'yigim: return_url may carry at most one query parameter and no ";"')
        params: Dict[str, str] = {
            "reference": request.order_id,
            "type": "DMS" if request.intent == "authorize" else "SMS",
            "amount": str(request.amount.amount),
            "currency": request.amount.numeric_currency(),
            "biller": self._biller,
        }
        if request.description:
            params["description"] = request.description
        params["template"] = self._template
        params["language"] = request.language or "az"
        # The gateway appends `&reference=…` to the callback URL as is, so it needs a query string.
        if request.callback_url:
            params["callback"] = request.callback_url if "?" in request.callback_url else request.callback_url + "?"
        params["extra"] = f"back-url={request.return_url};fail-url={request.return_url}"
        reply = self._call("payment/create", params)
        url = reply.get("url")
        if not isinstance(url, str) or not url:
            raise _error("payment page URL is missing", reply)
        return CreatePaymentResult(request.order_id, "pending", NextAction("redirect", url), reply)

    def verify_callback(self, callback: IncomingCallback) -> PaymentEvent:
        reference = callback.query_value("reference") or callback.body_field("reference")
        if reference is None or not _REFERENCE.match(reference):
            raise ManatError(INVALID_SIGNATURE, "yigim: callback has no reference", provider=NAME, raw=callback.to_dict())
        # The webhook is unsigned; trust only what payment/status says about this reference.
        state = self.get_status(reference)
        return PaymentEvent(state.payment_id, state.status, state.payment_id, state.amount, raw=state.raw)

    def get_status(self, payment_id: str) -> PaymentState:
        _require_reference(payment_id)
        reply = self._call("payment/status", {"reference": payment_id})
        reference = reply.get("reference")
        if reference and reference != payment_id:
            raise _error(f"status is for payment {text(reference)}, not {payment_id}", reply)
        currency = currency_from_numeric(reply["currency"]) if isinstance(reply.get("currency"), str) else None
        amount = None
        if reply.get("amount") not in (None, "") and currency:
            amount = money(reply["amount"], currency)
        return PaymentState(payment_id, map_status(text(reply.get("status"))), amount, raw=reply)

    def refund(self, request: RefundRequest) -> RefundResult:
        reply = self._call("payment/refund", _with_amount(request.payment_id, request.amount))
        state = self.get_status(request.payment_id)
        status = state.status
        # While payment/status still reads 00 the requested amount decides.
        if status == "paid":
            partial = request.amount is not None and state.amount is not None and request.amount.amount < state.amount.amount
            status = "partially_refunded" if partial else "refunded"
        return RefundResult(request.payment_id, status, raw={"refund": reply, "payment": state.raw})

    def capture(self, action: PaymentAction) -> PaymentState:
        self._call("payment/charge", _with_amount(action.payment_id, action.amount))
        return self.get_status(action.payment_id)

    def cancel(self, action: PaymentAction) -> PaymentState:
        self._call("payment/cancel", {"reference": action.payment_id})
        return self.get_status(action.payment_id)

    def _call(self, command: str, params: Dict[str, str]) -> dict:
        _require_reference(params.get("reference", ""))
        status, data = self._http.json("GET", f"{self._base_url}/{command}?{_form.encode(params)}", self._headers)
        # Replies may be nested under `response`.
        reply = data.get("response") if isinstance(data, dict) and isinstance(data.get("response"), dict) else data
        if not isinstance(reply, dict) or _code(reply) is None:
            raise ManatError(PROVIDER_ERROR, f"yigim: unexpected response (HTTP {status})", provider=NAME, raw=data)
        if status >= 400 or _code(reply) != _OK:
            raise _error(f"HTTP {status}", reply)
        return reply


def _code(reply: dict) -> Optional[int]:
    """`code` comes as 0 or "0"."""
    code: Any = reply.get("code")
    if isinstance(code, int) and not isinstance(code, bool):
        return code
    return int(code) if isinstance(code, str) and re.fullmatch(r"-?\d+", code) else None


def _error(what: str, reply: dict) -> ManatError:
    code = _code(reply)
    message = text(reply.get("message")) or None
    from_provider = code is not None and code != _OK
    return ManatError(PROVIDER_ERROR, f"yigim: {message if from_provider and message else what}", provider=NAME,
                      provider_code=str(code) if from_provider else None, provider_message=message if from_provider else None, raw=reply)


def _with_amount(reference: str, amount: Optional[Money]) -> Dict[str, str]:
    return {"reference": reference} if amount is None else {"reference": reference, "amount": str(amount.amount)}


def _require_reference(reference: str) -> None:
    if not isinstance(reference, str) or not _REFERENCE.match(reference):
        raise ManatError(INVALID_REQUEST, f'yigim: invalid reference "{reference}"')
