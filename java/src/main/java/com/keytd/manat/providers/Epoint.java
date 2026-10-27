package com.keytd.manat.providers;

import com.keytd.manat.Capability;
import com.keytd.manat.CreatePaymentRequest;
import com.keytd.manat.CreatePaymentResult;
import com.keytd.manat.Currency;
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
import com.keytd.manat.internal.Http;
import com.keytd.manat.internal.Json;
import com.keytd.manat.internal.Values;

import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.util.Base64;
import java.util.Collections;
import java.util.EnumSet;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.Set;
import java.util.regex.Pattern;

/**
 * Epoint (API v1).
 * Every message is data (base64 JSON) + signature = base64(sha1(privateKey + data + privateKey)).
 */
public final class Epoint implements PaymentProvider {
    private static final String NAME = "epoint";
    private static final String DEFAULT_BASE_URL = "https://epoint.az/api/1";
    private static final Set<Currency> CURRENCIES = EnumSet.of(Currency.AZN, Currency.USD, Currency.EUR, Currency.RUB);
    private static final Pattern BASE64 = Pattern.compile("^[A-Za-z0-9+/]+={0,2}$");
    private static final Set<Capability> CAPABILITIES = Collections.unmodifiableSet(EnumSet.of(
            Capability.CREATE_PAYMENT, Capability.VERIFY_CALLBACK, Capability.GET_STATUS, Capability.REFUND));

    private final Http http;
    private final String baseUrl;
    private final String publicKey;
    private final String privateKey;

    private Epoint(Builder builder) {
        if (builder.publicKey == null || builder.publicKey.isEmpty()) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST, "epoint: publicKey is required");
        }
        if (builder.privateKey == null || builder.privateKey.isEmpty()) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST, "epoint: privateKey is required");
        }
        this.publicKey = builder.publicKey;
        this.privateKey = builder.privateKey;
        this.baseUrl = (builder.baseUrl != null ? builder.baseUrl : DEFAULT_BASE_URL).replaceAll("/+$", "");
        this.http = new Http(NAME, builder.transport, builder.timeout);
    }

    public static Builder builder() {
        return new Builder();
    }

    public static final class Builder {
        private String publicKey;
        private String privateKey;
        private String baseUrl;
        private Transport transport;
        private Duration timeout;

        private Builder() {
        }

        /** The merchant's public key (i0000000000). */
        public Builder publicKey(String publicKey) {
            this.publicKey = publicKey;
            return this;
        }

        /** The merchant's private key, used to sign requests and check callbacks. */
        public Builder privateKey(String privateKey) {
            this.privateKey = privateKey;
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

        public Epoint build() {
            return new Epoint(this);
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

    /** base64(sha1(privateKey + data + privateKey)), the signature on every request and callback. */
    public static String signature(String privateKey, String data) {
        return Crypto.base64(Crypto.digest("SHA-1", (privateKey + data + privateKey).getBytes(StandardCharsets.UTF_8)));
    }

    /** Encodes a payload the way the adapter sends it: JSON text, then base64, then signed. Keys: data, signature. */
    public static Map<String, String> encode(String privateKey, String json) {
        String data = Crypto.base64(json.getBytes(StandardCharsets.UTF_8));
        Map<String, String> out = new LinkedHashMap<>();
        out.put("data", data);
        out.put("signature", signature(privateKey, data));
        return out;
    }

    /** Maps a payment status. */
    public static PaymentStatus mapStatus(String status) {
        switch (status == null ? "" : status) {
            case "new":
                return PaymentStatus.PENDING;
            case "success":
                return PaymentStatus.PAID;
            case "returned":
                return PaymentStatus.REFUNDED;
            case "failed":
            case "error":
                return PaymentStatus.FAILED;
            default:
                throw new ManatError(ManatError.Code.PROVIDER_ERROR, "epoint: unknown status \"" + status + "\"", NAME, null, null, null, null);
        }
    }

    @Override
    public CreatePaymentResult createPayment(CreatePaymentRequest request) {
        if (request.intent() == Intent.AUTHORIZE) {
            throw new ManatError(ManatError.Code.UNSUPPORTED_OPERATION,
                    "epoint: pre-authorisation is not supported (Epoint statuses do not tell a hold from a capture)", NAME, null, null, null, null);
        }
        Money amount = request.amount();
        if (!CURRENCIES.contains(amount.currency())) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST, "epoint: currency " + amount.currency() + " is not supported (AZN, USD, EUR, RUB)");
        }
        if (request.orderId().length() > 255) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST, "epoint: orderId is longer than 255 characters");
        }
        Map<String, Object> payload = new LinkedHashMap<>();
        payload.put("public_key", publicKey);
        payload.put("amount", amount.toDecimalString());
        payload.put("currency", amount.currency().name());
        payload.put("language", request.language() == null ? "az" : request.language().code());
        payload.put("order_id", request.orderId());
        if (request.description() != null && !request.description().isEmpty()) {
            payload.put("description", Values.truncate(request.description(), 1000));
        }
        payload.put("success_redirect_url", request.returnUrl());
        payload.put("error_redirect_url", request.returnUrl());

        Map<String, Object> response = call("request", payload);
        if (!"success".equals(response.get("status")) || !Values.truthy(response.get("redirect_url")) || !Values.truthy(response.get("transaction"))) {
            throw error("payment was not created", response);
        }
        return new CreatePaymentResult(Values.text(response.get("transaction")), PaymentStatus.PENDING,
                NextAction.redirect(Values.text(response.get("redirect_url"))), response);
    }

    @Override
    public PaymentEvent verifyCallback(IncomingCallback callback) {
        String data = callback.bodyField("data");
        String signature = callback.bodyField("signature");
        Object body = callback.bodyText() != null ? callback.bodyText() : callback.bodyMap();
        // An empty data still has a valid signature (sha1(key + key)), so it is rejected outright.
        if (data == null || data.isEmpty() || signature == null || signature.isEmpty()) {
            throw new ManatError(ManatError.Code.INVALID_SIGNATURE, "epoint: callback has no data/signature (the browser redirect is not signed; use getStatus)",
                    NAME, null, null, body, null);
        }
        if (!Crypto.safeEquals(signature, signature(privateKey, data))) {
            throw new ManatError(ManatError.Code.INVALID_SIGNATURE, "epoint: callback signature does not match", NAME, null, null, body, null);
        }
        Map<String, Object> payload = decodeData(data);
        if (payload == null || !Values.truthy(payload.get("transaction"))) {
            throw new ManatError(ManatError.Code.PROVIDER_ERROR, "epoint: signed callback has no transaction", NAME, null, null, payload, null);
        }
        Object orderId = payload.get("order_id");
        return new PaymentEvent(Values.text(payload.get("transaction")), Values.truthy(orderId) ? Values.text(orderId) : null,
                mapStatus(Values.string(payload.get("status"))), null, null, payload);
    }

    @Override
    public PaymentState getStatus(String paymentId) {
        Map<String, Object> payload = new LinkedHashMap<>();
        payload.put("public_key", publicKey);
        payload.put("transaction", paymentId);
        Map<String, Object> response = call("get-status", payload);
        // server_error means Epoint could not check the payment with the bank; the outcome is unknown.
        if ("server_error".equals(response.get("status")) || !Values.truthy(response.get("transaction"))) {
            throw error("status is not available", response);
        }
        if (!paymentId.equals(response.get("transaction"))) {
            throw error("status is for transaction " + Values.text(response.get("transaction")) + ", not " + paymentId, response);
        }
        return new PaymentState(paymentId, mapStatus(Values.string(response.get("status"))), null, null, null, response);
    }

    @Override
    public RefundResult refund(RefundRequest request) {
        Map<String, Object> payload = new LinkedHashMap<>();
        payload.put("public_key", publicKey);
        payload.put("language", "az");
        payload.put("transaction", request.paymentId());
        if (request.amount() != null) {
            payload.put("amount", request.amount().toDecimalString());
        }
        payload.put("currency", request.amount() != null ? request.amount().currency().name() : "AZN");

        Map<String, Object> response = call("reverse", payload);
        if (!"success".equals(response.get("status"))) {
            throw error("refund failed", response);
        }
        PaymentState state = getStatus(request.paymentId());
        // Epoint has no partial-refund status: a reversed payment still reported as "success" was partly refunded.
        PaymentStatus status = state.status() == PaymentStatus.PAID ? PaymentStatus.PARTIALLY_REFUNDED : state.status();
        Map<String, Object> raw = new LinkedHashMap<>();
        raw.put("refund", response);
        raw.put("payment", state.raw());
        return new RefundResult(request.paymentId(), null, status, raw);
    }

    private Map<String, Object> call(String method, Map<String, Object> payload) {
        Map<String, String> form = encode(privateKey, Json.stringify(payload));
        Http.JsonResponse response = http.json("POST", baseUrl + "/" + method, Collections.emptyMap(), null, form);
        Map<String, Object> body = Values.map(response.data);
        if (body == null) {
            throw new ManatError(ManatError.Code.PROVIDER_ERROR, "epoint: unexpected response (HTTP " + response.status + ")", NAME, null, null, response.data, null);
        }
        if (response.status >= 400) {
            throw error("HTTP " + response.status, body);
        }
        return body;
    }

    private static ManatError error(String what, Map<String, Object> response) {
        String message = Values.truthy(response.get("message")) ? Values.text(response.get("message")) : null;
        String code = Values.truthy(response.get("code")) ? Values.text(response.get("code")) : null;
        String text = response.get("message") != null ? Values.text(response.get("message")) : what;
        return new ManatError(ManatError.Code.PROVIDER_ERROR, "epoint: " + text, NAME, code, message, response, null);
    }

    private static Map<String, Object> decodeData(String data) {
        if (!BASE64.matcher(data).matches()) {
            throw new ManatError(ManatError.Code.PROVIDER_ERROR, "epoint: callback data is not base64", NAME, null, null, data, null);
        }
        try {
            return Values.map(Json.parse(new String(Base64.getDecoder().decode(data), StandardCharsets.UTF_8)));
        } catch (IllegalArgumentException notJson) {
            throw new ManatError(ManatError.Code.PROVIDER_ERROR, "epoint: callback data is not JSON", NAME, null, null, data, notJson);
        }
    }
}
