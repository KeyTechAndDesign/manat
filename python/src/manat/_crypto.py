"""Hashes, constant-time comparison and RSA-SHA256 (PKCS#1 v1.5) on the standard library alone."""

from __future__ import annotations

import base64
import hashlib
import hmac
import math
import re
import secrets
from dataclasses import dataclass
from typing import List, Optional, Tuple

_PEM = re.compile(r"-----BEGIN ([A-Z0-9 ]+)-----([A-Za-z0-9+/=\s]+)-----END \1-----")
# DigestInfo prefix for SHA-256 (RFC 8017, section 9.2).
_SHA256_PREFIX = bytes.fromhex("3031300d060960864801650304020105000420")


def md5_hex(text: str) -> str:
    return hashlib.md5(text.encode("utf-8")).hexdigest()


def sha1_base64(text: str) -> str:
    return base64.b64encode(hashlib.sha1(text.encode("utf-8")).digest()).decode("ascii")


def safe_equals(actual: Optional[str], expected: str) -> bool:
    return isinstance(actual, str) and hmac.compare_digest(actual.encode("utf-8"), expected.encode("utf-8"))


@dataclass(frozen=True)
class RsaPublicKey:
    n: int
    e: int

    @property
    def bits(self) -> int:
        return self.n.bit_length()

    def verify_sha256(self, text: str, signature: bytes) -> bool:
        k = (self.n.bit_length() + 7) // 8
        if len(signature) != k:
            return False
        s = int.from_bytes(signature, "big")
        if s >= self.n:
            return False
        return hmac.compare_digest(pow(s, self.e, self.n).to_bytes(k, "big"), _encode(text, k))


@dataclass(frozen=True)
class RsaPrivateKey:
    n: int
    e: int
    d: int

    @property
    def bits(self) -> int:
        return self.n.bit_length()

    def sign_sha256(self, text: str) -> bytes:
        k = (self.n.bit_length() + 7) // 8
        m = int.from_bytes(_encode(text, k), "big")
        # Blinding keeps the private-key exponentiation's timing independent of the message.
        while True:
            r = secrets.randbelow(self.n - 2) + 2
            if math.gcd(r, self.n) == 1:
                break
        blinded = pow(m * pow(r, self.e, self.n) % self.n, self.d, self.n)
        return (blinded * pow(r, -1, self.n) % self.n).to_bytes(k, "big")


def private_key(pem: str) -> RsaPrivateKey:
    label, der = _block(pem)
    if label == "RSA PRIVATE KEY":
        return _rsa_private(der)
    if label == "PRIVATE KEY":
        # PrivateKeyInfo { version, AlgorithmIdentifier, OCTET STRING { RSAPrivateKey } }
        info = _Der(_Der(der).read(0x30))
        info.read(0x02)
        info.read(0x30)
        return _rsa_private(info.read(0x04))
    raise ValueError(f'unsupported PEM block "{label}" (encrypted keys are not supported)')


def public_key(pem: str) -> RsaPublicKey:
    label, der = _block(pem)
    if label == "RSA PUBLIC KEY":
        return _rsa_public(der)
    if label == "PUBLIC KEY":
        return _spki(der)
    if label == "CERTIFICATE":
        # Certificate { TBSCertificate { [0] version?, serial, signature, issuer, validity, subject, spki, … }, … }
        tbs = _Der(_Der(_Der(der).read(0x30)).read(0x30))
        if tbs.peek() == 0xA0:
            tbs.read(0xA0)
        for tag in (0x02, 0x30, 0x30, 0x30, 0x30):
            tbs.read(tag)
        return _spki(_wrap(0x30, tbs.read(0x30)))
    raise ValueError(f'unsupported PEM block "{label}"')


def _encode(text: str, k: int) -> bytes:
    """EMSA-PKCS1-v1_5: 00 01 FF…FF 00 DigestInfo(SHA-256)."""
    t = _SHA256_PREFIX + hashlib.sha256(text.encode("utf-8")).digest()
    if k < len(t) + 11:
        raise ValueError("key too short")
    return b"\x00\x01" + b"\xff" * (k - len(t) - 3) + b"\x00" + t


def _block(pem: str) -> Tuple[str, bytes]:
    match = _PEM.search(pem or "")
    if not match:
        raise ValueError("not a PEM key")
    return match.group(1), base64.b64decode(re.sub(r"\s", "", match.group(2)), validate=True)


def _rsa_private(der: bytes) -> RsaPrivateKey:
    key = _Der(_Der(der).read(0x30))
    key.read(0x02)  # version
    n, e, d = (int.from_bytes(key.read(0x02), "big") for _ in range(3))
    return RsaPrivateKey(n, e, d)


def _rsa_public(der: bytes) -> RsaPublicKey:
    key = _Der(_Der(der).read(0x30))
    n, e = (int.from_bytes(key.read(0x02), "big") for _ in range(2))
    return RsaPublicKey(n, e)


def _spki(der: bytes) -> RsaPublicKey:
    # SubjectPublicKeyInfo { AlgorithmIdentifier, BIT STRING { 00, RSAPublicKey } }
    info = _Der(_Der(der).read(0x30))
    info.read(0x30)
    return _rsa_public(info.read(0x03)[1:])


def _wrap(tag: int, content: bytes) -> bytes:
    length = len(content)
    if length < 0x80:
        return bytes([tag, length]) + content
    size = (length.bit_length() + 7) // 8
    return bytes([tag, 0x80 | size]) + length.to_bytes(size, "big") + content


class _Der:
    """Just enough DER reading for RSA keys."""

    def __init__(self, data: bytes) -> None:
        self.data = data
        self.pos = 0

    def peek(self) -> int:
        return self.data[self.pos] if self.pos < len(self.data) else -1

    def read(self, tag: int) -> bytes:
        if self.peek() != tag:
            raise ValueError(f"bad key: expected DER tag 0x{tag:02x}")
        self.pos += 1
        length = self.data[self.pos]
        self.pos += 1
        if length & 0x80:
            count = length & 0x7F
            if not 1 <= count <= 4:
                raise ValueError("bad key: DER length")
            length = int.from_bytes(self.data[self.pos:self.pos + count], "big")
            self.pos += count
        if self.pos + length > len(self.data):
            raise ValueError("bad key: truncated")
        value = self.data[self.pos:self.pos + length]
        self.pos += length
        return value


__all__: List[str] = ["md5_hex", "sha1_base64", "safe_equals", "private_key", "public_key", "RsaPrivateKey", "RsaPublicKey"]
