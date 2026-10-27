"""One API for Azerbaijani online payment providers."""

from .client import Manat
from .errors import ManatError, is_manat_error
from .money import Money, azn, from_decimal
from .types import CreatePaymentResult, IncomingCallback, NextAction, PaymentEvent, PaymentState, RefundResult

__all__ = [
    "Manat", "ManatError", "is_manat_error", "Money", "azn", "from_decimal",
    "CreatePaymentResult", "IncomingCallback", "NextAction", "PaymentEvent", "PaymentState", "RefundResult",
]
from ._version import VERSION as __version__  # noqa: E402
