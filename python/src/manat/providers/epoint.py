"""Epoint (API v1).
Every message is data (base64 JSON) + signature = base64(sha1(privateKey + data + privateKey))."""

from __future__ import annotations

import base64
import binascii
import re
from typing import Any, Dict, Optional

from .. import _json
from .._crypto import safe_equals, sha1_base64
from .._http import Http, Transport
from .._values import text, truthy
from ..errors import INVALID_REQUEST, INVALID_SIGNATURE, PROVIDER_ERROR, UNSUPPORTED_OPERATION, ManatError
from ..types import CreatePaymentRequest, CreatePaymentResult, IncomingCallback, NextAction, PaymentEvent, PaymentState, RefundRequest, RefundResult

NAME = "epoint"
_CURRENCIES = ("AZN", "USD", "EUR", "RUB")
_BASE64 = re.compile(r"^[A-Za-z0-9+/]+={0,2}$")
_STATUSES = {"new": "pending", "success": "paid", "returned": "refunded", "failed": "failed", "error": "failed"}


def signature(private_key: str, data: str) -> str:
    """base64(sha1(privateKey + data + privateKey)), the signature on every request and callback."""
    return sha1_base64(private_key + data + private_key)


def encode(private_key: str, json_text: str) -> Dict[str, str]:
    """JSON text, then base64, then signed: what the adapter posts."""
    data = base64.b64encode(json_text.encode("utf-8")).decode("ascii")
    return {"data": data, "signature": signature(private_key, data)}


def map_status(status: Optional[str]) -> str:
    """Maps a payment status."""
    if status not in _STATUSES:
        raise ManatError(PROVIDER_ERROR, f'epoint: unknown status "{status}"', provider=NAME)
    return _STATUSES[status]


class Epoint:
    name = NAME
    capabilities = frozenset({"createPayment", "verifyCallback", "getStatus", "refund"})

    def __init__(self, public_key: str, private_key: str, *, base_url: Optional[str] = None, transport: Optional[Transport] = None,
                 timeout: float = 30.0) -> None:
        if not public_key:
            raise ManatError(INVALID_REQUEST, "epoint: public_key is required")
        if not private_key:
            raise ManatError(INVALID_REQUEST, "epoint: private_key is required")
        self._public_key = public_key
        self._private_key = private_key
        self._base_url = (base_url or "https://epoint.az/api/1").rstrip("/")
        self._http = Http(NAME, transport, timeout)

    def create_payment(self, request: CreatePaymentRequest) -> CreatePaymentResult:
        if request.intent == "authorize":
            raise ManatError(UNSUPPORTED_OPERATION, "epoint: pre-authorisation is not supported (Epoint statuses do not tell a hold from a capture)",
                             provider=NAME)
        if request.amount.currency not in _CURRENCIES:
            raise ManatError(INVALID_REQUEST, f"epoint: currency {request.amount.currency} is not supported (AZN, USD, EUR, RUB)")
        if len(request.order_id) > 255:
            raise ManatError(INVALID_REQUEST, "epoint: order_id is longer than 255 characters")
        payload = {
            "public_key": self._public_key,
            "amount": request.amount.to_decimal_string(),
            "currency": request.amount.currency,
            "language": request.language or "az",
            "order_id": request.order_id,
        }
        if request.description:
            payload["description"] = request.description[:1000]
        payload["success_redirect_url"] = request.return_url
        payload["error_redirect_url"] = request.return_url
        response = self._call("request", payload)
        if response.get("status") != "success" or not truthy(response.get("redirect_url")) or not truthy(response.get("transaction")):
            raise _error("payment was not created", response)
        return CreatePaymentResult(text(response["transaction"]), "pending", NextAction("redirect", text(response["redirect_url"])), response)

    def verify_callback(self, callback: IncomingCallback) -> PaymentEvent:
        data = callback.body_field("data")
        sig = callback.body_field("signature")
        # An empty data still has a valid signature (sha1(key + key)), so it is rejected outright.
        if not data or not sig:
            raise ManatError(INVALID_SIGNATURE, "epoint: callback has no data/signature (the browser redirect is not signed; use get_status)",
                             provider=NAME, raw=callback.body)
        if not safe_equals(sig, signature(self._private_key, data)):
            raise ManatError(INVALID_SIGNATURE, "epoint: callback signature does not match", provider=NAME, raw=callback.body)
        payload = _decode_data(data)
        if not isinstance(payload, dict) or not truthy(payload.get("transaction")):
            raise ManatError(PROVIDER_ERROR, "epoint: signed callback has no transaction", provider=NAME, raw=payload)
        order_id = payload.get("order_id")
        return PaymentEvent(text(payload["transaction"]), map_status(payload.get("status")), text(order_id) if truthy(order_id) else None, raw=payload)

    def get_status(self, payment_id: str) -> PaymentState:
        response = self._call("get-status", {"public_key": self._public_key, "transaction": payment_id})
        # server_error means Epoint could not check the payment with the bank; the outcome is unknown.
        if response.get("status") == "server_error" or not truthy(response.get("transaction")):
            raise _error("status is not available", response)
        if response["transaction"] != payment_id:
            raise _error(f"status is for transaction {text(response['transaction'])}, not {payment_id}", response)
        return PaymentState(payment_id, map_status(response.get("status")), raw=response)

    def refund(self, request: RefundRequest) -> RefundResult:
        payload: dict = {"public_key": self._public_key, "language": "az", "transaction": request.payment_id}
        if request.amount is not None:
            payload["amount"] = request.amount.to_decimal_string()
        payload["currency"] = request.amount.currency if request.amount is not None else "AZN"
        response = self._call("reverse", payload)
        if response.get("status") != "success":
            raise _error("refund failed", response)
        state = self.get_status(request.payment_id)
        # Epoint has no partial-refund status: a reversed payment still reported as "success" was partly refunded.
        status = "partially_refunded" if state.status == "paid" else state.status
        return RefundResult(request.payment_id, status, raw={"refund": response, "payment": state.raw})

    def _call(self, method: str, payload: dict) -> dict:
        status, body = self._http.json("POST", f"{self._base_url}/{method}", None, None, encode(self._private_key, _json.stringify(payload)))
        if not isinstance(body, dict):
            raise ManatError(PROVIDER_ERROR, f"epoint: unexpected response (HTTP {status})", provider=NAME, raw=body)
        if status >= 400:
            raise _error(f"HTTP {status}", body)
        return body


def _error(what: str, response: dict) -> ManatError:
    message, code = response.get("message"), response.get("code")
    return ManatError(PROVIDER_ERROR, f"epoint: {text(message) if message is not None else what}", provider=NAME,
                      provider_code=text(code) if truthy(code) else None, provider_message=text(message) if truthy(message) else None,
                      raw=response)


def _decode_data(data: str) -> Any:
    if not _BASE64.match(data):
        raise ManatError(PROVIDER_ERROR, "epoint: callback data is not base64", provider=NAME, raw=data)
    try:
        return _json.parse(base64.b64decode(data, validate=True).decode("utf-8"))
    except (ValueError, binascii.Error) as error:
        raise ManatError(PROVIDER_ERROR, "epoint: callback data is not JSON", provider=NAME, raw=data) from error


