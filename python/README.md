# Manat for Python

One API for Azerbaijani online payment providers: Kapital Bank, Azericard, Payriff, Epoint, GoldenPay and Yığım.
Python 3.10+, no dependencies (standard library only, RSA included).

```bash
pip install keytd-manat
```

```python
import os

from manat import Manat, azn
from manat.providers import KapitalBank

pay = Manat(KapitalBank(username=os.environ["KB_USER"], password=os.environ["KB_PASSWORD"]))

result = pay.create_payment("A-1001", azn("25.50"), "https://shop.az/payment/return")  # 2550 qəpik
if result.next.type == "redirect":
    return redirect(result.next.url)
```

Store `result.payment_id` with the order. It is what `get_status`, `refund`, `capture` and `cancel` take.

## Providers

```python
from manat.providers import Azericard, Epoint, GoldenPay, KapitalBank, Payriff, Yigim

KapitalBank(username, password, environment="test")
Azericard(terminal, private_key_pem, "My Shop", "https://shop.az", gateway_public_key=gateway_pem, environment="test")
Payriff(secret_key)
Epoint(public_key, private_key)
GoldenPay(auth_key, merchant_name, environment="test")
Yigim(merchant, api_key, biller, environment="test")
```

| Provider     | create_payment | verify_callback | get_status | refund | capture | cancel |
|--------------|:-:|:-:|:-:|:-:|:-:|:-:|
| Kapital Bank | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Azericard    | ✓ (form POST) | ✓ | ✓ | ✓ | ✓ | ✓ |
| Payriff      | ✓ | ✓ | ✓ | ✓ | ✓ | — |
| Epoint       | ✓ | ✓ | ✓ | ✓ | — | — |
| GoldenPay    | ✓ | ✓ | ✓ | ✓ | — | — |
| Yığım        | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |

Every provider takes `base_url` (or `gateway_url`), `transport` and `timeout` keyword arguments.

## Callbacks

Pass what your handler received, with the raw body. Unsigned redirects and webhooks are never trusted: the adapter
re-reads the payment from the provider's API. Epoint's browser redirect is unsigned and rejected with
`invalid_signature`; call `get_status` there.

```python
event = pay.verify_callback(request.method, query=request.args, body=request.get_data(as_text=True))
if event.status == "paid":
    ...  # fulfil event.payment_id; for Azericard store event.provider_ref for refunds
```

## Refunds, capture, cancel

```python
pay.refund(payment_id)                                   # full refund
pay.refund(payment_id, amount=azn("10.00"))              # partial
pay.capture(payment_id)                                  # after intent="authorize"
pay.refund(payment_id, provider_ref=event.provider_ref)  # Azericard
```

## Errors

Everything raises `manat.ManatError`. `error.code` is one of `invalid_request`, `invalid_signature`, `provider_error`,
`network_error`, `unsupported_operation`; `provider_code`, `provider_message` and `raw` hold what the provider said.

## Tests

```bash
cd python && PYTHONPATH=src:tests python3 -m unittest discover -s tests
```
