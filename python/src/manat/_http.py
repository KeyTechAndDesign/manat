from __future__ import annotations

import http.client
import urllib.error
import urllib.request
from dataclasses import dataclass
from typing import Any, Dict, Mapping, Optional, Protocol, Tuple

from . import _form, _json
from ._version import VERSION
from .errors import NETWORK_ERROR, ManatError


USER_AGENT = f"keytd-manat/{VERSION} (+https://github.com/KeyTechAndDesign/manat)"


@dataclass(frozen=True)
class HttpResponse:
    status: int
    text: str


class Transport(Protocol):
    """Sends one request. Raise OSError (or http.client.HTTPException) on a network failure."""

    def send(self, method: str, url: str, headers: Mapping[str, str], body: Optional[bytes], timeout: float) -> HttpResponse: ...


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args: Any, **kwargs: Any) -> None:
        return None


class UrllibTransport:
    """Default transport on urllib: TLS verified, redirects not followed."""

    def __init__(self) -> None:
        self._opener = urllib.request.build_opener(_NoRedirect)

    def send(self, method: str, url: str, headers: Mapping[str, str], body: Optional[bytes], timeout: float) -> HttpResponse:
        request = urllib.request.Request(url, data=body, headers=dict(headers), method=method)
        try:
            with self._opener.open(request, timeout=timeout) as response:
                return HttpResponse(response.status, _decode(response.read(), response.headers.get_content_charset()))
        except urllib.error.HTTPError as error:
            return HttpResponse(error.code, _decode(error.read(), error.headers.get_content_charset() if error.headers else None))


class Http:
    """Encodes bodies, applies the timeout and turns failures into ManatError for one adapter."""

    def __init__(self, provider: str, transport: Optional[Transport], timeout: float) -> None:
        self.provider = provider
        self.transport: Transport = transport or UrllibTransport()
        self.timeout = timeout

    def send(self, method: str, url: str, headers: Optional[Mapping[str, str]] = None, json: Any = None,
             form: Optional[Mapping[str, str]] = None) -> HttpResponse:
        lower: Dict[str, str] = {name.lower(): value for name, value in (headers or {}).items()}
        lower.setdefault("user-agent", USER_AGENT)
        body: Optional[bytes] = None
        if form is not None:
            body = _form.encode(form).encode("utf-8")
            lower.setdefault("content-type", "application/x-www-form-urlencoded")
        elif json is not None:
            body = _json.stringify(json).encode("utf-8")
            lower.setdefault("content-type", "application/json")
        try:
            return self.transport.send(method, url, lower, body, self.timeout)
        except ManatError:
            raise
        except (OSError, http.client.HTTPException) as error:
            raise ManatError(NETWORK_ERROR, f"{self.provider}: {method} {url} {error}", provider=self.provider) from error

    def json(self, method: str, url: str, headers: Optional[Mapping[str, str]] = None, json: Any = None,
             form: Optional[Mapping[str, str]] = None) -> Tuple[int, Any]:
        response = self.send(method, url, {"accept": "application/json", **{k.lower(): v for k, v in (headers or {}).items()}}, json, form)
        if not response.text:
            return response.status, None
        try:
            return response.status, _json.parse(response.text)
        except ValueError as error:
            raise ManatError(NETWORK_ERROR, f'{self.provider}: expected JSON, got HTTP {response.status} "{response.text[:200]}"',
                             provider=self.provider, raw=response.text) from error


def _decode(data: bytes, charset: Optional[str]) -> str:
    try:
        return data.decode(charset or "utf-8")
    except (LookupError, UnicodeDecodeError):
        return data.decode("utf-8", errors="replace")
