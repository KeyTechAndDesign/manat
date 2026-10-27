from __future__ import annotations

import re
from dataclasses import dataclass
from decimal import Decimal
from typing import Optional, Union

from .errors import INVALID_REQUEST, ManatError

CURRENCIES = ("AZN", "USD", "EUR", "RUB", "TRY", "GBP")
_NUMERIC = {"AZN": "944", "USD": "840", "EUR": "978", "RUB": "643", "TRY": "949", "GBP": "826"}
_DECIMAL = re.compile(r"^(\d+)(?:\.(\d+))?$")
_DIGITS = 2


@dataclass(frozen=True)
class Money:
    """An amount in minor units: ``Money(2550, "AZN")`` is 25.50 manat. Floats are never accepted."""

    amount: int
    currency: str

    def __post_init__(self) -> None:
        if self.currency not in CURRENCIES:
            raise ManatError(INVALID_REQUEST, f'currency "{self.currency}" is not supported')
        if isinstance(self.amount, bool) or not isinstance(self.amount, int):
            raise ManatError(INVALID_REQUEST, f"amount must be an integer in minor units (2550 = 25.50), got {self.amount!r}")
        if self.amount < 0:
            raise ManatError(INVALID_REQUEST, f"amount must not be negative, got {self.amount}")

    def to_decimal_string(self) -> str:
        """2550 → "25.50"."""
        units = str(self.amount).rjust(_DIGITS + 1, "0")
        return f"{units[:-_DIGITS]}.{units[-_DIGITS:]}"

    def to_number(self) -> Decimal:
        """2550 → 25.5 and 100 → 1, for JSON APIs that take amounts as numbers."""
        return Decimal(self.amount).scaleb(-_DIGITS).normalize()

    def numeric_currency(self) -> str:
        return _NUMERIC[self.currency]

    def to_dict(self) -> dict:
        return {"amount": self.amount, "currency": self.currency}


def from_decimal(value: Union[str, int, Decimal], currency: str) -> Money:
    """"25.5" / "25.50" / 25 → 2550. Rejects more decimals than the currency has."""
    if isinstance(value, Decimal):
        text = _plain(value)
    elif isinstance(value, int) and not isinstance(value, bool):
        text = str(value)
    elif isinstance(value, str):
        text = value.strip()
    else:
        raise ManatError(INVALID_REQUEST, f"{value!r} is not a valid {currency} amount (use a string or Decimal, not a float)")
    match = _DECIMAL.match(text)
    if not match or len(match.group(2) or "") > _DIGITS:
        raise ManatError(INVALID_REQUEST, f'"{text}" is not a valid {currency} amount')
    return Money(int(match.group(1) + (match.group(2) or "").ljust(_DIGITS, "0")), currency)


def azn(value: Union[str, int, Decimal]) -> Money:
    """``azn("25.50")`` is 2550 AZN."""
    return from_decimal(value, "AZN")


def currency_from_numeric(code: str) -> Optional[str]:
    for letters, numeric in _NUMERIC.items():
        if numeric == code:
            return letters
    return None


def _plain(value: Decimal) -> str:
    """25.50 → "25.5", 1E+1 → "10": the decimal as JavaScript would print the number."""
    if value == value.to_integral_value():
        return str(value.quantize(Decimal(1)))
    return format(value.normalize(), "f")
