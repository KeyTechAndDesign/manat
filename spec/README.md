# Manat interface spec

Every language package implements the same operations, types and error codes
described here, and passes the same test vectors in `spec/vectors/`.

## Money

Amounts are integers in minor units (qəpik for AZN): `2550` means 25.50 AZN.
Floats are never accepted on the public API. Each provider adapter converts to
whatever its API expects (decimal string, minor units, …).

```
Money = { amount: integer >= 1, currency: "AZN" | "USD" | "EUR" | … (ISO 4217) }
```

## Operations

| Operation          | Required | Purpose                                                        |
|--------------------|----------|----------------------------------------------------------------|
| `createPayment`    | yes      | Start a payment; returns where to send the customer next       |
| `verifyCallback`   | yes      | Authenticate a provider callback/redirect and map it to an event |
| `getStatus`        | yes*     | Ask the provider for the current state of a payment            |
| `refund`           | yes*     | Full or partial refund of a completed payment                  |
| `capture`          | optional | Complete a pre-authorised payment (`intent: "authorize"`)       |
| `cancel`           | optional | Void/reverse an authorisation or same-day payment              |

\* Required when the provider's public API supports it. An adapter that cannot
do an operation throws `unsupported_operation`; `capabilities` lists what it can do.

### createPayment

Input:

| Field         | Type     | Notes                                                      |
|---------------|----------|------------------------------------------------------------|
| `orderId`     | string   | Merchant's own id, unique per payment attempt               |
| `amount`      | Money    |                                                            |
| `description` | string?  | Shown to the customer where the provider supports it        |
| `returnUrl`   | string   | Where the customer lands after paying                       |
| `callbackUrl` | string?  | Server-to-server notification URL, if the provider has one  |
| `language`    | `az`\|`en`\|`ru`? | Payment page language                             |
| `intent`      | `purchase`\|`authorize` | Default `purchase`                          |
| `customer`    | { email?, phone? }? |                                                 |
| `metadata`    | map<string,string>? | Passed through where supported                  |

Output:

```
{
  paymentId: string,            // provider's id for this payment (or orderId when the provider has none)
  status: PaymentStatus,        // usually "pending"
  next: { type: "redirect", url }                       // send the customer here (GET)
      | { type: "form", url, method: "POST", fields }   // auto-submit this form
      | { type: "none" },                               // nothing to do (e.g. already paid)
  raw: object                   // provider response, untouched
}
```

### verifyCallback

Input: what the merchant's HTTP handler received.

```
{ method, query: map, body: map | string, headers: map }
```

Output: `PaymentEvent { paymentId, orderId?, status, amount?, raw }`.
Throws `invalid_signature` when the message is not authentic. Adapters must
compare signatures in constant time and must not trust status fields from an
unsigned redirect: when a provider's redirect is unsigned, `verifyCallback`
calls `getStatus` and reports that result instead.

### getStatus / refund / capture / cancel

`getStatus({ paymentId })` → `{ paymentId, status, amount?, refundedAmount?, raw }`
`refund({ paymentId, amount? })` → `{ paymentId, refundId?, status, raw }` (no amount = full refund)
`capture({ paymentId, amount? })`, `cancel({ paymentId })` → same shape as getStatus.

## PaymentStatus

`pending` · `authorized` · `paid` · `failed` · `cancelled` · `refunded` · `partially_refunded`

Each adapter documents how the provider's own codes map onto these.

## Errors

All errors are `ManatError` with a `code`:

| Code                    | Meaning                                                    |
|-------------------------|------------------------------------------------------------|
| `invalid_request`       | Bad input caught before calling the provider               |
| `invalid_signature`     | Callback failed authentication                             |
| `provider_error`        | Provider answered with an error (`providerCode`, `providerMessage` set) |
| `network_error`         | Timeout, DNS, TLS, non-JSON/XML response                   |
| `unsupported_operation` | The adapter cannot do this with the provider's public API  |

## Test vectors

`spec/vectors/<provider>/<case>.json`, one case per file:

```json
{
  "description": "Signs a purchase request the way the provider docs show",
  "operation": "sign | buildCreatePayment | verifyCallback | mapStatus",
  "config": { "...": "provider credentials (test values only)" },
  "input": { "...": "operation input" },
  "expected": { "...": "exact output, or { \"error\": \"invalid_signature\" }" }
}
```

Vectors use only test credentials and values taken from, or reproducible with,
the provider's public documentation. Every language runs every vector.
