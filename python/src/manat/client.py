from __future__ import annotations

from typing import Any, FrozenSet, List, Mapping, Optional, Union
from urllib.parse import urlsplit

from .errors import INVALID_REQUEST, UNSUPPORTED_OPERATION, ManatError
from .money import Money
from .types import (CreatePaymentRequest, CreatePaymentResult, IncomingCallback, PaymentAction, PaymentEvent, PaymentState,
                    RefundRequest, RefundResult)


class Manat:
    """Provider-neutral facade. Validates input once, then delegates to the adapter.

        pay = Manat(KapitalBank(username=..., password=...))
        result = pay.create_payment(order_id="A-1001", amount=azn("25.50"), return_url="https://shop.az/return")
        return redirect(result.next.url)
    """

    def __init__(self, provider: Any) -> None:
        if provider is None:
            raise ManatError(INVALID_REQUEST, "provider is required")
        self.provider = provider

    @property
    def capabilities(self) -> FrozenSet[str]:
        return self.provider.capabilities

    def supports(self, capability: str) -> bool:
        return capability in self.provider.capabilities

    def create_payment(self, order_id: str, amount: Money, return_url: str, *, callback_url: Optional[str] = None,
                       description: Optional[str] = None, language: Optional[str] = None, intent: str = "purchase",
                       customer_email: Optional[str] = None, customer_phone: Optional[str] = None,
                       metadata: Optional[Mapping[str, str]] = None) -> CreatePaymentResult:
        if not isinstance(order_id, str) or not order_id:
            raise ManatError(INVALID_REQUEST, "order_id is required")
        _require_money(amount)
        _require_url(return_url, "return_url")
        if callback_url is not None:
            _require_url(callback_url, "callback_url")
        return self.provider.create_payment(CreatePaymentRequest(
            order_id, amount, return_url, callback_url, description, language, intent, customer_email, customer_phone, dict(metadata or {})))

    def verify_callback(self, method: str, *, query: Optional[Mapping[str, Union[str, List[str]]]] = None,
                        body: Union[str, Mapping[str, Any], None] = None,
                        headers: Optional[Mapping[str, Union[str, List[str]]]] = None) -> PaymentEvent:
        """Authenticates a callback or redirect. Pass the raw body when you have it. An unsigned redirect is never
        trusted on its own: the adapter re-reads the payment from the provider."""
        if not isinstance(method, str):
            raise ManatError(INVALID_REQUEST, "callback method is required")
        lower = {str(name).lower(): value for name, value in (headers or {}).items()}
        return self.provider.verify_callback(IncomingCallback(method, dict(query or {}), body, lower))

    def get_status(self, payment_id: str) -> PaymentState:
        _require_payment_id(payment_id)
        return self._call("getStatus", "get_status", payment_id)

    def refund(self, payment_id: str, *, amount: Optional[Money] = None, provider_ref: Optional[str] = None) -> RefundResult:
        """No amount refunds everything."""
        _require_payment_id(payment_id)
        if amount is not None:
            _require_money(amount)
        return self._call("refund", "refund", RefundRequest(payment_id, amount, provider_ref))

    def capture(self, payment_id: str, *, amount: Optional[Money] = None, provider_ref: Optional[str] = None) -> PaymentState:
        """Completes a payment made with intent="authorize"."""
        _require_payment_id(payment_id)
        if amount is not None:
            _require_money(amount)
        return self._call("capture", "capture", PaymentAction(payment_id, amount, provider_ref))

    def cancel(self, payment_id: str, *, provider_ref: Optional[str] = None) -> PaymentState:
        _require_payment_id(payment_id)
        return self._call("cancel", "cancel", PaymentAction(payment_id, None, provider_ref))

    def _call(self, capability: str, method: str, argument: Any) -> Any:
        if capability not in self.provider.capabilities or not hasattr(self.provider, method):
            name = self.provider.name
            raise ManatError(UNSUPPORTED_OPERATION, f"{name} does not support {capability}", provider=name)
        return getattr(self.provider, method)(argument)


def _require_payment_id(payment_id: Any) -> None:
    if not isinstance(payment_id, str) or not payment_id:
        raise ManatError(INVALID_REQUEST, "payment_id is required")


def _require_money(amount: Any) -> None:
    if not isinstance(amount, Money):
        raise ManatError(INVALID_REQUEST, "amount is required (a Money, e.g. azn('25.50'))")
    if amount.amount < 1:
        raise ManatError(INVALID_REQUEST, f"amount must be a positive integer in minor units (2550 = 25.50), got {amount.amount}")


def _require_url(value: Any, field: str) -> None:
    parts = urlsplit(value) if isinstance(value, str) else None
    if parts is None or parts.scheme.lower() not in ("http", "https") or not parts.netloc:
        raise ManatError(INVALID_REQUEST, f"{field} must be an absolute http(s) URL")
