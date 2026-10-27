from __future__ import annotations

import re
import unittest
from decimal import Decimal
from pathlib import Path

from manat import Manat, ManatError, Money, azn, from_decimal
from manat.money import currency_from_numeric
from manat.types import CreatePaymentResult, NextAction, PaymentEvent, PaymentState


class MoneyTest(unittest.TestCase):
    def test_formats_minor_units_without_float_maths(self) -> None:
        self.assertEqual("25.50", Money(2550, "AZN").to_decimal_string())
        self.assertEqual("0.05", Money(5, "AZN").to_decimal_string())
        self.assertEqual("1.00", Money(100, "USD").to_decimal_string())
        self.assertEqual("1234567.89", Money(123456789, "AZN").to_decimal_string())

    def test_parses_decimals_into_minor_units(self) -> None:
        self.assertEqual(Money(2550, "AZN"), azn("25.5"))
        self.assertEqual(Money(2550, "AZN"), from_decimal(Decimal("25.50"), "AZN"))
        self.assertEqual(Money(5, "AZN"), azn("0.05"))
        self.assertEqual(Money(1000, "EUR"), from_decimal("10", "EUR"))
        self.assertEqual(Money(500, "AZN"), from_decimal(Decimal("5.000"), "AZN"))

    def test_rejects_amounts_that_cannot_be_represented_exactly(self) -> None:
        for bad in ("25.555", "-1", "abc", "", "1e3", 25.5, None):
            with self.assertRaises(ManatError, msg=repr(bad)):
                azn(bad)  # type: ignore[arg-type]
        for amount in (-5, 25.5, True):
            with self.assertRaises(ManatError):
                Money(amount, "AZN")  # type: ignore[arg-type]

    def test_writes_json_numbers_without_trailing_zeros(self) -> None:
        self.assertEqual(Decimal("25.5"), Money(2550, "AZN").to_number())
        self.assertEqual("1", str(Money(100, "AZN").to_number()))

    def test_maps_iso_numeric_codes(self) -> None:
        self.assertEqual("944", Money(1, "AZN").numeric_currency())
        self.assertEqual("AZN", currency_from_numeric("944"))
        self.assertIsNone(currency_from_numeric("000"))


class FakeProvider:
    name = "fake"
    capabilities = frozenset({"createPayment", "verifyCallback", "getStatus"})

    def __init__(self) -> None:
        self.calls: list = []

    def create_payment(self, request):
        self.calls.append("createPayment")
        return CreatePaymentResult(f"p-{request.order_id}", "pending", NextAction("redirect", "https://pay.example/1"))

    def verify_callback(self, callback):
        self.calls.append("verifyCallback")
        return PaymentEvent("p-1", "paid")

    def get_status(self, payment_id):
        self.calls.append("getStatus")
        return PaymentState(payment_id, "paid")


class ManatTest(unittest.TestCase):
    def test_delegates_valid_requests(self) -> None:
        provider = FakeProvider()
        self.assertEqual("p-A-1", Manat(provider).create_payment("A-1", Money(2550, "AZN"), "https://shop.example/return").payment_id)
        self.assertEqual(["createPayment"], provider.calls)

    def test_validates_input_before_calling_the_provider(self) -> None:
        provider = FakeProvider()
        pay = Manat(provider)
        cases = [
            lambda: pay.create_payment("", Money(2550, "AZN"), "https://shop.example/return"),
            lambda: pay.create_payment("A-1", None, "https://shop.example/return"),  # type: ignore[arg-type]
            lambda: pay.create_payment("A-1", Money(0, "AZN"), "https://shop.example/return"),
            lambda: pay.create_payment("A-1", Money(100, "AZN"), "javascript:alert(1)"),
            lambda: pay.create_payment("A-1", Money(100, "AZN"), "https://shop.example/return", callback_url="/relative"),
        ]
        for case in cases:
            with self.assertRaises(ManatError) as caught:
                case()
            self.assertEqual("invalid_request", caught.exception.code)
        self.assertEqual([], provider.calls)

    def test_reports_unsupported_operations(self) -> None:
        pay = Manat(FakeProvider())
        self.assertFalse(pay.supports("refund"))
        for call in (lambda: pay.refund("p-1"), lambda: pay.capture("p-1")):
            with self.assertRaises(ManatError) as caught:
                call()
            self.assertEqual("unsupported_operation", caught.exception.code)

    def test_requires_a_payment_id(self) -> None:
        with self.assertRaises(ManatError) as caught:
            Manat(FakeProvider()).get_status("")
        self.assertEqual("invalid_request", caught.exception.code)


class UserAgentTest(unittest.TestCase):
    def test_requests_identify_the_sdk(self) -> None:
        from manat._http import Http, HttpResponse

        sent = {}

        class Capture:
            def send(self, method, url, headers, body, timeout):
                sent.update(headers)
                return HttpResponse(200, "{}")

        Http("test", Capture(), 5).json("GET", "https://example.com")
        self.assertRegex(sent["user-agent"], r"^keytd-manat/\d+\.\d+\.\d+ ")


class NoDependenciesTest(unittest.TestCase):
    """The package is advertised as dependency-free."""

    def test_pyproject_declares_no_dependencies(self) -> None:
        pyproject = (Path(__file__).resolve().parents[1] / "pyproject.toml").read_text("utf-8")
        self.assertRegex(pyproject, r"(?m)^dependencies = \[\]$")
        self.assertNotIn("optional-dependencies", pyproject)

    def test_imports_only_the_standard_library(self) -> None:
        allowed = {"__future__", "base64", "binascii", "dataclasses", "datetime", "decimal", "hashlib", "hmac", "http", "json", "math",
                   "re", "secrets", "typing", "urllib"}
        source = Path(__file__).resolve().parents[1] / "src" / "manat"
        for path in source.rglob("*.py"):
            for module in re.findall(r"(?m)^\s*(?:from|import) ([a-zA-Z_][\w]*)", path.read_text("utf-8")):
                self.assertIn(module, allowed, f"{path.name} imports {module}")


if __name__ == "__main__":
    unittest.main()
