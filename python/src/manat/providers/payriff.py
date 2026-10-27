"""Payriff (API v3).
The webhook is unsigned, so verify_callback re-reads the order. There is no void for a pre-auth."""

from __future__ import annotations

import re
from typing import Any, Optional

from .. import _json
from .._http import Http, Transport
from .._values import get, money, text, truthy
from ..errors import INVALID_REQUEST, INVALID_SIGNATURE, PROVIDER_ERROR, ManatError
from ..money import CURRENCIES
from ..types import (CreatePaymentRequest, CreatePaymentResult, IncomingCallback, NextAction, PaymentAction, PaymentEvent,
                     PaymentState, RefundRequest, RefundResult)

NAME = "payriff"
_SUCCESS = "00000"
_ORDER_ID = re.compile(r"^[A-Za-z0-9-]{1,64}$")
_STATUSES = {
    "CREATED": "pending", "PENDING": "pending", "ACCEPTED": "pending", "IN_REVIEW": "pending",
    "APPROVED": "paid", "PAID": "paid", "PARTIAL": "paid", "CASH": "paid",  # PARTIAL: pre-auth partially captured
    "PREAUTH_APPROVED": "authorized",
    "REFUNDED": "refunded", "REFUND_IN_PROGRESS": "refunded",
    "PARTIAL_REFUND": "partially_refunded",
    "CANCELED": "cancelled", "EXPIRED": "cancelled", "REVERSE": "cancelled", "PREAUTH_EXPIRED": "cancelled",
    "DECLINED": "failed",
}


def map_status(status: Optional[str]) -> str:
    """Maps payload.paymentStatus."""
    if status not in _STATUSES:
        raise ManatError(PROVIDER_ERROR, f'payriff: unknown paymentStatus "{status}"', provider=NAME)
    return _STATUSES[status]


class Payriff:
    name = NAME
    capabilities = frozenset({"createPayment", "verifyCallback", "getStatus", "refund", "capture"})

    def __init__(self, secret_key: str, *, base_url: Optional[str] = None, transport: Optional[Transport] = None,
                 timeout: float = 30.0) -> None:
        if not secret_key:
            raise ManatError(INVALID_REQUEST, "payriff: secret_key is required")
        self._secret_key = secret_key
        self._base_url = (base_url or "https://api.payriff.com").rstrip("/")
        self._http = Http(NAME, transport, timeout)

    def create_payment(self, request: CreatePaymentRequest) -> CreatePaymentResult:
        if request.callback_url is not None and not request.callback_url.startswith("https://"):
            raise ManatError(INVALID_REQUEST, "payriff: callback_url must be an HTTPS URL")
        body = {
            "amount": request.amount.to_number(),
            "currency": request.amount.currency,
            "language": (request.language or "az").upper(),
            "operation": "PRE_AUTH" if request.intent == "authorize" else "PURCHASE",
            "description": request.description if request.description is not None else f"Order {request.order_id}",
            "redirectUrl": request.return_url,
        }
        if request.callback_url:
            body["callbackUrl"] = request.callback_url
        if request.metadata:
            body["metadata"] = dict(request.metadata)
        payload = self._call("POST", "/api/v3/orders", body).get("payload")
        if not isinstance(payload, dict) or not truthy(payload.get("orderId")) or not truthy(payload.get("paymentUrl")):
            raise ManatError(PROVIDER_ERROR, "payriff: order response has no orderId or paymentUrl", provider=NAME, raw=payload)
        return CreatePaymentResult(text(payload["orderId"]), "pending", NextAction("redirect", text(payload["paymentUrl"])), payload)

    def verify_callback(self, callback: IncomingCallback) -> PaymentEvent:
        body: Any = callback.body
        if isinstance(body, str):
            try:
                body = _json.parse(body)
            except ValueError:
                body = None
        order_id = get(body, "payload", "orderId")
        if not isinstance(order_id, str):
            order_id = callback.query_value("orderId")
        if not order_id:
            raise ManatError(INVALID_SIGNATURE, "payriff: callback has no payload.orderId", provider=NAME, raw=callback.body)
        # The webhook is unsigned; trust only what the API says about this order.
        return PaymentEvent.from_state(self.get_status(order_id))

    def get_status(self, payment_id: str) -> PaymentState:
        if not _ORDER_ID.match(payment_id):
            raise ManatError(INVALID_REQUEST, f'payriff: invalid payment_id "{payment_id}"')
        order = self._call("GET", f"/api/v3/orders/{payment_id}").get("payload")
        if not isinstance(order, dict) or not truthy(order.get("orderId")):
            raise ManatError(PROVIDER_ERROR, f"payriff: no order in response for {payment_id}", provider=NAME, raw=order)
        amount = None
        if order.get("amount") is not None and truthy(order.get("currencyType")):
            if order["currencyType"] not in CURRENCIES:
                raise ManatError(PROVIDER_ERROR, f'payriff: unsupported currency "{order["currencyType"]}"', provider=NAME)
            amount = money(order["amount"], order["currencyType"])
        return PaymentState(text(order["orderId"]), _order_status(order), amount, raw=order)

    def refund(self, request: RefundRequest) -> RefundResult:
        # Payriff rejects a refund without an amount ("Request refund amount is null"), so a full refund sends the order amount.
        amount = request.amount or self.get_status(request.payment_id).amount
        if amount is None:
            raise ManatError(PROVIDER_ERROR, f"payriff: order {request.payment_id} has no amount to refund", provider=NAME)
        envelope = self._call("POST", "/api/v3/refund", {"orderId": request.payment_id, "refundReason": "Refund", "amount": amount.to_number()})
        state = self.get_status(request.payment_id)
        response_id = envelope.get("responseId")
        return RefundResult(request.payment_id, state.status, text(response_id) if truthy(response_id) else None,
                            {"refund": envelope, "order": state.raw})

    def capture(self, action: PaymentAction) -> PaymentState:
        body: dict = {"orderId": action.payment_id}
        if action.amount is not None:
            body["amount"] = action.amount.to_number()
        self._call("POST", "/api/v3/complete", body)
        return self.get_status(action.payment_id)

    def _call(self, method: str, path: str, body: Any = None) -> dict:
        status, data = self._http.json(method, self._base_url + path, {"authorization": self._secret_key}, body)
        if not isinstance(data, dict) or "code" not in data:
            raise ManatError(PROVIDER_ERROR, f"payriff: unexpected response (HTTP {status})", provider=NAME, raw=data)
        if status >= 400 or data["code"] != _SUCCESS:
            code, message = data.get("code"), data.get("message")
            raise ManatError(PROVIDER_ERROR, f"payriff: {text(message) if message is not None else f'code {code}'}", provider=NAME,
                             provider_code=text(code) if truthy(code) else None, provider_message=text(message) if truthy(message) else None,
                             raw=data)
        return data


def _order_status(order: dict) -> str:
    """After a partial refund Payriff reports the order as REFUNDED; only its transaction says PARTIAL_REFUND."""
    status = map_status(order.get("paymentStatus"))
    transactions = order.get("transactions")
    if status == "refunded" and isinstance(transactions, list) and any(get(t, "status") == "PARTIAL_REFUND" for t in transactions):
        return "partially_refunded"
    return status
