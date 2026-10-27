# Manat for TypeScript / JavaScript

One API for Azerbaijani online payment providers: Kapital Bank, Azericard, Payriff, Epoint, GoldenPay and Yığım.
Node.js 18+, ESM and CommonJS, no runtime dependencies.

```bash
npm i @keytd/manat
```

```ts
import {Manat, azn, kapitalBank} from '@keytd/manat';

const pay = new Manat(kapitalBank({username: process.env.KB_USER!, password: process.env.KB_PASSWORD!}));

const {paymentId, next} = await pay.createPayment({
    orderId: 'A-1001',
    amount: azn('25.50'),          // {amount: 2550, currency: 'AZN'}; floats are never accepted
    returnUrl: 'https://shop.az/payment/return',
});

// Send the customer on: a redirect, or (Azericard) an auto-submitted form.
if (next.type === 'redirect') res.redirect(next.url);
```

Store `paymentId` with the order. It is what `getStatus`, `refund`, `capture` and `cancel` take.

## Providers

```ts
import {azericard, epoint, goldenPay, kapitalBank, payriff, yigim} from '@keytd/manat';

kapitalBank({username, password, environment: 'test'});
azericard({terminal, privateKey, gatewayPublicKey, merchantName, merchantUrl, environment: 'test'});
payriff({secretKey});
epoint({publicKey, privateKey});
goldenPay({authKey, merchantName, environment: 'test'});
yigim({merchant, apiKey, biller, environment: 'test'});
```

| Provider     | createPayment | verifyCallback | getStatus | refund | capture | cancel |
|--------------|:-:|:-:|:-:|:-:|:-:|:-:|
| Kapital Bank | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Azericard    | ✓ (form POST) | ✓ | ✓ | ✓ | ✓ | ✓ |
| Payriff      | ✓ | ✓ | ✓ | ✓ | ✓ | — |
| Epoint       | ✓ | ✓ | ✓ | ✓ | — | — |
| GoldenPay    | ✓ | ✓ | ✓ | ✓ | — | — |
| Yığım        | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |

`pay.supports('capture')` tells you at runtime; an unsupported call throws `unsupported_operation`.
Provider details (what was checked live, quirks, why some operations are missing) are in the
[main README](https://github.com/KeyTechAndDesign/manat#providers).

## Callbacks

Pass what your handler received. Signed callbacks are verified in constant time; unsigned redirects and
webhooks are never trusted: the adapter re-reads the payment from the provider's API.

```ts
const event = await pay.verifyCallback({method: req.method, query: req.query, body: req.body, headers: req.headers});

if (event.status === 'paid') {
    // fulfil order event.paymentId; for Azericard store event.providerRef for refunds
}
```

Statuses: `pending`, `authorized`, `paid`, `failed`, `cancelled`, `refunded`, `partially_refunded`.

## Refunds, capture, cancel

```ts
await pay.refund({paymentId});                              // full refund
await pay.refund({paymentId, amount: azn('10.00')});        // partial
await pay.capture({paymentId});                             // after intent: 'authorize'
await pay.refund({paymentId, providerRef});                 // Azericard needs the callback's providerRef
```

## Errors

Everything throws `ManatError`. `error.code` is one of `invalid_request`, `invalid_signature`, `provider_error`,
`network_error`, `unsupported_operation`; for provider errors `error.providerCode` and `error.providerMessage`
hold what the provider said and `error.raw` its whole reply. `isManatError(error, 'provider_error')` narrows the type.

## HTTP

Requests use the global `fetch` with a 30-second timeout. To change either, pass options as the second argument:

```ts
payriff({secretKey}, {timeoutMs: 10_000, fetch: myFetch});
```

MIT licensed. Not affiliated with any bank or payment provider: test against your provider's sandbox before going live.
