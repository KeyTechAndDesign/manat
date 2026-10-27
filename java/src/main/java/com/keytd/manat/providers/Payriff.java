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
import com.keytd.manat.PaymentAction;
import com.keytd.manat.PaymentEvent;
import com.keytd.manat.PaymentProvider;
import com.keytd.manat.PaymentState;
import com.keytd.manat.PaymentStatus;
import com.keytd.manat.RefundRequest;
import com.keytd.manat.RefundResult;
import com.keytd.manat.http.Transport;
import com.keytd.manat.internal.Http;
import com.keytd.manat.internal.Json;
import com.keytd.manat.internal.Values;

import java.time.Duration;
import java.util.Collections;
import java.util.EnumSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.regex.Pattern;

/**
 * Payriff (API v3).
 * The webhook is unsigned, so verifyCallback re-reads the order. There is no void for a pre-auth.
 */
public final class Payriff implements PaymentProvider {
    private static final String NAME = "payriff";
    private static final String DEFAULT_BASE_URL = "https://api.payriff.com";
    private static final String SUCCESS = "00000";
    private static final Pattern ORDER_ID = Pattern.compile("^[A-Za-z0-9-]{1,64}$");
    private static final Set<Capability> CAPABILITIES = Collections.unmodifiableSet(EnumSet.of(
            Capability.CREATE_PAYMENT, Capability.VERIFY_CALLBACK, Capability.GET_STATUS, Capability.REFUND, Capability.CAPTURE));

    private final Http http;
    private final String baseUrl;
    private final String secretKey;

    private Payriff(Builder builder) {
        if (builder.secretKey == null || builder.secretKey.isEmpty()) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST, "payriff: secretKey is required");
        }
        this.secretKey = builder.secretKey;
        this.baseUrl = (builder.baseUrl != null ? builder.baseUrl : DEFAULT_BASE_URL).replaceAll("/+$", "");
        this.http = new Http(NAME, builder.transport, builder.timeout);
    }

    public static Builder builder() {
        return new Builder();
    }

    public static final class Builder {
        private String secretKey;
        private String baseUrl;
        private Transport transport;
        private Duration timeout;

        private Builder() {
        }

        /** The application's secret key; a "Development" application's key is test mode. */
        public Builder secretKey(String secretKey) {
            this.secretKey = secretKey;
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

        public Payriff build() {
            return new Payriff(this);
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

    /** Maps payload.paymentStatus. */
    public static PaymentStatus mapStatus(String status) {
        switch (status == null ? "" : status) {
            case "CREATED":
            case "PENDING":
            case "ACCEPTED":
            case "IN_REVIEW":
                return PaymentStatus.PENDING;
            case "APPROVED":
            case "PAID":
            case "PARTIAL": // pre-auth partially captured
            case "CASH":
                return PaymentStatus.PAID;
            case "PREAUTH_APPROVED":
                return PaymentStatus.AUTHORIZED;
            case "REFUNDED":
            case "REFUND_IN_PROGRESS":
                return PaymentStatus.REFUNDED;
            case "PARTIAL_REFUND":
                return PaymentStatus.PARTIALLY_REFUNDED;
            case "CANCELED":
            case "EXPIRED":
            case "REVERSE":
            case "PREAUTH_EXPIRED":
                return PaymentStatus.CANCELLED;
            case "DECLINED":
                return PaymentStatus.FAILED;
            default:
                throw new ManatError(ManatError.Code.PROVIDER_ERROR, "payriff: unknown paymentStatus \"" + status + "\"", NAME, null, null, null, null);
        }
    }

    @Override
    public CreatePaymentResult createPayment(CreatePaymentRequest request) {
        if (request.callbackUrl() != null && !request.callbackUrl().startsWith("https://")) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST, "payriff: callbackUrl must be an HTTPS URL");
        }
        Money amount = request.amount();
        Map<String, Object> body = new LinkedHashMap<>();
        body.put("amount", amount.toNumber());
        body.put("currency", amount.currency().name());
        body.put("language", (request.language() == null ? "az" : request.language().code()).toUpperCase(Locale.ROOT));
        body.put("operation", request.intent() == Intent.AUTHORIZE ? "PRE_AUTH" : "PURCHASE");
        body.put("description", request.description() != null ? request.description() : "Order " + request.orderId());
        body.put("redirectUrl", request.returnUrl());
        if (request.callbackUrl() != null && !request.callbackUrl().isEmpty()) {
            body.put("callbackUrl", request.callbackUrl());
        }
        if (!request.metadata().isEmpty()) {
            body.put("metadata", request.metadata());
        }
        Map<String, Object> payload = Values.map(call("POST", "/api/v3/orders", body).get("payload"));
        if (payload == null || !Values.truthy(payload.get("orderId")) || !Values.truthy(payload.get("paymentUrl"))) {
            throw new ManatError(ManatError.Code.PROVIDER_ERROR, "payriff: order response has no orderId or paymentUrl", NAME, null, null, payload, null);
        }
        return new CreatePaymentResult(Values.text(payload.get("orderId")), PaymentStatus.PENDING, NextAction.redirect(Values.text(payload.get("paymentUrl"))),
                payload);
    }

    @Override
    public PaymentEvent verifyCallback(IncomingCallback callback) {
        String orderId = callbackOrderId(callback);
        if (orderId == null || orderId.isEmpty()) {
            Object body = callback.bodyText() != null ? callback.bodyText() : callback.bodyMap();
            throw new ManatError(ManatError.Code.INVALID_SIGNATURE, "payriff: callback has no payload.orderId", NAME, null, null, body, null);
        }
        // The webhook is unsigned; trust only what the API says about this order.
        return PaymentEvent.fromState(getStatus(orderId));
    }

    @Override
    public PaymentState getStatus(String paymentId) {
        if (!ORDER_ID.matcher(paymentId).matches()) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST, "payriff: invalid paymentId \"" + paymentId + "\"");
        }
        Map<String, Object> order = Values.map(call("GET", "/api/v3/orders/" + paymentId, null).get("payload"));
        if (order == null || !Values.truthy(order.get("orderId"))) {
            throw new ManatError(ManatError.Code.PROVIDER_ERROR, "payriff: no order in response for " + paymentId, NAME, null, null, order, null);
        }
        Money amount = null;
        if (order.get("amount") != null && Values.truthy(order.get("currencyType"))) {
            amount = Values.money(order.get("amount"), currency(order.get("currencyType")));
        }
        return new PaymentState(Values.text(order.get("orderId")), orderStatus(order), amount, null, null, order);
    }

    @Override
    public RefundResult refund(RefundRequest request) {
        String paymentId = request.paymentId();
        // Payriff rejects a refund without an amount ("Request refund amount is null"), so a full refund sends the order amount.
        Money amount = request.amount() != null ? request.amount() : getStatus(paymentId).amount();
        if (amount == null) {
            throw new ManatError(ManatError.Code.PROVIDER_ERROR, "payriff: order " + paymentId + " has no amount to refund", NAME, null, null, null, null);
        }
        Map<String, Object> body = new LinkedHashMap<>();
        body.put("orderId", paymentId);
        body.put("refundReason", "Refund");
        body.put("amount", amount.toNumber());
        Map<String, Object> envelope = call("POST", "/api/v3/refund", body);
        PaymentState state = getStatus(paymentId);
        Map<String, Object> raw = new LinkedHashMap<>();
        raw.put("refund", envelope);
        raw.put("order", state.raw());
        Object responseId = envelope.get("responseId");
        return new RefundResult(paymentId, Values.truthy(responseId) ? Values.text(responseId) : null, state.status(), raw);
    }

    @Override
    public PaymentState capture(PaymentAction action) {
        Map<String, Object> body = new LinkedHashMap<>();
        body.put("orderId", action.paymentId());
        if (action.amount() != null) {
            body.put("amount", action.amount().toNumber());
        }
        call("POST", "/api/v3/complete", body);
        return getStatus(action.paymentId());
    }

    /** The whole envelope. */
    private Map<String, Object> call(String method, String path, Object body) {
        Http.JsonResponse response = http.json(method, baseUrl + path, Collections.singletonMap("authorization", secretKey), body, null);
        Map<String, Object> data = Values.map(response.data);
        if (data == null || !data.containsKey("code")) {
            throw new ManatError(ManatError.Code.PROVIDER_ERROR, "payriff: unexpected response (HTTP " + response.status + ")", NAME, null, null, response.data, null);
        }
        if (response.status >= 400 || !SUCCESS.equals(data.get("code"))) {
            String code = Values.truthy(data.get("code")) ? Values.text(data.get("code")) : null;
            String message = Values.truthy(data.get("message")) ? Values.text(data.get("message")) : null;
            throw new ManatError(ManatError.Code.PROVIDER_ERROR, "payriff: " + (data.get("message") != null ? Values.text(data.get("message")) : "code " + data.get("code")),
                    NAME, code, message, data, null);
        }
        return data;
    }

    /** After a partial refund Payriff reports the order as REFUNDED; only its transaction says PARTIAL_REFUND. */
    private static PaymentStatus orderStatus(Map<String, Object> order) {
        PaymentStatus status = mapStatus(Values.string(order.get("paymentStatus")));
        List<Object> transactions = Values.list(order.get("transactions"));
        if (status == PaymentStatus.REFUNDED && transactions != null) {
            for (Object transaction : transactions) {
                if ("PARTIAL_REFUND".equals(Values.get(transaction, "status"))) {
                    return PaymentStatus.PARTIALLY_REFUNDED;
                }
            }
        }
        return status;
    }

    private static String callbackOrderId(IncomingCallback callback) {
        Object body = callback.bodyMap();
        if (callback.bodyText() != null) {
            try {
                body = Json.parse(callback.bodyText());
            } catch (Json.ParseException notJson) {
                body = null;
            }
        }
        String fromBody = Values.string(Values.get(body, "payload", "orderId"));
        return fromBody != null ? fromBody : callback.query("orderId");
    }


    private static Currency currency(Object code) {
        Currency currency = Currency.find(Values.string(code));
        if (currency == null) {
            throw new ManatError(ManatError.Code.PROVIDER_ERROR, "payriff: unsupported currency \"" + code + "\"", NAME, null, null, null, null);
        }
        return currency;
    }
}
