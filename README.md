# Manat

[![CI](https://github.com/KeyTechAndDesign/manat/actions/workflows/ci.yml/badge.svg)](https://github.com/KeyTechAndDesign/manat/actions/workflows/ci.yml)

One API for Azerbaijani online payment providers, in several languages.

```ts
import {Manat, azn, kapitalBank} from '@keytd/manat';

const pay = new Manat(kapitalBank({username: process.env.KB_USER!, password: process.env.KB_PASSWORD!}));

const {next} = await pay.createPayment({
    orderId: 'A-1001',
    amount: azn('25.50'),
    returnUrl: 'https://shop.az/payment/return',
});
// redirect the customer to next.url
```

- Amounts are integers in minor units (`azn('25.50')` → 2550 qəpik), never floats.
- Same operations for every provider: `createPayment`, `verifyCallback`, `getStatus`, `refund`, `capture`, `cancel`.
- Callbacks are verified; an unsigned redirect is never trusted on its own.
- No dependencies.

## Languages

| Language      | Install | Status |
|---------------|---------|--------|
| TypeScript    | [`npm i @keytd/manat`](https://www.npmjs.com/package/@keytd/manat) ([docs](node/README.md)) | 0.2.1 |
| PHP           | [`composer require keytd/manat`](https://packagist.org/packages/keytd/manat) ([docs](php/README.md)) | 0.2.1 |
| Java / Kotlin | [`com.keytd:manat`](https://central.sonatype.com/artifact/com.keytd/manat) ([docs](java/README.md)) | 0.2.1 |
| C# / .NET     | [`dotnet add package KeyTD.Manat`](https://www.nuget.org/packages/KeyTD.Manat) ([docs](dotnet/README.md)) | 0.2.1 |
| Python        | [`pip install keytd-manat`](https://pypi.org/project/keytd-manat/) ([docs](python/README.md)) | 0.2.1 |
| Go            | [`go get github.com/KeyTechAndDesign/manat/go`](https://pkg.go.dev/github.com/KeyTechAndDesign/manat/go) ([docs](go/README.md)) | 0.2.1 |

## Providers

| Provider     | createPayment | verifyCallback | getStatus | refund | capture | cancel |
|--------------|:-:|:-:|:-:|:-:|:-:|:-:|
| Kapital Bank | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Azericard    | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Payriff      | ✓ | ✓ | ✓ | ✓ | ✓ | — |
| Epoint       | ✓ | ✓ | ✓ | ✓ | — | — |
| GoldenPay    | ✓ | ✓ | ✓ | ✓ | — | — |
| Yığım        | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |

```ts
import {Manat, azericard, epoint, goldenPay, kapitalBank, payriff, yigim} from '@keytd/manat';

new Manat(kapitalBank({username, password, environment: 'test'}));
new Manat(azericard({terminal, privateKey, gatewayPublicKey, merchantName, merchantUrl, environment: 'test'}));
new Manat(payriff({secretKey}));
new Manat(epoint({publicKey, privateKey}));
new Manat(goldenPay({authKey, merchantName, environment: 'test'}));
new Manat(yigim({merchant, apiKey, biller, environment: 'test'}));
```

Azericard pays through a form POST: render `next.fields` and submit them to `next.url`, and keep `event.providerRef`
for refunds. Each adapter's notes on its provider are at the top of its source file.

Not affiliated with any bank or payment provider. MIT licensed. Test against your provider's sandbox before going live.

Maintained by [KeyTD](https://keytd.com).
