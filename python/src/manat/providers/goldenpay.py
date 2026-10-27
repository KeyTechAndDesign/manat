"""GoldenPay. AZN only.
The redirect back is unsigned, so verify_callback re-reads the payment."""

from __future__ import annotations

import re
from typing import Any, Optional
from urllib.parse import quote

from .. import _form
from .._crypto import md5_hex
from .._http import Http, Transport
from .._values import get, integer, text, truthy
from ..errors import INVALID_REQUEST, INVALID_SIGNATURE, PROVIDER_ERROR, UNSUPPORTED_OPERATION, ManatError
from ..money import Money
from ..types import CreatePaymentRequest, CreatePaymentResult, IncomingCallback, NextAction, PaymentEvent, PaymentState, RefundRequest, RefundResult

NAME = "goldenpay"
_BASE_URLS = {"test": "https://rest-newpgtest.goldenpay.az", "production": "https://rest-pg.goldenpay.az"}
_SUCCESS = 1
_REVERSED = 817
_PAYMENT_CODES = {_SUCCESS, 813, 814, 815, 816, _REVERSED, 818, 819}  # others are request errors
_MAX_DESCRIPTION_BYTES = 500
_PAYMENT_KEY = re.compile(r"^[A-Za-z0-9-]{1,64}$")


def map_status(code: Optional[int]) -> str:
    if code == _SUCCESS:
        return "paid"
    if code in (813, 818):  # not finished / in progress
        return "pending"
    if code in (814, 819):  # cancelled by the customer / payment page timed out
        return "cancelled"
    if code in (815, 816):
        return "failed"
    if code == _REVERSED:
        return "refunded"
    raise ManatError(PROVIDER_ERROR, f"goldenpay: status code {code} does not describe a payment", provider=NAME)


class GoldenPay:
    name = NAME
    capabilities = frozenset({"createPayment", "verifyCallback", "getStatus", "refund"})

    def __init__(self, auth_key: str, merchant_name: str, *, environment: str = "production", base_url: Optional[str] = None,
                 transport: Optional[Transport] = None, timeout: float = 30.0) -> None:
        if not auth_key:
            raise ManatError(INVALID_REQUEST, "goldenpay: auth_key is required")
        if not merchant_name:
            raise ManatError(INVALID_REQUEST, "goldenpay: merchant_name is required")
        self._auth_key = auth_key
        self._merchant_name = merchant_name
        self._base_url = (base_url or _BASE_URLS[environment]).rstrip("/")
        self._http = Http(NAME, transport, timeout)

    def create_payment(self, request: CreatePaymentRequest) -> CreatePaymentResult:
        if request.intent == "authorize":
            raise ManatError(UNSUPPORTED_OPERATION, "goldenpay: pre-authorisation is not supported", provider=NAME)
        _require_azn(request.amount)
        # GoldenPay has no order id field; the description is the only text that reaches its panel.
        description = _truncate_bytes(request.description if request.description is not None else request.order_id, _MAX_DESCRIPTION_BYTES)
        amount = request.amount.amount
        body = {
            "merchantName": self._merchant_name,
            "amount": amount,
            "lang": request.language or "az",
            "description": description,
            "redirectUrl": request.return_url,
            "hashCode": md5_hex(f"{self._auth_key}{self._merchant_name}{amount}{description}"),
        }
        response = self._call("POST", "getPaymentKey", body)
        if integer(get(response, "status", "code")) != _SUCCESS or not truthy(response.get("paymentKey")):
            raise _error("payment was not created", response)
        key = text(response["paymentKey"])
        return CreatePaymentResult(key, "pending", NextAction("redirect", f"{self._base_url}/pay/{quote(key, safe='')}"), response)

    def verify_callback(self, callback: IncomingCallback) -> PaymentEvent:
        key = (callback.query_value("payment_key") or callback.query_value("paymentKey")
               or callback.body_field("payment_key") or callback.body_field("paymentKey"))
        if key is None or not _PAYMENT_KEY.match(key):
            raise ManatError(INVALID_SIGNATURE, "goldenpay: redirect has no payment_key", provider=NAME, raw=callback.to_dict())
        # The redirect is unsigned; trust only what getPaymentResult says about this key.
        return PaymentEvent.from_state(self.get_status(key))

    def get_status(self, payment_id: str) -> PaymentState:
        if not _PAYMENT_KEY.match(payment_id):
            raise ManatError(INVALID_REQUEST, f'goldenpay: invalid payment_id "{payment_id}"')
        query = _form.encode({"payment_key": payment_id, "hash_code": md5_hex(self._auth_key + payment_id)})
        response = self._call("GET", f"getPaymentResult?{query}")
        code = integer(get(response, "status", "code"))
        if code not in _PAYMENT_CODES:
            raise _error("status is not available", response)
        key = response.get("paymentKey")
        if truthy(key) and key != payment_id:
            raise _error(f"result is for payment {text(key)}, not {payment_id}", response)
        amount = integer(response.get("amount"))
        return PaymentState(payment_id, map_status(code), Money(amount, "AZN") if amount and amount > 0 else None, raw=response)

    def refund(self, request: RefundRequest) -> RefundResult:
        if request.amount is not None:
            _require_azn(request.amount)
        # reversePaymentKey needs an amount, and GoldenPay has no partial-refund status, so the payment is read first.
        payment = self.get_status(request.payment_id)
        amount = request.amount or payment.amount
        if amount is None:
            raise ManatError(PROVIDER_ERROR, f"goldenpay: payment {request.payment_id} has no amount to refund", provider=NAME, raw=payment.raw)
        hash_code = md5_hex(self._auth_key + request.payment_id)
        response = self._call("POST", "reversePaymentKey", {
            "paymentKey": request.payment_id,
            "hashCode": hash_code,
            "amount": amount.amount,
            "secretKey": md5_hex(f"{hash_code}{amount.amount}"),
        })
        # 817 (reversed) and 1 both mean success.
        if integer(get(response, "status", "code")) not in (_REVERSED, _SUCCESS):
            raise _error("refund failed", response)
        partial = payment.amount is not None and amount.amount < payment.amount.amount
        return RefundResult(request.payment_id, "partially_refunded" if partial else "refunded", raw={"refund": response, "payment": payment.raw})

    def _call(self, method: str, path: str, body: Any = None) -> dict:
        status, data = self._http.json(method, f"{self._base_url}/{path}", None, body)
        if not isinstance(data, dict) or not truthy(data.get("status")):
            raise ManatError(PROVIDER_ERROR, f"goldenpay: unexpected response (HTTP {status})", provider=NAME, raw=data)
        if status >= 400:
            raise _error(f"HTTP {status}", data)
        return data


def _error(what: str, response: dict) -> ManatError:
    code = get(response, "status", "code")
    message = text(get(response, "status", "message"))
    # Code 1 is GoldenPay saying "success" (e.g. a result for another key), so only `what` describes the problem.
    from_provider = code is not None and integer(code) != _SUCCESS
    has_message = from_provider and bool(message)
    return ManatError(PROVIDER_ERROR, f"goldenpay: {message if has_message else what}", provider=NAME,
                      provider_code=text(code) if from_provider else None, provider_message=message if has_message else None, raw=response)


def _require_azn(money: Money) -> None:
    if money.currency != "AZN":
        raise ManatError(INVALID_REQUEST, f"goldenpay: only AZN is supported, got {money.currency}")


def _truncate_bytes(value: str, limit: int) -> str:
    """Cuts to at most `limit` UTF-8 bytes without splitting a character."""
    out, size = [], 0
    for char in value:
        length = len(char.encode("utf-8"))
        if size + length > limit:
            break
        out.append(char)
        size += length
    return "".join(out)
