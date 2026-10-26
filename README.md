# Manat

One API for Azerbaijani online payment providers, in several languages.

```ts
import {Manat, azn} from 'manat';
import {payriff} from 'manat/payriff'; // provider adapters land here

const pay = new Manat(payriff({secretKey: process.env.PAYRIFF_SECRET!}));

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

Not affiliated with any bank or payment provider. MIT licensed, provided without warranty: test against your provider's sandbox before going live.

Maintained by [KeyTD](https://keytd.com).
