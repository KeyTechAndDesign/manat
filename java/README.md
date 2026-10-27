# Manat for Java / Kotlin

One API for Azerbaijani online payment providers: Kapital Bank, Azericard, Payriff, Epoint, GoldenPay and Yığım.
Java 11+, works from Kotlin as is, no runtime dependencies (java.net.http, java.security and a small built-in JSON reader).

```xml
<dependency>
    <groupId>com.keytd</groupId>
    <artifactId>manat</artifactId>
    <version>0.2.2</version>
</dependency>
```

Gradle: `implementation("com.keytd:manat:0.2.2")`

```java
import com.keytd.manat.*;
import com.keytd.manat.providers.KapitalBank;

Manat pay = new Manat(KapitalBank.builder()
        .username(System.getenv("KB_USER"))
        .password(System.getenv("KB_PASSWORD"))
        .build());

CreatePaymentResult result = pay.createPayment(
        CreatePaymentRequest.of("A-1001", Money.azn("25.50"), "https://shop.az/payment/return")); // 2550 qəpik

// Send the customer on: a redirect, or (Azericard) an auto-submitted form.
if (result.next().type() == NextAction.Type.REDIRECT) {
    response.sendRedirect(result.next().url());
}
```

Store `result.paymentId()` with the order. It is what `getStatus`, `refund`, `capture` and `cancel` take.

## Providers

```java
KapitalBank.builder().username(user).password(password).environment(Environment.TEST).build();
Azericard.builder().terminal(terminal).privateKey(merchantPem).gatewayPublicKey(gatewayPem)
        .merchantName("My Shop").merchantUrl("https://shop.az").environment(Environment.TEST).build();
Payriff.builder().secretKey(secretKey).build();
Epoint.builder().publicKey(publicKey).privateKey(privateKey).build();
GoldenPay.builder().authKey(authKey).merchantName(merchantName).environment(Environment.TEST).build();
Yigim.builder().merchant(merchant).apiKey(apiKey).biller(biller).environment(Environment.TEST).build();
```

| Provider     | createPayment | verifyCallback | getStatus | refund | capture | cancel |
|--------------|:-:|:-:|:-:|:-:|:-:|:-:|
| Kapital Bank | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Azericard    | ✓ (form POST) | ✓ | ✓ | ✓ | ✓ | ✓ |
| Payriff      | ✓ | ✓ | ✓ | ✓ | ✓ | — |
| Epoint       | ✓ | ✓ | ✓ | ✓ | — | — |
| GoldenPay    | ✓ | ✓ | ✓ | ✓ | — | — |
| Yığım        | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |

`pay.supports(Capability.CAPTURE)` tells you at runtime; an unsupported call throws `unsupported_operation`.

## Callbacks

Pass what your handler received. Signed callbacks are verified in constant time; unsigned redirects and
webhooks are never trusted: the adapter re-reads the payment from the provider's API. Epoint's browser redirect
is unsigned and rejected with `invalid_signature`; call `getStatus` there.

```java
PaymentEvent event = pay.verifyCallback(IncomingCallback.of(request.getMethod())
        .withQuery(request.getParameterMap())   // String, String[] or List values
        .withBody(rawBody));                    // raw text, or a Map your framework parsed

if (event.status() == PaymentStatus.PAID) {
    // fulfil order event.paymentId(); for Azericard store event.providerRef() for refunds
}
```

## Refunds, capture, cancel

```java
pay.refund(RefundRequest.of(paymentId));                                  // full refund
pay.refund(RefundRequest.of(paymentId).withAmount(Money.azn("10.00")));   // partial
pay.capture(PaymentAction.of(paymentId));                                 // after withIntent(Intent.AUTHORIZE)
pay.refund(RefundRequest.of(paymentId).withProviderRef(providerRef));     // Azericard needs the callback's providerRef
```

## Errors

Everything throws `ManatError` (unchecked). `error.code()` is one of `INVALID_REQUEST`, `INVALID_SIGNATURE`,
`PROVIDER_ERROR`, `NETWORK_ERROR`, `UNSUPPORTED_OPERATION`; for provider errors `providerCode()` and
`providerMessage()` hold what the provider said and `raw()` its whole reply.

## HTTP

Requests go through `java.net.http.HttpClient` (HTTP/1.1, no redirects, TLS verified) with a 30-second timeout.
Every builder takes `.timeout(Duration)` and `.transport(Transport)`; `new JdkTransport(myHttpClient)` keeps the default
transport with your own client (proxy, SSL context), or implement `com.keytd.manat.http.Transport` for another library.

The API is synchronous. Call it from your own executor if you need it off the request thread.

## Tests

The adapters run the same JSON test vectors as every other language (`spec/vectors`):

```bash
cd java && mvn test
```
