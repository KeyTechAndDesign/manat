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
| Azericard    | ✓ (form POST) | ✓ | ✓ | ✓ | ✓ | ✓ | RSA-signed CGI interface; store `event.providerRef` for refunds/captures |
| Payriff      | ✓ | ✓ | ✓ | ✓ | ✓ | — | REST + secret key; the webhook is unsigned, so callbacks are confirmed through the API; no documented void for an uncaptured pre-auth |

### Checked against the providers' test environments (2026-10-26)

- **Kapital Bank** (`txpgtst.kapitalbank.az`, shared test user from the docs): order creation, hosted page URL,
  status, the real `?ID=…&STATUS=Cancelled` redirect through `verifyCallback`, a forged `STATUS=FullyPaid`
  redirect (still reported as cancelled), and the bank's error format. The test cards printed in the docs
  are expired (06/25, 11/24) and the payment page rejects them, so a paid order was not tested.
- **Azericard** (`testmpi.3dsecure.az`, test terminal and test key from the docs): signed purchase and
  pre-auth forms accepted (a tampered P_SIGN is refused), test card + 3-D Secure payment, `getStatus`,
  partial refund, second refund, capture of a pre-auth and cancel of a pre-auth. Not tested live:
  `verifyCallback` on a real gateway callback, because the result is posted server-to-server to BACKREF
  and the test machine was not reachable from the internet. Callback verification is covered by vectors.

Learned on the Azericard test gateway (not in the docs):
- The payment result goes to BACKREF server-to-server; nothing is posted through the browser.
- Status (TRTYPE 90) answers in JSON (`actionCode`, `responseCode`, `rrn`, `intRef`, `tranDate` in Baku time…),
  only for the transaction type asked about; `-24` means "context mismatch", `-40` "form in progress".
- Only one online reversal (TRTYPE 22) is accepted per transaction; later refunds must be offline (24).
- The status response signature matches no documented field order, so `getStatus` relies on TLS.
| Epoint       | planned | | | | | | |

```ts
import {Manat, azericard, kapitalBank} from 'manat';

const kapital = new Manat(kapitalBank({username, password, environment: 'test'}));
const azc = new Manat(azericard({terminal, privateKey, gatewayPublicKey, merchantName, merchantUrl, environment: 'test'}));
```

Not affiliated with any bank or payment provider. MIT licensed, provided without warranty: test against your provider's sandbox before going live.

Maintained by [KeyTD](https://keytd.com).
