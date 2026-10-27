from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Dict, List, Mapping, Optional, Union

from .money import Money

#: The statuses every adapter maps its provider's codes onto.
STATUSES = ("pending", "authorized", "paid", "failed", "cancelled", "refunded", "partially_refunded")
CAPABILITIES = ("createPayment", "verifyCallback", "getStatus", "refund", "capture", "cancel")


@dataclass(frozen=True)
class CreatePaymentRequest:
    order_id: str
    amount: Money
    return_url: str
    callback_url: Optional[str] = None
    description: Optional[str] = None
    language: Optional[str] = None  # "az", "en" or "ru"
    intent: str = "purchase"  # or "authorize"
    customer_email: Optional[str] = None
    customer_phone: Optional[str] = None
    metadata: Dict[str, str] = field(default_factory=dict)


@dataclass(frozen=True)
class RefundRequest:
    payment_id: str
    amount: Optional[Money] = None  # None refunds everything
    provider_ref: Optional[str] = None


@dataclass(frozen=True)
class PaymentAction:
    payment_id: str
    amount: Optional[Money] = None
    provider_ref: Optional[str] = None


@dataclass(frozen=True)
class IncomingCallback:
    """What the merchant's HTTP handler received. ``body`` is the raw text, or a dict your framework parsed."""

    method: str
    query: Mapping[str, Union[str, List[str]]] = field(default_factory=dict)
    body: Union[str, Mapping[str, Any], None] = None
    headers: Mapping[str, Union[str, List[str]]] = field(default_factory=dict)

    def query_value(self, name: str) -> Optional[str]:
        return _first(self.query.get(name))

    def body_field(self, name: str) -> Optional[str]:
        from ._form import decode

        if isinstance(self.body, str):
            return decode(self.body).get(name)
        if isinstance(self.body, Mapping):
            return _first(self.body.get(name))
        return None

    def to_dict(self) -> dict:
        return {"method": self.method, "query": dict(self.query), "body": self.body, "headers": dict(self.headers)}


@dataclass(frozen=True)
class NextAction:
    """Where to send the customer: ``redirect`` to ``url``, auto-submit ``fields`` to ``url`` (``form``), or ``none``."""

    type: str
    url: Optional[str] = None
    fields: Dict[str, str] = field(default_factory=dict)

    @property
    def method(self) -> Optional[str]:
        return "POST" if self.type == "form" else None

    def to_dict(self) -> dict:
        if self.type == "redirect":
            return {"type": "redirect", "url": self.url}
        if self.type == "form":
            return {"type": "form", "url": self.url, "method": "POST", "fields": dict(self.fields)}
        return {"type": self.type}


@dataclass(frozen=True)
class CreatePaymentResult:
    payment_id: str
    status: str
    next: NextAction
    raw: Any = None

    def to_dict(self) -> dict:
        return {"paymentId": self.payment_id, "status": self.status, "next": self.next.to_dict(), "raw": self.raw}


@dataclass(frozen=True)
class PaymentState:
    payment_id: str
    status: str
    amount: Optional[Money] = None
    refunded_amount: Optional[Money] = None
    provider_ref: Optional[str] = None
    raw: Any = None

    def to_dict(self) -> dict:
        out: dict = {"paymentId": self.payment_id, "status": self.status}
        if self.amount is not None:
            out["amount"] = self.amount.to_dict()
        if self.refunded_amount is not None:
            out["refundedAmount"] = self.refunded_amount.to_dict()
        if self.provider_ref is not None:
            out["providerRef"] = self.provider_ref
        out["raw"] = self.raw
        return out


@dataclass(frozen=True)
class PaymentEvent:
    payment_id: str
    status: str
    order_id: Optional[str] = None
    amount: Optional[Money] = None
    provider_ref: Optional[str] = None  # store it with the order; refunds and captures need it (Azericard)
    raw: Any = None

    @staticmethod
    def from_state(state: PaymentState) -> "PaymentEvent":
        return PaymentEvent(state.payment_id, state.status, amount=state.amount, raw=state.raw)

    def to_dict(self) -> dict:
        out: dict = {"paymentId": self.payment_id}
        if self.order_id is not None:
            out["orderId"] = self.order_id
        out["status"] = self.status
        if self.amount is not None:
            out["amount"] = self.amount.to_dict()
        if self.provider_ref is not None:
            out["providerRef"] = self.provider_ref
        out["raw"] = self.raw
        return out


@dataclass(frozen=True)
class RefundResult:
    payment_id: str
    status: str
    refund_id: Optional[str] = None
    raw: Any = None

    def to_dict(self) -> dict:
        out: dict = {"paymentId": self.payment_id}
        if self.refund_id is not None:
            out["refundId"] = self.refund_id
        out["status"] = self.status
        out["raw"] = self.raw
        return out


def _first(value: Any) -> Optional[str]:
    if isinstance(value, (list, tuple)):
        value = value[0] if value else None
    return value if isinstance(value, str) else None
