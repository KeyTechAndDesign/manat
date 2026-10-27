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
import com.keytd.manat.PaymentAction;
import com.keytd.manat.PaymentEvent;
import com.keytd.manat.PaymentProvider;
import com.keytd.manat.PaymentState;
import com.keytd.manat.PaymentStatus;
import com.keytd.manat.RefundRequest;
import com.keytd.manat.RefundResult;
import com.keytd.manat.http.Transport;
import com.keytd.manat.internal.Form;
import com.keytd.manat.internal.Http;
import com.keytd.manat.internal.Values;

import java.time.Duration;
import java.util.Collections;
import java.util.EnumSet;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.Set;
import java.util.regex.Pattern;

/**
 * Yığım (MAGNET).
 * The webhook carries only `reference` and is unsigned, so verifyCallback re-reads the payment.
 * 09 (issuer timeout) and S7 are not final: the money may have moved, so they stay pending until cancelled.
 */
public final class Yigim implements PaymentProvider {
    private static final String NAME = "yigim";
    private static final String TEST_URL = "https://sandbox.api.pay.yigim.az";
    private static final String PRODUCTION_URL = "https://api.pay.yigim.az";
    private static final long OK = 0;
    private static final String DEFAULT_TEMPLATE = "TPL0001";
    private static final Pattern REFERENCE = Pattern.compile("^[^\\s\\x00-\\x1f]{1,255}$");
    private static final Set<Capability> CAPABILITIES = Collections.unmodifiableSet(EnumSet.allOf(Capability.class));

    private final Http http;
    private final String baseUrl;
    private final String merchant;
    private final String apiKey;
    private final String biller;
    private final String template;

    private Yigim(Builder builder) {
        if (isEmpty(builder.merchant)) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST, "yigim: merchant is required");
        }
        if (isEmpty(builder.apiKey)) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST, "yigim: apiKey is required");
        }
        if (isEmpty(builder.biller)) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST, "yigim: biller is required");
        }
        this.merchant = builder.merchant;
        this.apiKey = builder.apiKey;
        this.biller = builder.biller;
        this.template = isEmpty(builder.template) ? DEFAULT_TEMPLATE : builder.template;
        String base = builder.baseUrl != null ? builder.baseUrl : builder.environment == Environment.TEST ? TEST_URL : PRODUCTION_URL;
        this.baseUrl = base.replaceAll("/+$", "");
        this.http = new Http(NAME, builder.transport, builder.timeout);
    }

    public static Builder builder() {
        return new Builder();
    }

    public static final class Builder {
        private String merchant;
        private String apiKey;
        private String biller;
        private String template;
        private Environment environment = Environment.PRODUCTION;
        private String baseUrl;
        private Transport transport;
        private Duration timeout;

        private Builder() {
        }

        /** Merchant name issued by Yığım ({@code X-Merchant}). */
        public Builder merchant(String merchant) {
            this.merchant = merchant;
            return this;
        }

        /** API key issued by Yığım ({@code X-API-Key}). */
        public Builder apiKey(String apiKey) {
            this.apiKey = apiKey;
            return this;
        }

        /** Biller name provided by the acquirer. */
        public Builder biller(String biller) {
            this.biller = biller;
            return this;
        }

        /** Payment page template; default {@code TPL0001}. */
        public Builder template(String template) {
            this.template = template;
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

        public Yigim build() {
            return new Yigim(this);
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

    /** Maps a payment status code. */
    public static PaymentStatus mapStatus(String status) {
        switch (status == null ? "" : status) {
            case "S0": // created, waiting for card data
            case "S2": // in progress
            case "S7": // system malfunction
            case "09": // issuer timeout: cancel it, then read the status again
                return PaymentStatus.PENDING;
            case "S1": // pre-authorised DMS payment: charge or cancel within 30 days
                return PaymentStatus.AUTHORIZED;
            case "00":
                return PaymentStatus.PAID;
            case "S4": // reversed
                return PaymentStatus.CANCELLED;
            case "S3": // unknown error
            case "01":
            case "02":
            case "03":
            case "04":
            case "05":
            case "06":
            case "07":
            case "08":
                return PaymentStatus.FAILED;
            default:
                throw new ManatError(ManatError.Code.PROVIDER_ERROR, "yigim: unknown payment status \"" + status + "\"", NAME, null, null, null, null);
        }
    }

    @Override
    public CreatePaymentResult createPayment(CreatePaymentRequest request) {
        requireReference(request.orderId());
        // The return URL travels in `extra` (name=value;name=value), which the gateway also cuts at `&`.
        if (request.returnUrl().contains("&") || request.returnUrl().contains(";")) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST, "yigim: returnUrl may carry at most one query parameter and no \";\"");
        }
        Money amount = request.amount();
        Map<String, String> params = new LinkedHashMap<>();
        params.put("reference", request.orderId());
        params.put("type", request.intent() == Intent.AUTHORIZE ? "DMS" : "SMS");
        params.put("amount", Long.toString(amount.amount()));
        params.put("currency", amount.currency().numericCode());
        params.put("biller", biller);
        if (!isEmpty(request.description())) {
            params.put("description", request.description());
        }
        params.put("template", template);
        params.put("language", request.language() == null ? "az" : request.language().code());
        // The gateway appends `&reference=…` to the callback URL as is, so it needs a query string.
        if (!isEmpty(request.callbackUrl())) {
            params.put("callback", request.callbackUrl().contains("?") ? request.callbackUrl() : request.callbackUrl() + "?");
        }
        params.put("extra", "back-url=" + request.returnUrl() + ";fail-url=" + request.returnUrl());
        Map<String, Object> reply = call("payment/create", params);
        String url = Values.string(reply.get("url"));
        if (isEmpty(url)) {
            throw error("payment page URL is missing", reply);
        }
        return new CreatePaymentResult(request.orderId(), PaymentStatus.PENDING, NextAction.redirect(url), reply);
    }

    @Override
    public PaymentEvent verifyCallback(IncomingCallback callback) {
        String reference = callback.query("reference");
        if (reference == null) {
            reference = callback.bodyField("reference");
        }
        if (reference == null || !REFERENCE.matcher(reference).matches()) {
            throw new ManatError(ManatError.Code.INVALID_SIGNATURE, "yigim: callback has no reference", NAME, null, null, callback.toMap(), null);
        }
        // The webhook is unsigned; trust only what payment/status says about this reference.
        PaymentState state = getStatus(reference);
        return new PaymentEvent(state.paymentId(), state.paymentId(), state.status(), state.amount(), null, state.raw());
    }

    @Override
    public PaymentState getStatus(String paymentId) {
        requireReference(paymentId);
        Map<String, Object> reply = call("payment/status", Collections.singletonMap("reference", paymentId));
        Object reference = reply.get("reference");
        if (Values.truthy(reference) && !paymentId.equals(reference)) {
            throw error("status is for payment " + Values.text(reference) + ", not " + paymentId, reply);
        }
        PaymentStatus status = mapStatus(Values.text(reply.get("status")));
        Currency currency = Currency.fromNumeric(Values.string(reply.get("currency")));
        Money amount = null;
        if (reply.get("amount") != null && !"".equals(reply.get("amount")) && currency != null) {
            amount = Values.money(reply.get("amount"), currency);
        }
        return new PaymentState(paymentId, status, amount, null, null, reply);
    }

    @Override
    public RefundResult refund(RefundRequest request) {
        Map<String, Object> reply = call("payment/refund", withAmount(request.paymentId(), request.amount()));
        PaymentState state = getStatus(request.paymentId());
        // While payment/status still reads 00 the requested amount decides.
        PaymentStatus status = state.status();
        if (status == PaymentStatus.PAID) {
            boolean partial = request.amount() != null && state.amount() != null && request.amount().amount() < state.amount().amount();
            status = partial ? PaymentStatus.PARTIALLY_REFUNDED : PaymentStatus.REFUNDED;
        }
        Map<String, Object> raw = new LinkedHashMap<>();
        raw.put("refund", reply);
        raw.put("payment", state.raw());
        return new RefundResult(request.paymentId(), null, status, raw);
    }

    @Override
    public PaymentState capture(PaymentAction action) {
        call("payment/charge", withAmount(action.paymentId(), action.amount()));
        return getStatus(action.paymentId());
    }

    @Override
    public PaymentState cancel(PaymentAction action) {
        call("payment/cancel", Collections.singletonMap("reference", action.paymentId()));
        return getStatus(action.paymentId());
    }

    private Map<String, Object> call(String command, Map<String, String> params) {
        requireReference(params.getOrDefault("reference", ""));
        Map<String, String> headers = new LinkedHashMap<>();
        headers.put("x-merchant", merchant);
        headers.put("x-api-key", apiKey);
        headers.put("x-type", "JSON");
        Http.JsonResponse response = http.json("GET", baseUrl + "/" + command + "?" + Form.encode(params), headers, null, null);
        // Replies may be nested under `response`.
        Map<String, Object> data = Values.map(response.data);
        Map<String, Object> reply = data != null && Values.map(data.get("response")) != null ? Values.map(data.get("response")) : data;
        Long code = reply == null ? null : code(reply);
        if (code == null) {
            throw new ManatError(ManatError.Code.PROVIDER_ERROR, "yigim: unexpected response (HTTP " + response.status + ")", NAME, null, null, response.data, null);
        }
        if (response.status >= 400 || code != OK) {
            throw error("HTTP " + response.status, reply);
        }
        return reply;
    }

    private static ManatError error(String what, Map<String, Object> reply) {
        Long code = code(reply);
        String message = Values.truthy(reply.get("message")) ? Values.text(reply.get("message")) : null;
        boolean fromProvider = code != null && code != OK;
        return new ManatError(ManatError.Code.PROVIDER_ERROR, "yigim: " + (fromProvider && message != null ? message : what), NAME,
                fromProvider ? code.toString() : null, fromProvider ? message : null, reply, null);
    }

    /** {@code code} comes as 0 or "0". */
    private static Long code(Map<String, Object> reply) {
        Object code = reply.get("code");
        if (code instanceof Long) {
            return (Long) code;
        }
        return code instanceof String && ((String) code).matches("-?\\d+") ? Long.valueOf((String) code) : null;
    }

    private static Map<String, String> withAmount(String reference, Money amount) {
        Map<String, String> params = new LinkedHashMap<>();
        params.put("reference", reference);
        if (amount != null) {
            params.put("amount", Long.toString(amount.amount()));
        }
        return params;
    }

    private static void requireReference(String reference) {
        if (reference == null || !REFERENCE.matcher(reference).matches()) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST, "yigim: invalid reference \"" + reference + "\"");
        }
    }

    private static boolean isEmpty(String value) {
        return value == null || value.isEmpty();
    }
}
