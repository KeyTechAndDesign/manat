# Manat

One API for Azerbaijani online payment providers, in several languages.

```ts
import {Manat, azn, kapitalBank} from 'manat';

const pay = new Manat(kapitalBank({username: process.env.KB_USER!, password: process.env.KB_PASSWORD!}));

const {next} = await pay.createPayment({
    orderId: 'A-1001',
    amount: azn('25.50'),
    returnUrl: 'https://shop.az/payment/return',
});
// redirect the customer to next.url (or auto-submit next.fields for form-based providers)
```

- Amounts are integers in minor units (`azn('25.50')` → `{amount: 2550, currency: 'AZN'}`), never floats.
- Every provider exposes the same operations: `createPayment`, `verifyCallback`, `getStatus`, `refund`, plus `capture`/`cancel` where the provider supports them.
- Callbacks are verified with constant-time signature checks; an unsigned redirect is never trusted on its own.
- Built only from providers' public developer documentation.

| Language   | Package                      | Status      |
|------------|------------------------------|-------------|
| TypeScript | `npm i manat`                | in progress |
| PHP        | `composer require keytd/manat` | planned   |
| Python     | `pip install manat`          | planned     |

The shared interface and the cross-language test vectors live in [`spec/`](spec/README.md).

## Providers

| Provider     | createPayment | verifyCallback | getStatus | refund | capture | cancel | Notes |
|--------------|:-:|:-:|:-:|:-:|:-:|:-:|---|
| Kapital Bank | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | REST + Basic auth; the redirect back is unsigned, so callbacks are confirmed through the API |
| Azericard    | ✓ (form POST) | ✓ | — | ✓ | ✓ | ✓ | RSA-signed CGI interface; store `event.providerRef` for refunds/captures |
| Payriff      | planned | | | | | | |
| Epoint       | planned | | | | | | |

```ts
import {Manat, azericard, kapitalBank} from 'manat';

const kapital = new Manat(kapitalBank({username, password, environment: 'test'}));
const azc = new Manat(azericard({terminal, privateKey, gatewayPublicKey, merchantName, merchantUrl, environment: 'test'}));
```

Not affiliated with any bank or payment provider. MIT licensed, provided without warranty: test against your provider's sandbox before going live.

Maintained by [KeyTD](https://keytd.com).
