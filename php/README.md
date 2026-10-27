# Manat for PHP

One API for Azerbaijani online payment providers: Kapital Bank, Azericard, Payriff, Epoint, GoldenPay and Yığım.
PHP 8.1+, no Composer dependencies (only the `curl`, `json` and `openssl` extensions that ship with PHP).

```bash
composer require keytd/manat
```

```php
use Manat\Manat;
use Manat\Money;
use Manat\Providers\KapitalBank;

$pay = new Manat(new KapitalBank([
    'username' => getenv('KB_USER'),
    'password' => getenv('KB_PASSWORD'),
]));

$result = $pay->createPayment([
    'orderId' => 'A-1001',
    'amount' => Money::azn('25.50'),          // 2550 qəpik; floats are never accepted
    'returnUrl' => 'https://shop.az/payment/return',
]);

// Send the customer on: a redirect, or (Azericard) an auto-submitted form.
if ($result->next->type === 'redirect') {
    header('Location: ' . $result->next->url);
}
```

Store `$result->paymentId` with the order. It is what `getStatus`, `refund`, `capture` and `cancel` take.

## Providers

```php
use Manat\Providers\{Azericard, Epoint, GoldenPay, KapitalBank, Payriff, Yigim};

new KapitalBank(['username' => ..., 'password' => ..., 'environment' => 'test']);
new Azericard([
    'terminal' => ..., 'privateKey' => file_get_contents('merchant.pem'),
    'gatewayPublicKey' => file_get_contents('gateway.pem'),
    'merchantName' => 'My Shop', 'merchantUrl' => 'https://shop.az', 'environment' => 'test',
]);
new Payriff(['secretKey' => ...]);
new Epoint(['publicKey' => ..., 'privateKey' => ...]);
new GoldenPay(['authKey' => ..., 'merchantName' => ..., 'environment' => 'test']);
new Yigim(['merchant' => ..., 'apiKey' => ..., 'biller' => ..., 'environment' => 'test']);
```

| Provider     | createPayment | verifyCallback | getStatus | refund | capture | cancel |
|--------------|:-:|:-:|:-:|:-:|:-:|:-:|
| Kapital Bank | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Azericard    | ✓ (form POST) | ✓ | ✓ | ✓ | ✓ | ✓ |
| Payriff      | ✓ | ✓ | ✓ | ✓ | ✓ | — |
| Epoint       | ✓ | ✓ | ✓ | ✓ | — | — |
| GoldenPay    | ✓ | ✓ | ✓ | ✓ | — | — |
| Yığım        | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |

`$pay->supports('capture')` tells you at runtime; an unsupported call throws `unsupported_operation`.

## Callbacks

Pass what your handler received. Signed callbacks are verified in constant time; unsigned redirects and
webhooks are never trusted: the adapter re-reads the payment from the provider's API. Epoint's browser redirect
is unsigned and rejected with `invalid_signature`; call `getStatus` there.

```php
$event = $pay->verifyCallback([
    'method' => $_SERVER['REQUEST_METHOD'],
    'query' => $_GET,
    'body' => $_POST ?: file_get_contents('php://input'),
]);

if ($event->status === \Manat\PaymentStatus::PAID) {
    // fulfil order $event->paymentId; for Azericard store $event->providerRef for refunds
}
```

Statuses: `pending`, `authorized`, `paid`, `failed`, `cancelled`, `refunded`, `partially_refunded`.

## Refunds, capture, cancel

```php
$pay->refund(['paymentId' => $id]);                                   // full refund
$pay->refund(['paymentId' => $id, 'amount' => Money::azn('10.00')]);  // partial
$pay->capture(['paymentId' => $id]);                                  // after 'intent' => 'authorize'
$pay->refund(['paymentId' => $id, 'providerRef' => $ref]);            // Azericard needs the callback's providerRef
```

## Errors

Everything throws `Manat\ManatError`. `$e->errorCode` is one of `invalid_request`, `invalid_signature`,
`provider_error`, `network_error`, `unsupported_operation`; for provider errors `$e->providerCode` and
`$e->providerMessage` hold what the provider said and `$e->raw` its whole reply.

## HTTP

Requests go through ext-curl with TLS verification and a 30-second timeout. To change either, or to use
your framework's HTTP client, pass options as the second argument:

```php
new Payriff(['secretKey' => ...], ['timeout' => 10.0, 'transport' => new MyTransport()]); // implements Manat\Http\Transport
```

## Tests

The adapters run the same JSON test vectors as every other language (`spec/vectors`). From the repository root:

```bash
composer install && composer test
```
