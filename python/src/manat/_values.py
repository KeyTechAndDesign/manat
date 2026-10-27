from __future__ import annotations

from decimal import Decimal, InvalidOperation
from typing import Any, Optional

from .money import Money, _plain, from_decimal


def get(value: Any, *path: str) -> Any:
    """Walks dict keys: ``get(data, "order", "id")``; None when a step is missing."""
    for key in path:
        if not isinstance(value, dict):
            return None
        value = value.get(key)
    return value


def text(value: Any) -> Optional[str]:
    """JavaScript's String(value) for strings, numbers and booleans; None for anything else."""
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, str):
        return value
    if isinstance(value, int):
        return str(value)
    if isinstance(value, Decimal):
        return _plain(value)
    return None


def truthy(value: Any) -> bool:
    """JavaScript truthiness: everything but None, False, "" and 0 (an empty list or dict is truthy)."""
    if value is None or value is False or value == "":
        return False
    if isinstance(value, (int, Decimal)) and not isinstance(value, bool):
        return value != 0
    return True


def integer(value: Any) -> Optional[int]:
    """A JSON integer, or None (strings, decimals with a fraction)."""
    if isinstance(value, bool):
        return None
    if isinstance(value, int):
        return value
    if isinstance(value, Decimal) and value == value.to_integral_value():
        return int(value)
    return None


def number(value: Any) -> Optional[Decimal]:
    """JavaScript's Number(value) for numbers and numeric strings; None when it is not a number."""
    if isinstance(value, bool):
        return None
    if isinstance(value, (int, Decimal)):
        return Decimal(value)
    if isinstance(value, str):
        stripped = value.strip()
        if not stripped:
            return Decimal(0)
        try:
            return Decimal(stripped)
        except InvalidOperation:
            return None
    return None


def money(value: Any, currency: str) -> Money:
    """A decimal amount from JSON (number or string, "25.5" or 25.5)."""
    if isinstance(value, (int, Decimal)) and not isinstance(value, bool):
        return from_decimal(Decimal(value), currency)
    return from_decimal(text(value) or "", currency)
