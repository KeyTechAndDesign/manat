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

import java.math.BigDecimal;
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
 * Kapital Bank (TXPG REST API).
 * The redirect back (?ID=…&amp;STATUS=…) is unsigned, so verifyCallback re-reads the order.
 */
public final class KapitalBank implements PaymentProvider {
    private static final String NAME = "kapitalbank";
    private static final String TEST_URL = "https://txpgtst.kapitalbank.az/api";
    private static final String PRODUCTION_URL = "https://e-commerce.kapitalbank.az/api";
    private static final Pattern ORDER_ID = Pattern.compile("^\\d+$");
    private static final Pattern BARE_ORIGIN = Pattern.compile("^[a-zA-Z][a-zA-Z0-9+.-]*://[^/]+$");
    private static final Set<Capability> CAPABILITIES = Collections.unmodifiableSet(EnumSet.allOf(Capability.class));

    private final Http http;
    private final String baseUrl;
    private final String authHeader;

    private KapitalBank(Builder builder) {
        if (isEmpty(builder.username) || isEmpty(builder.password)) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST, "kapitalbank: username and password are required");
        }
        String base = builder.baseUrl != null ? builder.baseUrl : builder.environment == Environment.TEST ? TEST_URL : PRODUCTION_URL;
        this.baseUrl = base.replaceAll("/+$", "");
        this.authHeader = "Basic " + Base64.getEncoder().encodeToString((builder.username + ":" + builder.password).getBytes(StandardCharsets.UTF_8));
        this.http = new Http(NAME, builder.transport, builder.timeout);
    }

    public static Builder builder() {
        return new Builder();
    }

    public static final class Builder {
        private String username;
        private String password;
        private Environment environment = Environment.PRODUCTION;
        private String baseUrl;
        private Transport transport;
        private Duration timeout;

        private Builder() {
        }

        /** The merchant's API user, such as {@code TerminalSys/xxxx}. */
        public Builder username(String username) {
            this.username = username;
            return this;
        }

        public Builder password(String password) {
            this.password = password;
            return this;
        }

        /** Default {@link Environment#PRODUCTION}. */
        public Builder environment(Environment environment) {
            this.environment = environment;
            return this;
        }

        /** Overrides the environment's API URL. */
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

        public KapitalBank build() {
            return new KapitalBank(this);
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

    /** Maps an order's status. */
    public static PaymentStatus mapStatus(Map<String, Object> order) {
        Object status = order.get("status");
        switch (status instanceof String ? (String) status : "") {
            case "Preparing":
                return PaymentStatus.PENDING;
            case "Authorized":
                return PaymentStatus.AUTHORIZED;
            case "PartPaid":
            case "FullyPaid":
            case "Funded":
            case "Closed": {
                BigDecimal refunded = order.get("clearedRefundAmount") == null ? BigDecimal.ZERO : Values.decimal(order.get("clearedRefundAmount"));
                if (refunded != null && refunded.signum() > 0) {
                    BigDecimal charged = order.get("clearedChargeAmount") == null ? null : Values.decimal(order.get("clearedChargeAmount"));
                    boolean full = order.get("clearedChargeAmount") == null || (charged != null && refunded.compareTo(charged) >= 0);
                    return full ? PaymentStatus.REFUNDED : PaymentStatus.PARTIALLY_REFUNDED;
                }
                return PaymentStatus.PAID;
            }
            case "Refunded":
                return PaymentStatus.REFUNDED;
            case "Cancelled":
            case "Voided":
            case "Expired":
                return PaymentStatus.CANCELLED;
            case "Rejected":
            case "Refused":
            case "Declined":
                return PaymentStatus.FAILED;
            default:
                throw new ManatError(ManatError.Code.PROVIDER_ERROR, "kapitalbank: unknown order status \"" + status + "\"", NAME, null, null, order, null);
        }
    }

    @Override
    public CreatePaymentResult createPayment(CreatePaymentRequest request) {
        Money amount = request.amount();
        Map<String, Object> order = new LinkedHashMap<>();
        order.put("typeRid", request.intent() == Intent.AUTHORIZE ? "Order_DMS" : "Order_SMS");
        order.put("amount", amount.toDecimalString());
        order.put("currency", amount.currency().name());
        order.put("language", request.language() == null ? "az" : request.language().code());
        order.put("description", request.description() != null ? request.description() : request.orderId());
        order.put("hppRedirectUrl", request.returnUrl());
        Map<String, Object> data = call("POST", "/order", Collections.singletonMap("order", order));
        Map<String, Object> created = Values.map(data.get("order"));
        if (created == null || !Values.truthy(created.get("id")) || !Values.truthy(created.get("hppUrl")) || !Values.truthy(created.get("password"))) {
            throw new ManatError(ManatError.Code.PROVIDER_ERROR, "kapitalbank: order response is missing id, hppUrl or password", NAME, null, null, data, null);
        }
        String id = Values.text(created.get("id"));
        Map<String, String> params = new LinkedHashMap<>();
        params.put("id", id);
        params.put("password", Values.text(created.get("password")));
        String url = withQuery(Values.text(created.get("hppUrl")), params);
        PaymentStatus status = created.get("status") != null ? mapStatus(created) : PaymentStatus.PENDING;
        return new CreatePaymentResult(id, status, NextAction.redirect(url), data);
    }

    @Override
    public PaymentEvent verifyCallback(IncomingCallback callback) {
        String id = callback.query("ID");
        if (id == null) {
            id = callback.query("id");
        }
        if (id == null) {
            id = callback.bodyField("ID");
        }
        if (id == null || !ORDER_ID.matcher(id).matches()) {
            throw new ManatError(ManatError.Code.INVALID_SIGNATURE, "kapitalbank: callback has no numeric ID", NAME, null, null, callback.toMap(), null);
        }
        // The redirect is unsigned; the API is the source of truth.
        return PaymentEvent.fromState(getStatus(id));
    }

    @Override
    public PaymentState getStatus(String paymentId) {
        return toState(fetchOrder(paymentId));
    }

    @Override
    public RefundResult refund(RefundRequest request) {
        String paymentId = request.paymentId();
        Money amount = request.amount() != null ? request.amount() : remainingRefundable(paymentId);
        Map<String, Object> tran = new LinkedHashMap<>();
        tran.put("phase", "Single");
        tran.put("amount", amount.toDecimalString());
        tran.put("type", "Refund");
        Map<String, Object> data = exec(paymentId, tran);
        PaymentState state = getStatus(paymentId);
        Map<String, Object> raw = new LinkedHashMap<>();
        raw.put("exec", data);
        raw.put("order", state.raw());
        Object actionId = Values.get(data, "tran", "match", "tranActionId");
        return new RefundResult(paymentId, Values.truthy(actionId) ? Values.text(actionId) : null, state.status(), raw);
    }

    @Override
    public PaymentState capture(PaymentAction action) {
        String paymentId = action.paymentId();
        Money amount = action.amount() != null ? action.amount() : getStatus(paymentId).amount();
        if (amount == null) {
            throw new ManatError(ManatError.Code.PROVIDER_ERROR, "kapitalbank: order " + paymentId + " has no amount to capture", NAME, null, null, null, null);
        }
        Map<String, Object> tran = new LinkedHashMap<>();
        tran.put("phase", "Clearing");
        tran.put("amount", amount.toDecimalString());
        exec(paymentId, tran);
        return getStatus(paymentId);
    }

    @Override
    public PaymentState cancel(PaymentAction action) {
        String paymentId = action.paymentId();
        PaymentState current = getStatus(paymentId);
        // Reversal of a pre-auth hold uses phase Auth; a same-day purchase uses phase Single.
        Map<String, Object> tran = new LinkedHashMap<>();
        tran.put("phase", current.status() == PaymentStatus.AUTHORIZED ? "Auth" : "Single");
        tran.put("voidKind", "Full");
        exec(paymentId, tran);
        return getStatus(paymentId);
    }

    private Money remainingRefundable(String paymentId) {
        Map<String, Object> order = fetchOrder(paymentId);
        Currency currency = currency(order.get("currency"), paymentId);
        Object charged = order.get("clearedChargeAmount") != null ? order.get("clearedChargeAmount") : order.get("amount");
        long remaining = Values.money(charged == null ? "0" : charged, currency).amount();
        if (Values.truthy(order.get("clearedRefundAmount"))) {
            remaining -= Values.money(order.get("clearedRefundAmount"), currency).amount();
        }
        if (remaining <= 0) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST, "kapitalbank: order " + paymentId + " has nothing left to refund", NAME, null, null, order, null);
        }
        return Money.of(remaining, currency);
    }

    private Map<String, Object> fetchOrder(String paymentId) {
        requireOrderId(paymentId);
        Map<String, Object> data = call("GET", "/order/" + paymentId + "?tranDetailLevel=2&tokenDetailLevel=2&orderDetailLevel=2", null);
        Map<String, Object> order = Values.map(data.get("order"));
        if (order == null) {
            throw new ManatError(ManatError.Code.PROVIDER_ERROR, "kapitalbank: no order in response for " + paymentId, NAME, null, null, data, null);
        }
        order.putIfAbsent("id", paymentId);
        return order;
    }

    private PaymentState toState(Map<String, Object> order) {
        String id = Values.text(order.get("id"));
        Money amount = null;
        Money refunded = null;
        if (order.get("amount") != null && order.get("currency") != null) {
            amount = Values.money(order.get("amount"), currency(order.get("currency"), id));
        }
        if (Values.truthy(order.get("clearedRefundAmount")) && order.get("currency") != null) {
            refunded = Values.money(order.get("clearedRefundAmount"), currency(order.get("currency"), id));
        }
        return new PaymentState(id, mapStatus(order), amount, refunded, null, order);
    }

    private Map<String, Object> exec(String paymentId, Map<String, Object> tran) {
        requireOrderId(paymentId);
        return call("POST", "/order/" + paymentId + "/exec-tran", Collections.singletonMap("tran", tran));
    }

    private Map<String, Object> call(String method, String path, Object body) {
        Http.JsonResponse response = http.json(method, baseUrl + path, Collections.singletonMap("authorization", authHeader), body, null);
        Map<String, Object> data = Values.map(response.data);
        if (response.status >= 400 || (data != null && Values.truthy(data.get("errorCode")))) {
            String code = data == null ? null : Values.text(data.get("errorCode"));
            String message = data == null ? null : Values.text(data.get("errorDescription"));
            throw new ManatError(ManatError.Code.PROVIDER_ERROR, "kapitalbank: " + (message != null ? message : "HTTP " + response.status), NAME, code, message,
                    response.data, null);
        }
        return data != null ? data : new LinkedHashMap<>();
    }


    private static void requireOrderId(String paymentId) {
        if (!ORDER_ID.matcher(paymentId).matches()) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST, "kapitalbank: paymentId must be the numeric order id, got \"" + paymentId + "\"");
        }
    }

    private static Currency currency(Object code, String paymentId) {
        Currency currency = Currency.find(Values.string(code));
        if (currency == null) {
            throw new ManatError(ManatError.Code.PROVIDER_ERROR, "kapitalbank: unexpected currency \"" + code + "\" on order " + paymentId, NAME, null, null, null, null);
        }
        return currency;
    }

    /** Sets query parameters on a URL, replacing existing ones with the same name (like URL.searchParams.set). */
    private static String withQuery(String url, Map<String, String> params) {
        String fragment = "";
        int hash = url.indexOf('#');
        if (hash >= 0) {
            fragment = url.substring(hash);
            url = url.substring(0, hash);
        }
        int question = url.indexOf('?');
        String base = question >= 0 ? url.substring(0, question) : url;
        String query = question >= 0 ? url.substring(question + 1) : "";
        if (BARE_ORIGIN.matcher(base).matches()) {
            base += "/";
        }
        Map<String, String> merged = new LinkedHashMap<>(Form.decode(query));
        merged.putAll(params);
        return base + "?" + Form.encode(merged) + fragment;
    }

    private static boolean isEmpty(String value) {
        return value == null || value.isEmpty();
    }
}
