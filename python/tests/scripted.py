"""Serves a vector's `http` steps in order and checks each request against `expect`."""

from __future__ import annotations

import unittest
from decimal import Decimal
from typing import Any, List, Mapping, Optional

from manat import _form, _json
from manat._http import HttpResponse


class ScriptedTransport:
    def __init__(self, test: unittest.TestCase, steps: Optional[List[dict]]) -> None:
        self.test = test
        self.steps = steps or []
        self.index = 0

    def send(self, method: str, url: str, headers: Mapping[str, str], body: Optional[bytes], timeout: float) -> HttpResponse:
        n = self.index + 1
        self.test.assertLess(self.index, len(self.steps), f"unexpected request #{n}: {method} {url}")
        step = self.steps[self.index]
        self.index += 1
        expect = step["expect"]
        self.test.assertEqual(expect["method"], method, f"request #{n} method")
        self.test.assertEqual(expect["url"], url, f"request #{n} url")
        for name, value in (expect.get("headers") or {}).items():
            self.test.assertEqual(value, headers.get(name.lower()), f"request #{n} header {name}")
        text = (body or b"").decode("utf-8")
        if "json" in expect:
            assert_equal(self.test, expect["json"], _json.parse(text), f"request #{n} body")
        if "form" in expect:
            assert_equal(self.test, expect["form"], _form.decode(text), f"request #{n} form")
        respond = step["respond"]
        reply = _json.stringify(respond["json"]) if "json" in respond else respond.get("text", "")
        return HttpResponse(int(respond["status"]), reply)

    def assert_done(self) -> None:
        self.test.assertEqual(len(self.steps), self.index, "all scripted requests were made")


def assert_equal(test: unittest.TestCase, expected: Any, actual: Any, path: str) -> None:
    """Deep equality with JSON semantics: numbers compare by value, a string never equals a number."""
    _compare(test, expected, actual, path, subset=False)


def assert_subset(test: unittest.TestCase, expected: Any, actual: Any, path: str) -> None:
    """Every key in `expected` must be in `actual` with a matching value, like toMatchObject."""
    _compare(test, expected, actual, path, subset=True)


def _compare(test: unittest.TestCase, expected: Any, actual: Any, path: str, subset: bool) -> None:
    if isinstance(expected, dict):
        test.assertIsInstance(actual, dict, f"{path}: expected an object, got {actual!r}")
        if not subset:
            test.assertEqual(sorted(expected), sorted(actual), f"{path}: keys")
        for key, value in expected.items():
            test.assertIn(key, actual, f"{path}.{key} is missing")
            _compare(test, value, actual[key], f"{path}.{key}", subset)
        return
    if isinstance(expected, list):
        test.assertIsInstance(actual, list, f"{path}: expected a list, got {actual!r}")
        test.assertEqual(len(expected), len(actual), f"{path}: list length")
        for i, (want, got) in enumerate(zip(expected, actual)):
            _compare(test, want, got, f"{path}[{i}]", subset)
        return
    if _is_number(expected) and _is_number(actual):
        test.assertEqual(Decimal(expected), Decimal(actual), path)
        return
    test.assertEqual(type(expected), type(actual), f"{path}: {expected!r} vs {actual!r}")
    test.assertEqual(expected, actual, path)


def _is_number(value: Any) -> bool:
    return isinstance(value, (int, Decimal)) and not isinstance(value, bool)
