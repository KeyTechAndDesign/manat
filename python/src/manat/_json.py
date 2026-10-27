from __future__ import annotations

import json
from decimal import Decimal
from typing import Any

from .money import _plain


def parse(text: str) -> Any:
    """Objects become dicts, decimals stay exact (Decimal). Raises ValueError for text that is not JSON."""
    return json.loads(text, parse_float=Decimal)


def stringify(value: Any) -> str:
    """Like JavaScript's JSON.stringify: compact, "/" and non-ASCII unescaped, so signed payloads match every language."""
    if value is None:
        return "null"
    if value is True:
        return "true"
    if value is False:
        return "false"
    if isinstance(value, str):
        return json.dumps(value, ensure_ascii=False)
    if isinstance(value, int):
        return str(value)
    if isinstance(value, Decimal):
        return _plain(value)
    if isinstance(value, float):
        return "null" if value != value or value in (float("inf"), float("-inf")) else repr(value)
    if isinstance(value, dict):
        return "{" + ",".join(f"{stringify(str(key))}:{stringify(item)}" for key, item in value.items()) + "}"
    if isinstance(value, (list, tuple)):
        return "[" + ",".join(stringify(item) for item in value) + "]"
    raise TypeError(f"cannot write {type(value).__name__} as JSON")
