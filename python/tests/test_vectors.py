"""Runs every vector in spec/vectors, the same files the other languages run."""

from __future__ import annotations

import unittest
from datetime import datetime
from pathlib import Path
from typing import Any

from manat import Manat, ManatError, Money, _json
from manat.providers import Azericard, Epoint, GoldenPay, KapitalBank, Payriff, Yigim
from manat.providers import azericard, epoint, goldenpay, kapitalbank, payriff, yigim

from scripted import ScriptedTransport, assert_equal, assert_subset

VECTORS = Path(__file__).resolve().parents[2] / "spec" / "vectors"

MAP_STATUS = {
    "kapitalbank": lambda case: kapitalbank.map_status(case),
    "payriff": lambda case: payriff.map_status(case.get("paymentStatus")),
    "epoint": lambda case: epoint.map_status(case.get("status")),
    "goldenpay": lambda case: goldenpay.map_status(case["status"]["code"]),
    "yigim": lambda case: yigim.map_status(case.get("status")),
}
CONSTRUCTORS = {
    "kapitalbank": (KapitalBank, {"username": "username", "password": "password", "environment": "environment", "baseUrl": "base_url"}),
    "payriff": (Payriff, {"secretKey": "secret_key", "baseUrl": "base_url"}),
    "epoint": (Epoint, {"publicKey": "public_key", "privateKey": "private_key", "baseUrl": "base_url"}),
    "goldenpay": (GoldenPay, {"authKey": "auth_key", "merchantName": "merchant_name", "environment": "environment", "baseUrl": "base_url"}),
    "yigim": (Yigim, {"merchant": "merchant", "apiKey": "api_key", "biller": "biller", "template": "template", "environment": "environment",
                      "baseUrl": "base_url"}),
    "azericard": (Azericard, {"terminal": "terminal", "privateKey": "private_key", "gatewayPublicKey": "gateway_public_key",
                              "merchantName": "merchant_name", "merchantUrl": "merchant_url", "environment": "environment",
                              "gatewayUrl": "gateway_url", "email": "email", "country": "country", "merchantGmt": "merchant_gmt",
                              "macFields": "mac_fields", "clock": "clock", "nonce": "nonce"}),
}


def provider(name: str, config: dict, transport: ScriptedTransport) -> Any:
    """Builds the adapter from the vector config the same way in every language: `<name>File` is read relative to the
    provider's vector folder, `clock` (ISO time) and `nonce` (string) become fixed generators."""
    cls, keys = CONSTRUCTORS[name]
    kwargs: dict = {"transport": transport}
    for key, value in config.items():
        if key.endswith("File"):
            key, value = key[:-4], (VECTORS / name / value).read_text("utf-8")
        if key == "clock":
            moment = datetime.fromisoformat(value.replace("Z", "+00:00"))
            value = lambda moment=moment: moment  # noqa: E731
        elif key == "nonce":
            value = lambda nonce=value: nonce  # noqa: E731
        kwargs[keys[key]] = value
    return cls(**kwargs)


def money(value: Any) -> Money:
    return Money(value["amount"], value["currency"])


def invoke(pay: Manat, operation: str, data: dict) -> dict:
    if operation == "createPayment":
        customer = data.get("customer") or {}
        return pay.create_payment(data["orderId"], money(data["amount"]), data["returnUrl"], callback_url=data.get("callbackUrl"),
                                  description=data.get("description"), language=data.get("language"), intent=data.get("intent", "purchase"),
                                  customer_email=customer.get("email"), customer_phone=customer.get("phone"),
                                  metadata=data.get("metadata")).to_dict()
    if operation == "verifyCallback":
        return pay.verify_callback(data["method"], query=data.get("query"), body=data.get("body"), headers=data.get("headers")).to_dict()
    if operation == "getStatus":
        return pay.get_status(data["paymentId"]).to_dict()
    amount = money(data["amount"]) if data.get("amount") is not None else None
    if operation == "refund":
        return pay.refund(data["paymentId"], amount=amount, provider_ref=data.get("providerRef")).to_dict()
    if operation == "capture":
        return pay.capture(data["paymentId"], amount=amount, provider_ref=data.get("providerRef")).to_dict()
    if operation == "cancel":
        return pay.cancel(data["paymentId"], provider_ref=data.get("providerRef")).to_dict()
    raise AssertionError(f"unknown operation {operation}")


class VectorsTest(unittest.TestCase):
    def test_finds_vectors(self) -> None:
        self.assertGreater(len(list(VECTORS.glob("*/*.json"))), 100)


def make_test(path: Path):
    def test(self: unittest.TestCase) -> None:
        vector = _json.parse(path.read_text("utf-8"))
        name, operation, config = vector["provider"], vector["operation"], vector["config"]
        if operation == "mapStatus":
            for case in vector["cases"]:
                self.assertEqual(case["expected"]["status"], MAP_STATUS[name](case["input"]), case["input"])
            return
        if operation == "macSource":
            for case in vector["cases"]:
                self.assertEqual(case["expected"]["source"], azericard.mac_source(case["input"]["fields"], case["input"]["order"]))
            return
        if operation == "sign":
            for case in vector["cases"]:
                self.assertEqual(case["expected"], epoint.encode(config["privateKey"], case["input"]["json"]))
            return
        transport = ScriptedTransport(self, vector.get("http"))
        expected = vector.get("expected") or {}
        if "error" in expected:
            with self.assertRaises(ManatError) as caught:
                invoke(Manat(provider(name, config, transport)), operation, vector.get("input") or {})
            self.assertEqual(expected["error"], caught.exception.code, str(caught.exception))
            if expected.get("providerCode") is not None:
                self.assertEqual(expected["providerCode"], caught.exception.provider_code)
        else:
            assert_subset(self, expected, invoke(Manat(provider(name, config, transport)), operation, vector.get("input") or {}), "result")
        transport.assert_done()
    return test


for _path in sorted(VECTORS.glob("*/*.json")):
    setattr(VectorsTest, f"test_{_path.parent.name}_{_path.stem}".replace("-", "_"), make_test(_path))

if __name__ == "__main__":
    unittest.main()
