# Manat for Go

One API for Azerbaijani online payment providers: Kapital Bank, Azericard, Payriff, Epoint, GoldenPay and Yığım.
Go 1.21+, standard library only.

```bash
go get github.com/KeyTechAndDesign/manat/go
```

```go
import manat "github.com/KeyTechAndDesign/manat/go"

kb, err := manat.NewKapitalBank(manat.KapitalBankConfig{Username: os.Getenv("KB_USER"), Password: os.Getenv("KB_PASSWORD")})
if err != nil {
	return err
}
pay := manat.New(kb)

amount, _ := manat.AZN("25.50") // 2550 qəpik; floats are never accepted
res, err := pay.CreatePayment(ctx, manat.CreatePaymentRequest{
	OrderID:   "A-1001",
	Amount:    amount,
	ReturnURL: "https://shop.az/payment/return",
})
if err != nil {
	return err
}
// res.Next.Type is "redirect" (send the browser to res.Next.URL) or, for Azericard, "form" (POST res.Next.Fields).
```

Store `res.PaymentID` with the order. It is what `GetStatus`, `Refund`, `Capture` and `Cancel` take.

## Providers

```go
manat.NewKapitalBank(manat.KapitalBankConfig{Username: u, Password: p, Environment: "test"})
manat.NewAzericard(manat.AzericardConfig{Terminal: t, PrivateKey: merchantPEM, GatewayPublicKey: gatewayPEM,
	MerchantName: "My Shop", MerchantURL: "https://shop.az", Environment: "test"})
manat.NewPayriff(manat.PayriffConfig{SecretKey: key})
manat.NewEpoint(manat.EpointConfig{PublicKey: pub, PrivateKey: priv})
manat.NewGoldenPay(manat.GoldenPayConfig{AuthKey: key, MerchantName: name, Environment: "test"})
manat.NewYigim(manat.YigimConfig{Merchant: m, APIKey: key, Biller: b, Environment: "test"})
```

| Provider     | CreatePayment | VerifyCallback | GetStatus | Refund | Capture | Cancel |
|--------------|:-:|:-:|:-:|:-:|:-:|:-:|
| Kapital Bank | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Azericard    | ✓ (form POST) | ✓ | ✓ | ✓ | ✓ | ✓ |
| Payriff      | ✓ | ✓ | ✓ | ✓ | ✓ | — |
| Epoint       | ✓ | ✓ | ✓ | ✓ | — | — |
| GoldenPay    | ✓ | ✓ | ✓ | ✓ | — | — |
| Yığım        | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |

`pay.Supports(manat.CapCapture)` tells you at runtime; an unsupported call returns `unsupported_operation`. Every config
also takes `BaseURL` (`GatewayURL` for Azericard), `HTTPClient` (any `Do(*http.Request)`, such as your `*http.Client`) and `Timeout` (default 30s).

## Callbacks

Signed callbacks are verified in constant time; unsigned redirects and webhooks are never trusted: the adapter re-reads
the payment from the provider's API. Epoint's browser redirect is unsigned and rejected with `invalid_signature`; call
`GetStatus` there.

```go
cb, err := manat.CallbackFromRequest(r)
if err != nil {
	http.Error(w, "bad request", http.StatusBadRequest)
	return
}
event, err := pay.VerifyCallback(r.Context(), cb)
if err == nil && event.Status == manat.StatusPaid {
	// fulfil order event.PaymentID; for Azericard store event.ProviderRef for refunds
}
```

## Refunds, capture, cancel

```go
pay.Refund(ctx, manat.RefundRequest{PaymentID: id})                         // full refund
pay.Refund(ctx, manat.RefundRequest{PaymentID: id, Amount: &partial})       // partial
pay.Capture(ctx, manat.PaymentAction{PaymentID: id})                        // after Intent: manat.IntentAuthorize
pay.Refund(ctx, manat.RefundRequest{PaymentID: id, ProviderRef: ref})       // Azericard needs the callback's ProviderRef
```

## Errors

Every error is a `*manat.Error`. `Code` is one of `invalid_request`, `invalid_signature`, `provider_error`,
`network_error`, `unsupported_operation`; for provider errors `ProviderCode` and `ProviderMessage` hold what the provider
said and `Raw` its whole reply. `manat.IsError(err, manat.ProviderError)` checks the code.

## Tests

The adapters run the same JSON test vectors as every other language (`spec/vectors`):

```bash
cd go && go test ./...
```
