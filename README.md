# Manat

[![CI](https://github.com/KeyTechAndDesign/manat/actions/workflows/ci.yml/badge.svg)](https://github.com/KeyTechAndDesign/manat/actions/workflows/ci.yml)

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
- No runtime dependencies: each package uses only its language's standard library.
- Built from providers' public developer documentation; where there is none (GoldenPay), from community SDKs
  checked against the provider's test host.

| Language      | Package                          | Status      |
|---------------|----------------------------------|-------------|
| TypeScript    | `npm i manat` ([`node/`](node))  | in progress |
| PHP           | `composer require keytd/manat` ([`php/`](php/README.md)) | in progress |
| Java / Kotlin | Maven Central                    | planned     |
| C#            | NuGet                            | planned     |
| Python        | `pip install manat`              | planned     |
| Go            | `go get`                         | planned     |

The shared interface and the cross-language test vectors live in [`spec/`](spec/README.md).

## Providers

| Provider     | createPayment | verifyCallback | getStatus | refund | capture | cancel | Notes |
|--------------|:-:|:-:|:-:|:-:|:-:|:-:|---|
| Kapital Bank | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | REST + Basic auth; the redirect back is unsigned, so callbacks are confirmed through the API |
| Azericard    | ✓ (form POST) | ✓ | ✓ | ✓ | ✓ | ✓ | RSA-signed CGI interface; store `event.providerRef` for refunds/captures |
| Payriff      | ✓ | ✓ | ✓ | ✓ | ✓ | — | REST + secret key; the webhook is unsigned, so callbacks are confirmed through the API; no documented void for an uncaptured pre-auth |
| Epoint       | ✓ | ✓ | ✓ | ✓ | — | — | Signed base64 JSON (`data` + `signature`); set the result URL in the Epoint panel; no pre-auth (see below) |
| GoldenPay    | ✓ | ✓ | ✓ | ✓ | — | — | md5-signed JSON; AZN only; the redirect back is unsigned, so callbacks are confirmed through the API (see below) |

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
- **Payriff** (`api.payriff.com`, key of an application in "Development" status, which Payriff runs in test mode
  only): AZN and USD order creation (with `callbackUrl` and metadata), the hosted payment page, `getStatus`
  (`CREATED` → `pending`, `amount: 1.5` USD → 150 cents, `EXPIRED` → `cancelled` once the payment page timed out),
  `verifyCallback` re-reading the order, a payment with the docs' sandbox card (`APPROVED` → `paid`), a partial
  refund, a rejected second refund, a full refund, and the errors for an unknown order, a refund of an unpaid order
  and a capture of an unpaid pre-auth. A capture could not be tested: test mode never holds a pre-auth (see below).
  Test-mode orders also get a `pay.payriff.com` link, not the `sbpay.payriff.com` shown in the docs, and the page
  shows no test marking.

Learned on Payriff test mode (not in the docs):
- Code `01000` covers several errors ("Order info not found", "Transaction status is not approved",
  "… not preauth-approved"); only the message tells them apart. Lookups answer HTTP 200, refund/capture HTTP 400.
- Metadata comes back from `GET /orders/{id}` as one string (`"{orderRef=A-1001}"`), not as an object.
- A real card on a Development application is refused ("Only test cards can be used in Development status") and
  the order becomes `DECLINED`; nothing is charged.
- Only one refund per order is accepted. After a partial refund the order's `paymentStatus` is `REFUNDED` and only
  the transaction says `PARTIAL_REFUND`; the adapter reports `partially_refunded`. Any further refund answers `01000`.
- A refund without an amount is rejected ("Request refund amount is null"), so for a full refund the adapter
  reads the order and sends its amount.
- A paid `PRE_AUTH` order reads `APPROVED` (not `PREAUTH_APPROVED`) and `/complete` answers "Transaction status is
  not preauth-approved"; the order can be refunded like a sale. So test mode runs pre-auths as sales, and the
  `PREAUTH_APPROVED` → `authorized` mapping and `capture` are covered by vectors built from the docs only.
- `paidAmount` is the amount net of Payriff's commission (1.00 → 0.97 at 3%); the adapter reports `amount`.
- An unpaid page expires within minutes (`EXPIRED`, reported as `cancelled`).

Learned on the Azericard test gateway (not in the docs):
- The payment result goes to BACKREF server-to-server; nothing is posted through the browser.
- Status (TRTYPE 90) answers in JSON (`actionCode`, `responseCode`, `rrn`, `intRef`, `tranDate` in Baku time…),
  only for the transaction type asked about; `-24` means "context mismatch", `-40` "form in progress".
- Only one online reversal (TRTYPE 22) is accepted per transaction; later refunds must be offline (24).
- The status response signature matches no documented field order, so `getStatus` relies on TLS.

### Epoint (vectors only)

Epoint has no public test environment, so the adapter is checked against the docs' own signed example, the
request/response examples, and two community SDKs (rafoabbas/epoint-php, integrify/epoint). One live call was made
with the docs' placeholder merchant: `get-status` answers HTTP 200 `{"status":"server_error","message":"Merchant not found"}`,
which the adapter reports as a `provider_error`, not as a failed payment.

- The callback is sent to the result URL configured in the Epoint merchant panel; `callbackUrl` is not sent.
  The browser redirect back to `returnUrl` is not signed: use `getStatus` with the `paymentId` from `createPayment`.
- Refunds use `/reverse` (full, or partial with an amount). `/refund-request` is a payout to a saved card and is not used.
- Pre-auth is left out: the documented statuses have no value for a held but uncaptured payment, so a hold could not be told from a capture.
- Neither the callback nor `get-status` names a currency, so `amount` is only in `raw.amount`.

### GoldenPay (no public docs)

GoldenPay publishes no API documentation. The adapter follows two community SDKs (orkhanahmadov/goldenpay, 2021;
Rasimoghlu/laravel-golden-pay-payment, 2026) and the replies of the test host `rest-newpgtest.goldenpay.az`
(checked 2026-10-27 without an account: routes, the reply format and the error codes for an unknown payment key or
merchant). A payment, a refund and the hash checks have not been tried against GoldenPay: that needs a merchant
name and auth key, test or live.

- The two SDKs disagree. The host and paths (`/getPaymentKey`, `/getPaymentResult`, `/reversePaymentKey`, payment
  page `/pay/{paymentKey}`) follow the 2026 SDK, which the test host confirms. The reply format follows the 2021 SDK:
  the test host answers `{status: {code, message}, paymentKey, …}` at the top level, not under `response`.
- The API has no currency (AZN only), no order id (the description, or the orderId when there is none, is sent
  instead), no pre-auth and no callback URL: the customer comes back to `returnUrl` with `payment_key`.
- An unknown payment key answers code 900 "Internal exception", reported as a `provider_error`.
- Refunds need an amount, so the payment is read first. GoldenPay has no partial-refund status: a refund smaller
  than the payment is reported as `partially_refunded`.

```ts
import {Manat, azericard, epoint, goldenPay, kapitalBank, payriff} from 'manat';

const kapital = new Manat(kapitalBank({username, password, environment: 'test'}));
const azc = new Manat(azericard({terminal, privateKey, gatewayPublicKey, merchantName, merchantUrl, environment: 'test'}));
const pr = new Manat(payriff({secretKey}));
const ep = new Manat(epoint({publicKey, privateKey}));
const gp = new Manat(goldenPay({authKey, merchantName, environment: 'test'}));
```

Not affiliated with any bank or payment provider. MIT licensed, provided without warranty: test against your provider's sandbox before going live.

Maintained by [KeyTD](https://keytd.com).
