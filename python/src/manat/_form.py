from __future__ import annotations

from typing import Dict, Iterable, Mapping, Tuple, Union
from urllib.parse import unquote_plus

_SAFE = frozenset(b"abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789*-._")


def encode(fields: Union[Mapping[str, str], Iterable[Tuple[str, str]]]) -> str:
    """application/x-www-form-urlencoded, encoded like JavaScript's URLSearchParams."""
    pairs = fields.items() if isinstance(fields, Mapping) else fields
    return "&".join(f"{_escape(key)}={_escape(value)}" for key, value in pairs)


def decode(body: str) -> Dict[str, str]:
    """Later duplicates are ignored; malformed %-escapes are kept as text."""
    out: Dict[str, str] = {}
    for pair in (body or "").split("&"):
        if not pair:
            continue
        key, _, value = pair.partition("=")
        out.setdefault(unquote_plus(key), unquote_plus(value))
    return out


def _escape(text: str) -> str:
    out = []
    for byte in (text or "").encode("utf-8"):
        if byte in _SAFE:
            out.append(chr(byte))
        elif byte == 0x20:
            out.append("+")
        else:
            out.append(f"%{byte:02X}")
    return "".join(out)
