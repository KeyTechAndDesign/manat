package com.keytd.manat.providers;

import com.keytd.manat.Capability;
import com.keytd.manat.CreatePaymentRequest;
import com.keytd.manat.CreatePaymentResult;
import com.keytd.manat.Currency;
import com.keytd.manat.Environment;
import com.keytd.manat.IncomingCallback;
import com.keytd.manat.Intent;
import com.keytd.manat.ManatError;
import com.keytd.manat.Money;
import com.keytd.manat.NextAction;
import com.keytd.manat.PaymentEvent;
import com.keytd.manat.PaymentProvider;
import com.keytd.manat.PaymentState;
import com.keytd.manat.PaymentStatus;
import com.keytd.manat.RefundRequest;
import com.keytd.manat.RefundResult;
import com.keytd.manat.http.Transport;
import com.keytd.manat.internal.Crypto;
import com.keytd.manat.internal.Form;
import com.keytd.manat.internal.Http;
import com.keytd.manat.internal.Values;

import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.util.Arrays;
import java.util.Collections;
import java.util.EnumSet;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.Set;
import java.util.regex.Pattern;

/**
 * GoldenPay gateway (rest-pg.goldenpay.az).
 * There is no public documentation. Built from two community SDKs (orkhanahmadov/goldenpay, 2021, and
 * Rasimoghlu/laravel-golden-pay-payment, 2026) and checked against the test host's replies:
 *
 * <ul>
 * <li>{@code POST /getPaymentKey} with {@code hashCode} = md5(authKey + merchantName + amount + description); amounts are
 * integers in qəpik and the API has no currency field, so only AZN is accepted.</li>
 * <li>The customer pays on {@code {host}/pay/{paymentKey}}. The redirect back carries only the payment key and is not
 * signed, so verifyCallback re-reads the payment with {@code GET /getPaymentResult} (hash_code = md5(authKey + key)).</li>
 * <li>Every reply is HTTP 200 with {@code status: {code, message}} at the top level (not under {@code response}, as the
 * newer SDK assumes). Code 1 is success; an unknown payment key answers 900 "Internal exception".</li>
 * <li>Refunds are {@code POST /reversePaymentKey} with an amount and {@code secretKey} = md5(hashCode + amount). There is
 * no pre-auth and no callback URL in the API.</li>
 * </ul>
 */
public final class GoldenPay implements PaymentProvider {
    private static final String NAME = "goldenpay";
    private static final String TEST_URL = "https://rest-newpgtest.goldenpay.az";
    private static final String PRODUCTION_URL = "https://rest-pg.goldenpay.az";
    private static final long SUCCESS = 1;
    private static final long REVERSED = 817;
    /** getPaymentResult codes that describe a payment; others (801–812, 900, 901…) are request errors. */
    private static final Set<Long> PAYMENT_CODES = new HashSet<>(Arrays.asList(SUCCESS, 813L, 814L, 815L, 816L, REVERSED, 818L, 819L));
    /** The SDKs cap the description at 500 bytes. */
    private static final int MAX_DESCRIPTION_BYTES = 500;
    private static final Pattern PAYMENT_KEY = Pattern.compile("^[A-Za-z0-9-]{1,64}$");
    private static final Set<Capability> CAPABILITIES = Collections.unmodifiableSet(EnumSet.of(
            Capability.CREATE_PAYMENT, Capability.VERIFY_CALLBACK, Capability.GET_STATUS, Capability.REFUND));

    private final Http http;
    private final String baseUrl;
    private final String authKey;
    private final String merchantName;

    private GoldenPay(Builder builder) {
        if (builder.authKey == null || builder.authKey.isEmpty()) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST, "goldenpay: authKey is required");
        }
        if (builder.merchantName == null || builder.merchantName.isEmpty()) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST, "goldenpay: merchantName is required");
        }
        this.authKey = builder.authKey;
        this.merchantName = builder.merchantName;
        String base = builder.baseUrl != null ? builder.baseUrl : builder.environment == Environment.TEST ? TEST_URL : PRODUCTION_URL;
        this.baseUrl = base.replaceAll("/+$", "");
        this.http = new Http(NAME, builder.transport, builder.timeout);
    }

    public static Builder builder() {
        return new Builder();
    }

    public static final class Builder {
        private String authKey;
        private String merchantName;
        private Environment environment = Environment.PRODUCTION;
        private String baseUrl;
        private Transport transport;
        private Duration timeout;

        private Builder() {
        }

        public Builder authKey(String authKey) {
            this.authKey = authKey;
            return this;
        }

        public Builder merchantName(String merchantName) {
            this.merchantName = merchantName;
            return this;
        }

        /** Default {@link Environment#PRODUCTION}. */
        public Builder environment(Environment environment) {
            this.environment = environment;
            return this;
        }

        public Builder baseUrl(String baseUrl) {
            this.baseUrl = baseUrl;
            return this;
        }

        public Builder transport(Transport transport) {
            this.transport = transport;
            return this;
        }

        /** Per-request timeout; default 30 seconds. */
        public Builder timeout(Duration timeout) {
            this.timeout = timeout;
            return this;
        }

        public GoldenPay build() {
            return new GoldenPay(this);
        }
    }

    @Override
    public String name() {
        return NAME;
    }

    @Override
    public Set<Capability> capabilities() {
        return CAPABILITIES;
    }

    /** @param code {@code status.code} of a getPaymentResult reply, or null */
    public static PaymentStatus mapStatus(Long code) {
        int value = code == null ? -1 : code.intValue();
        switch (value) {
            case 1:
                return PaymentStatus.PAID;
            case 813: // not finished
            case 818: // in progress
                return PaymentStatus.PENDING;
            case 814: // cancelled by the customer
            case 819: // payment page timed out
                return PaymentStatus.CANCELLED;
            case 815:
            case 816:
                return PaymentStatus.FAILED;
            case 817:
                return PaymentStatus.REFUNDED;
            default:
                throw new ManatError(ManatError.Code.PROVIDER_ERROR, "goldenpay: status code " + code + " does not describe a payment", NAME, null, null, null, null);
        }
    }

    @Override
    public CreatePaymentResult createPayment(CreatePaymentRequest request) {
        if (request.intent() == Intent.AUTHORIZE) {
            throw new ManatError(ManatError.Code.UNSUPPORTED_OPERATION, "goldenpay: pre-authorisation is not supported", NAME, null, null, null, null);
        }
        requireAzn(request.amount());
        // GoldenPay has no order id field; the description is the only text that reaches its panel.
        String description = truncateBytes(request.description() != null ? request.description() : request.orderId(), MAX_DESCRIPTION_BYTES);
        long amount = request.amount().amount();
        Map<String, Object> body = new LinkedHashMap<>();
        body.put("merchantName", merchantName);
        body.put("amount", amount);
        body.put("lang", request.language() == null ? "az" : request.language().code());
        body.put("description", description);
        body.put("redirectUrl", request.returnUrl());
        body.put("hashCode", Crypto.md5Hex(authKey + merchantName + amount + description));
        Map<String, Object> response = call("POST", "getPaymentKey", body);
        Object key = response.get("paymentKey");
        if (!Long.valueOf(SUCCESS).equals(statusCode(response)) || !Values.truthy(key)) {
            throw error("payment was not created", response);
        }
        String paymentKey = Values.text(key);
        return new CreatePaymentResult(paymentKey, PaymentStatus.PENDING, NextAction.redirect(baseUrl + "/pay/" + encodeUriComponent(paymentKey)), response);
    }

    @Override
    public PaymentEvent verifyCallback(IncomingCallback callback) {
        String key = callback.query("payment_key");
        if (key == null) {
            key = callback.query("paymentKey");
        }
        if (key == null) {
            key = callback.bodyField("payment_key");
        }
        if (key == null) {
            key = callback.bodyField("paymentKey");
        }
        if (key == null || !PAYMENT_KEY.matcher(key).matches()) {
            throw new ManatError(ManatError.Code.INVALID_SIGNATURE, "goldenpay: redirect has no payment_key", NAME, null, null, callback.toMap(), null);
        }
        // The redirect is unsigned; trust only what getPaymentResult says about this key.
        return PaymentEvent.fromState(getStatus(key));
    }

    @Override
    public PaymentState getStatus(String paymentId) {
        if (!PAYMENT_KEY.matcher(paymentId).matches()) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST, "goldenpay: invalid paymentId \"" + paymentId + "\"");
        }
        Map<String, String> query = new LinkedHashMap<>();
        query.put("payment_key", paymentId);
        query.put("hash_code", Crypto.md5Hex(authKey + paymentId));
        Map<String, Object> response = call("GET", "getPaymentResult?" + Form.encode(query), null);
        Long code = statusCode(response);
        if (code == null || !PAYMENT_CODES.contains(code)) {
            throw error("status is not available", response);
        }
        Object key = response.get("paymentKey");
        if (Values.truthy(key) && !paymentId.equals(key)) {
            throw error("result is for payment " + Values.text(key) + ", not " + paymentId, response);
        }
        Long amount = Values.integer(response.get("amount"));
        return new PaymentState(paymentId, mapStatus(code), amount != null && amount > 0 ? Money.of(amount, Currency.AZN) : null, null, null, response);
    }

    @Override
    public RefundResult refund(RefundRequest request) {
        String paymentId = request.paymentId();
        if (request.amount() != null) {
            requireAzn(request.amount());
        }
        // reversePaymentKey needs an amount, and GoldenPay has no partial-refund status, so the payment is read first.
        PaymentState payment = getStatus(paymentId);
        Money amount = request.amount() != null ? request.amount() : payment.amount();
        if (amount == null) {
            throw new ManatError(ManatError.Code.PROVIDER_ERROR, "goldenpay: payment " + paymentId + " has no amount to refund", NAME, null, null, payment.raw(), null);
        }
        String hashCode = Crypto.md5Hex(authKey + paymentId);
        Map<String, Object> body = new LinkedHashMap<>();
        body.put("paymentKey", paymentId);
        body.put("hashCode", hashCode);
        body.put("amount", amount.amount());
        body.put("secretKey", Crypto.md5Hex(hashCode + amount.amount()));
        Map<String, Object> response = call("POST", "reversePaymentKey", body);
        // The 2026 SDK treats 817 (reversed) as success; 1 is the API's general success code.
        Long code = statusCode(response);
        if (code == null || (code != REVERSED && code != SUCCESS)) {
            throw error("refund failed", response);
        }
        boolean partial = payment.amount() != null && amount.amount() < payment.amount().amount();
        Map<String, Object> raw = new LinkedHashMap<>();
        raw.put("refund", response);
        raw.put("payment", payment.raw());
        return new RefundResult(paymentId, null, partial ? PaymentStatus.PARTIALLY_REFUNDED : PaymentStatus.REFUNDED, raw);
    }

    private Map<String, Object> call(String method, String path, Object body) {
        Http.JsonResponse response = http.json(method, baseUrl + "/" + path, Collections.emptyMap(), body, null);
        Map<String, Object> data = Values.map(response.data);
        if (data == null || !Values.truthy(data.get("status"))) {
            throw new ManatError(ManatError.Code.PROVIDER_ERROR, "goldenpay: unexpected response (HTTP " + response.status + ")", NAME, null, null, response.data, null);
        }
        if (response.status >= 400) {
            throw error("HTTP " + response.status, data);
        }
        return data;
    }

    private static Long statusCode(Map<String, Object> response) {
        return Values.integer(Values.get(response, "status", "code"));
    }

    private static ManatError error(String what, Map<String, Object> response) {
        Object code = Values.get(response, "status", "code");
        String message = Values.text(Values.get(response, "status", "message"));
        // Code 1 is GoldenPay saying "success" (e.g. a result for another key), so only `what` describes the problem.
        boolean fromProvider = code != null && !Long.valueOf(SUCCESS).equals(Values.integer(code));
        boolean hasMessage = fromProvider && message != null && !message.isEmpty();
        return new ManatError(ManatError.Code.PROVIDER_ERROR, "goldenpay: " + (hasMessage ? message : what), NAME,
                fromProvider ? Values.text(code) : null, hasMessage ? message : null, response, null);
    }

    private static void requireAzn(Money money) {
        if (money.currency() != Currency.AZN) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST, "goldenpay: only AZN is supported, got " + money.currency());
        }
    }

    /** Cuts to at most {@code max} UTF-8 bytes without splitting a character. */
    private static String truncateBytes(String value, int max) {
        if (value.getBytes(StandardCharsets.UTF_8).length <= max) {
            return value;
        }
        StringBuilder out = new StringBuilder();
        int bytes = 0;
        for (int i = 0; i < value.length(); ) {
            int codePoint = value.codePointAt(i);
            String character = new String(Character.toChars(codePoint));
            int size = character.getBytes(StandardCharsets.UTF_8).length;
            if (bytes + size > max) {
                break;
            }
            out.append(character);
            bytes += size;
            i += Character.charCount(codePoint);
        }
        return out.toString();
    }

    /** JavaScript's encodeURIComponent. */
    private static String encodeUriComponent(String value) {
        return java.net.URLEncoder.encode(value, StandardCharsets.UTF_8)
                .replace("+", "%20").replace("%21", "!").replace("%27", "'").replace("%28", "(").replace("%29", ")").replace("%7E", "~");
    }
}
