"""Kapital Bank (TXPG REST API).
The redirect back (?ID=…&STATUS=…) is unsigned, so verify_callback re-reads the order."""

from __future__ import annotations

import base64
import re
from decimal import Decimal
from typing import Any, Optional

from .. import _form
from .._http import Http, Transport
from .._values import get, money, number, text, truthy
from ..errors import INVALID_REQUEST, INVALID_SIGNATURE, PROVIDER_ERROR, ManatError
from ..money import CURRENCIES, Money
from ..types import (CreatePaymentRequest, CreatePaymentResult, IncomingCallback, NextAction, PaymentAction, PaymentEvent,
                     PaymentState, RefundRequest, RefundResult)

_BASE_URLS = {"test": "https://txpgtst.kapitalbank.az/api", "production": "https://e-commerce.kapitalbank.az/api"}
_ORDER_ID = re.compile(r"^\d+$")
_BARE_ORIGIN = re.compile(r"^[a-z][a-z0-9+.-]*://[^/]+$", re.I)
NAME = "kapitalbank"


def map_status(order: dict) -> str:
    """Maps an order's status."""
    status = order.get("status")
    if status == "Preparing":
        return "pending"
    if status == "Authorized":
        return "authorized"
    if status in ("PartPaid", "FullyPaid", "Funded", "Closed"):
        refunded = Decimal(0) if order.get("clearedRefundAmount") is None else number(order.get("clearedRefundAmount"))
        if refunded is not None and refunded > 0:
            charged_raw = order.get("clearedChargeAmount")
            charged = None if charged_raw is None else number(charged_raw)
            full = charged_raw is None or (charged is not None and refunded >= charged)
            return "refunded" if full else "partially_refunded"
        return "paid"
    if status == "Refunded":
        return "refunded"
    if status in ("Cancelled", "Voided", "Expired"):
        return "cancelled"
    if status in ("Rejected", "Refused", "Declined"):
        return "failed"
    raise ManatError(PROVIDER_ERROR, f'kapitalbank: unknown order status "{status}"', provider=NAME, raw=order)



class KapitalBank:
    name = NAME
    capabilities = frozenset({"createPayment", "verifyCallback", "getStatus", "refund", "capture", "cancel"})

    def __init__(self, username: str, password: str, *, environment: str = "production", base_url: Optional[str] = None,
                 transport: Optional[Transport] = None, timeout: float = 30.0) -> None:
        if not username or not password:
            raise ManatError(INVALID_REQUEST, "kapitalbank: username and password are required")
        self._base_url = (base_url or _BASE_URLS[environment]).rstrip("/")
        self._auth = "Basic " + base64.b64encode(f"{username}:{password}".encode("utf-8")).decode("ascii")
        self._http = Http(NAME, transport, timeout)

    def create_payment(self, request: CreatePaymentRequest) -> CreatePaymentResult:
        order = {
            "typeRid": "Order_DMS" if request.intent == "authorize" else "Order_SMS",
            "amount": request.amount.to_decimal_string(),
            "currency": request.amount.currency,
            "language": request.language or "az",
            "description": request.description if request.description is not None else request.order_id,
            "hppRedirectUrl": request.return_url,
        }
        data = self._call("POST", "/order", {"order": order})
        created = data.get("order")
        if not isinstance(created, dict) or not truthy(created.get("id")) or not truthy(created.get("hppUrl")) or not truthy(created.get("password")):
            raise ManatError(PROVIDER_ERROR, "kapitalbank: order response is missing id, hppUrl or password", provider=NAME, raw=data)
        order_id = text(created["id"])
        url = _with_query(text(created["hppUrl"]), {"id": order_id, "password": text(created["password"])})
        status = map_status(created) if created.get("status") is not None else "pending"
        return CreatePaymentResult(order_id, status, NextAction("redirect", url), data)

    def verify_callback(self, callback: IncomingCallback) -> PaymentEvent:
        order_id = callback.query_value("ID") or callback.query_value("id") or callback.body_field("ID")
        if order_id is None or not _ORDER_ID.match(order_id):
            raise ManatError(INVALID_SIGNATURE, "kapitalbank: callback has no numeric ID", provider=NAME, raw=callback.to_dict())
        # The redirect is unsigned; the API is the source of truth.
        return PaymentEvent.from_state(self.get_status(order_id))

    def get_status(self, payment_id: str) -> PaymentState:
        return self._to_state(self._fetch_order(payment_id))

    def refund(self, request: RefundRequest) -> RefundResult:
        amount = request.amount or self._remaining_refundable(request.payment_id)
        data = self._exec(request.payment_id, {"phase": "Single", "amount": amount.to_decimal_string(), "type": "Refund"})
        state = self.get_status(request.payment_id)
        action_id = get(data, "tran", "match", "tranActionId")
        return RefundResult(request.payment_id, state.status, text(action_id) if truthy(action_id) else None, {"exec": data, "order": state.raw})

    def capture(self, action: PaymentAction) -> PaymentState:
        amount = action.amount or self.get_status(action.payment_id).amount
        if amount is None:
            raise ManatError(PROVIDER_ERROR, f"kapitalbank: order {action.payment_id} has no amount to capture", provider=NAME)
        self._exec(action.payment_id, {"phase": "Clearing", "amount": amount.to_decimal_string()})
        return self.get_status(action.payment_id)

    def cancel(self, action: PaymentAction) -> PaymentState:
        current = self.get_status(action.payment_id)
        # Reversal of a pre-auth hold uses phase Auth; a same-day purchase uses phase Single.
        self._exec(action.payment_id, {"phase": "Auth" if current.status == "authorized" else "Single", "voidKind": "Full"})
        return self.get_status(action.payment_id)

    def _remaining_refundable(self, payment_id: str) -> Money:
        order = self._fetch_order(payment_id)
        currency = _currency(order.get("currency"), payment_id)
        charged = order.get("clearedChargeAmount") if order.get("clearedChargeAmount") is not None else order.get("amount")
        remaining = money("0" if charged is None else charged, currency).amount
        if truthy(order.get("clearedRefundAmount")):
            remaining -= money(order["clearedRefundAmount"], currency).amount
        if remaining <= 0:
            raise ManatError(INVALID_REQUEST, f"kapitalbank: order {payment_id} has nothing left to refund", provider=NAME, raw=order)
        return Money(remaining, currency)

    def _fetch_order(self, payment_id: str) -> dict:
        _require_order_id(payment_id)
        data = self._call("GET", f"/order/{payment_id}?tranDetailLevel=2&tokenDetailLevel=2&orderDetailLevel=2")
        order = data.get("order")
        if not isinstance(order, dict):
            raise ManatError(PROVIDER_ERROR, f"kapitalbank: no order in response for {payment_id}", provider=NAME, raw=data)
        if order.get("id") is None:
            order["id"] = payment_id
        return order

    def _to_state(self, order: dict) -> PaymentState:
        order_id = text(order["id"])
        amount = refunded = None
        if order.get("amount") is not None and order.get("currency") is not None:
            amount = money(order["amount"], _currency(order["currency"], order_id))
        if truthy(order.get("clearedRefundAmount")) and order.get("currency") is not None:
            refunded = money(order["clearedRefundAmount"], _currency(order["currency"], order_id))
        return PaymentState(order_id, map_status(order), amount, refunded, None, order)

    def _exec(self, payment_id: str, tran: dict) -> dict:
        _require_order_id(payment_id)
        return self._call("POST", f"/order/{payment_id}/exec-tran", {"tran": tran})

    def _call(self, method: str, path: str, body: Any = None) -> dict:
        status, data = self._http.json(method, self._base_url + path, {"authorization": self._auth}, body)
        if status >= 400 or (isinstance(data, dict) and truthy(data.get("errorCode"))):
            code = text(data.get("errorCode")) if isinstance(data, dict) else None
            message = text(data.get("errorDescription")) if isinstance(data, dict) else None
            raise ManatError(PROVIDER_ERROR, f"kapitalbank: {message or f'HTTP {status}'}", provider=NAME, provider_code=code,
                             provider_message=message, raw=data)
        return data if isinstance(data, dict) else {}


def _require_order_id(payment_id: str) -> None:
    if not _ORDER_ID.match(payment_id):
        raise ManatError(INVALID_REQUEST, f'kapitalbank: payment_id must be the numeric order id, got "{payment_id}"')


def _currency(code: Any, payment_id: str) -> str:
    if code in CURRENCIES:
        return code
    raise ManatError(PROVIDER_ERROR, f'kapitalbank: unexpected currency "{code}" on order {payment_id}', provider=NAME)


def _with_query(url: str, params: dict) -> str:
    """Sets query parameters on a URL, replacing existing ones with the same name (like URL.searchParams.set)."""
    url, hash_mark, fragment = url.partition("#")
    base, _, query = url.partition("?")
    if _BARE_ORIGIN.match(base):
        base += "/"
    merged = {**_form.decode(query), **params}
    return f"{base}?{_form.encode(merged)}{hash_mark}{fragment}"
