# Manat for .NET

One API for Azerbaijani online payment providers: Kapital Bank, Azericard, Payriff, Epoint, GoldenPay and Yığım.
netstandard2.0, so it runs on .NET Framework 4.6.1+ as well as .NET 6, 8, 10…; no package dependencies.

Not on NuGet yet. Until it is, reference the project (`dotnet/src/KeyTD.Manat`) or build a package with `dotnet pack`.

```bash
dotnet add package KeyTD.Manat
```

```csharp
using KeyTD.Manat;
using KeyTD.Manat.Providers;

var pay = new ManatClient(new KapitalBankProvider(new KapitalBankOptions
{
    Username = config["KB_USER"],
    Password = config["KB_PASSWORD"],
}));

var result = await pay.CreatePaymentAsync(new CreatePaymentRequest
{
    OrderId = "A-1001",
    Amount = Money.Azn("25.50"),   // 2550 qəpik; floats are never accepted
    ReturnUrl = "https://shop.az/payment/return",
});

// Send the customer on: a redirect, or (Azericard) an auto-submitted form.
if (result.Next.Type == NextAction.ActionType.Redirect)
{
    return Redirect(result.Next.Url!);
}
```

Store `result.PaymentId` with the order. It is what `GetStatusAsync`, `RefundAsync`, `CaptureAsync` and `CancelAsync` take.

## Providers

```csharp
new KapitalBankProvider(new KapitalBankOptions { Username = user, Password = password, Environment = ProviderEnvironment.Test });
new AzericardProvider(new AzericardOptions
{
    Terminal = terminal, PrivateKey = File.ReadAllText("merchant.pem"), GatewayPublicKey = File.ReadAllText("gateway.pem"),
    MerchantName = "My Shop", MerchantUrl = "https://shop.az", Environment = ProviderEnvironment.Test,
});
new PayriffProvider(new PayriffOptions { SecretKey = secretKey });
new EpointProvider(new EpointOptions { PublicKey = publicKey, PrivateKey = privateKey });
new GoldenPayProvider(new GoldenPayOptions { AuthKey = authKey, MerchantName = merchantName, Environment = ProviderEnvironment.Test });
new YigimProvider(new YigimOptions { Merchant = merchant, ApiKey = apiKey, Biller = biller, Environment = ProviderEnvironment.Test });
```

| Provider     | CreatePayment | VerifyCallback | GetStatus | Refund | Capture | Cancel |
|--------------|:-:|:-:|:-:|:-:|:-:|:-:|
| Kapital Bank | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Azericard    | ✓ (form POST) | ✓ | ✓ | ✓ | ✓ | ✓ |
| Payriff      | ✓ | ✓ | ✓ | ✓ | ✓ | — |
| Epoint       | ✓ | ✓ | ✓ | ✓ | — | — |
| GoldenPay    | ✓ | ✓ | ✓ | ✓ | — | — |
| Yığım        | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |

`pay.Supports(Capability.Capture)` tells you at runtime; an unsupported call throws `unsupported_operation`.

## Callbacks

Pass what your handler received. Signed callbacks are verified in constant time; unsigned redirects and
webhooks are never trusted: the adapter re-reads the payment from the provider's API.

```csharp
using var reader = new StreamReader(Request.Body);
var evt = await pay.VerifyCallbackAsync(new IncomingCallback(Request.Method)
    .WithQuery(Request.Query)                 // ASP.NET Core's IQueryCollection works as is
    .WithBody(await reader.ReadToEndAsync()));

if (evt.Status == PaymentStatus.Paid)
{
    // fulfil order evt.PaymentId; for Azericard store evt.ProviderRef for refunds
}
```

## Refunds, capture, cancel

```csharp
await pay.RefundAsync(new RefundRequest { PaymentId = id });                                 // full refund
await pay.RefundAsync(new RefundRequest { PaymentId = id, Amount = Money.Azn("10.00") });    // partial
await pay.CaptureAsync(new PaymentAction { PaymentId = id });                                // after Intent = Intent.Authorize
await pay.RefundAsync(new RefundRequest { PaymentId = id, ProviderRef = providerRef });      // Azericard needs the callback's ProviderRef
```

## Errors

Everything throws `ManatException`. `error.Code` is one of `InvalidRequest`, `InvalidSignature`, `ProviderError`,
`NetworkError`, `UnsupportedOperation` (`error.Code.ToValue()` gives the spec's `"provider_error"`); for provider errors
`ProviderCode` and `ProviderMessage` hold what the provider said and `Raw` its whole reply.

## HTTP

Every options class takes `HttpClient` (pass one from `IHttpClientFactory`) and `Timeout` (default 30 seconds). Without a client
a shared one is used, with redirects off. On .NET Framework, enable TLS 1.2 (`ServicePointManager.SecurityProtocol`) if your
version does not default to it. All methods are async and take a `CancellationToken`.

## Tests

The adapters run the same JSON test vectors as every other language (`spec/vectors`). Without a local .NET SDK:

```bash
docker run --rm -v "$PWD":/repo -w /repo/dotnet mcr.microsoft.com/dotnet/sdk:10.0 dotnet test tests/KeyTD.Manat.Tests -f net10.0
```
