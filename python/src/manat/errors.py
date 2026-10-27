from __future__ import annotations

from typing import Any, Optional

INVALID_REQUEST = "invalid_request"
INVALID_SIGNATURE = "invalid_signature"
PROVIDER_ERROR = "provider_error"
NETWORK_ERROR = "network_error"
UNSUPPORTED_OPERATION = "unsupported_operation"


class ManatError(Exception):
    """Every error the library raises. ``code`` is one of the constants in this module."""

    def __init__(
        self,
        code: str,
        message: str,
        *,
        provider: Optional[str] = None,
        provider_code: Optional[str] = None,
        provider_message: Optional[str] = None,
        raw: Any = None,
    ) -> None:
        super().__init__(message)
        self.code = code
        self.provider = provider
        self.provider_code = provider_code
        self.provider_message = provider_message
        self.raw = raw


def is_manat_error(error: object, code: Optional[str] = None) -> bool:
    return isinstance(error, ManatError) and (code is None or error.code == code)
