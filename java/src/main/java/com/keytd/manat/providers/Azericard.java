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
import com.keytd.manat.http.HttpResponse;
import com.keytd.manat.http.Transport;
import com.keytd.manat.internal.Crypto;
import com.keytd.manat.internal.Form;
import com.keytd.manat.internal.Http;
import com.keytd.manat.internal.Values;

import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.PrivateKey;
import java.security.PublicKey;
import java.security.SecureRandom;
import java.security.interfaces.RSAKey;
import java.time.Clock;
import java.time.Duration;
import java.time.Instant;
import java.time.LocalDateTime;
import java.time.ZoneOffset;
import java.time.format.DateTimeFormatter;
import java.time.format.DateTimeParseException;
import java.time.format.ResolverStyle;
import java.util.Arrays;
import java.util.Collections;
import java.util.EnumSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.function.Supplier;
import java.util.regex.Pattern;

/**
 * Azericard e-commerce gateway (CGI form-post interface, RSA-signed).
 * Docs: https://developer.azericard.com/en
 *
 * <ul>
 * <li>The customer's browser POSTs a signed form to the gateway ({@code next().type() == FORM}).</li>
 * <li>Every message carries P_SIGN: RSA-SHA256 (PKCS#1 v1.5, hex) over a "MAC source" where each field value is
 * prefixed with its byte length and empty values become "-". Field order differs per message type and the docs note
 * it can differ per terminal, so every order can be overridden with {@code macFields}.</li>
 * <li>The result is POSTed back to BACKREF and signed with the gateway (MPI) key over AMOUNT, TERMINAL, APPROVAL, RRN,
 * INT_REF. ORDER and ACTION are NOT covered by that signature, so a payment only counts as approved when the signed
 * APPROVAL code is present, and merchants should reject a providerRef (RRN + INT_REF) they have already seen.</li>
 * <li>Completion (21) and reversals (22 online, 24 offline) are server-to-server form POSTs that answer with a bare
 * ACTION code ("0" = approved).</li>
 * <li>The gateway delivers the result to BACKREF server-to-server (checked on the test gateway: nothing is posted through
 * the browser), so BACKREF must be reachable from the internet.</li>
 * <li>Status (TRTYPE 90) answers in JSON. Its {@code signature} does not match any documented field order with the
 * published test key, so getStatus relies on TLS to the gateway instead.</li>
 * </ul>
 */
public final class Azericard implements PaymentProvider {
    private static final String NAME = "azericard";
    private static final String TEST_URL = "https://testmpi.3dsecure.az/cgi-bin/cgi_link";
    /** Not in the public docs (they only publish the test URL); used by existing integrations. */
    private static final String PRODUCTION_URL = "https://mpi.3dsecure.az/cgi-bin/cgi_link";

    /** MAC field orders from the docs (sections 2.2.1–2.2.4 and 4.3), keyed by message type. */
    public static final Map<String, List<String>> MAC_FIELDS;

    static {
        Map<String, List<String>> fields = new LinkedHashMap<>();
        fields.put("purchase", List.of("AMOUNT", "CURRENCY", "TERMINAL", "TRTYPE", "TIMESTAMP", "NONCE", "MERCH_URL"));
        fields.put("completion", List.of("AMOUNT", "CURRENCY", "TERMINAL", "TRTYPE", "ORDER", "RRN", "INT_REF"));
        fields.put("reversal", List.of("AMOUNT", "CURRENCY", "TERMINAL", "TRTYPE", "ORDER", "RRN", "INT_REF"));
        fields.put("callback", List.of("AMOUNT", "TERMINAL", "APPROVAL", "RRN", "INT_REF"));
        fields.put("status", List.of("ORDER", "TERMINAL", "TRTYPE", "TIMESTAMP", "NONCE"));
        MAC_FIELDS = Collections.unmodifiableMap(fields);
    }

    private static final Map<String, String> ACTION_MESSAGES = Map.of(
            "0", "Approved",
            "1", "Duplicate transaction",
            "2", "Transaction declined",
            "3", "Transaction processing error",
            "6", "Repeat of a declined transaction",
            "7", "Repeat of a transaction with an authentication error",
            "8", "Repeat of a transaction that terminated without a response");

    private static final long DAY_SECONDS = 86400;
    private static final Pattern ORDER = Pattern.compile("^\\d{6,32}$");
    private static final Pattern HEX = Pattern.compile("^[0-9a-fA-F]+$");
    private static final Pattern THREE_DIGITS = Pattern.compile("^\\d{3}$");
    private static final DateTimeFormatter TIMESTAMP = DateTimeFormatter.ofPattern("uuuuMMddHHmmss").withResolverStyle(ResolverStyle.STRICT);
    private static final DateTimeFormatter BAKU_TIME = DateTimeFormatter.ofPattern("uuuu.MM.dd HH:mm:ss").withResolverStyle(ResolverStyle.STRICT);
    private static final SecureRandom RANDOM = new SecureRandom();
    private static final Set<Capability> CAPABILITIES = Collections.unmodifiableSet(EnumSet.allOf(Capability.class));

    private final Http http;
    private final String url;
    private final String terminal;
    private final String merchantName;
    private final String merchantUrl;
    private final String email;
    private final String country;
    private final String merchantGmt;
    private final PrivateKey privateKey;
    private final PublicKey gatewayKey;
    private final Map<String, List<String>> macFields;
    private final Clock clock;
    private final Supplier<String> nonce;

    private Azericard(Builder builder) {
        if (isEmpty(builder.terminal)) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST, "azericard: terminal is required");
        }
        if (isEmpty(builder.merchantName) || isEmpty(builder.merchantUrl)) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST, "azericard: merchantName and merchantUrl are required");
        }
        if (isEmpty(builder.privateKey)) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST, "azericard: privateKey (PEM) is required");
        }
        try {
            this.privateKey = Crypto.rsaPrivateKey(builder.privateKey);
        } catch (GeneralSecurityException | IllegalArgumentException invalid) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST, "azericard: privateKey is not a valid PEM key", NAME, null, null, null, invalid);
        }
        if (!(privateKey instanceof RSAKey) || ((RSAKey) privateKey).getModulus().bitLength() < 2048) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST, "azericard: privateKey must be an RSA key of at least 2048 bits");
        }
        PublicKey gateway = null;
        if (!isEmpty(builder.gatewayPublicKey)) {
            try {
                gateway = Crypto.rsaPublicKey(builder.gatewayPublicKey);
            } catch (GeneralSecurityException | IllegalArgumentException invalid) {
                throw new ManatError(ManatError.Code.INVALID_REQUEST, "azericard: gatewayPublicKey is not a valid PEM key", NAME, null, null, null, invalid);
            }
        }
        this.gatewayKey = gateway;
        this.terminal = builder.terminal;
        this.merchantName = builder.merchantName;
        this.merchantUrl = builder.merchantUrl;
        this.email = builder.email;
        this.country = builder.country;
        this.merchantGmt = builder.merchantGmt;
        this.url = builder.gatewayUrl != null ? builder.gatewayUrl : builder.environment == Environment.TEST ? TEST_URL : PRODUCTION_URL;
        Map<String, List<String>> fields = new LinkedHashMap<>(MAC_FIELDS);
        fields.putAll(builder.macFields);
        this.macFields = fields;
        this.clock = builder.clock;
        this.nonce = builder.nonce;
        this.http = new Http(NAME, builder.transport, builder.timeout);
    }

    public static Builder builder() {
        return new Builder();
    }

    public static final class Builder {
        private String terminal;
        private String privateKey;
        private String gatewayPublicKey;
        private String merchantName;
        private String merchantUrl;
        private Environment environment = Environment.PRODUCTION;
        private String gatewayUrl;
        private String email;
        private String country;
        private String merchantGmt;
        private Map<String, List<String>> macFields = Collections.emptyMap();
        private Clock clock = Clock.systemUTC();
        private Supplier<String> nonce = () -> {
            byte[] bytes = new byte[16];
            RANDOM.nextBytes(bytes);
            return Crypto.hex(bytes);
        };
        private Transport transport;
        private Duration timeout;

        private Builder() {
        }

        public Builder terminal(String terminal) {
            this.terminal = terminal;
            return this;
        }

        /** The merchant's RSA private key as PEM text (PKCS#1 or PKCS#8), at least 2048 bits. */
        public Builder privateKey(String pem) {
            this.privateKey = pem;
            return this;
        }

        /** Azericard's public key as PEM text; required to verify callbacks. */
        public Builder gatewayPublicKey(String pem) {
            this.gatewayPublicKey = pem;
            return this;
        }

        public Builder merchantName(String merchantName) {
            this.merchantName = merchantName;
            return this;
        }

        public Builder merchantUrl(String merchantUrl) {
            this.merchantUrl = merchantUrl;
            return this;
        }

        /** Default {@link Environment#PRODUCTION}. */
        public Builder environment(Environment environment) {
            this.environment = environment;
            return this;
        }

        /** Overrides the environment's gateway URL. */
        public Builder gatewayUrl(String gatewayUrl) {
            this.gatewayUrl = gatewayUrl;
            return this;
        }

        public Builder email(String email) {
            this.email = email;
            return this;
        }

        public Builder country(String country) {
            this.country = country;
            return this;
        }

        public Builder merchantGmt(String merchantGmt) {
            this.merchantGmt = merchantGmt;
            return this;
        }

        /** Field orders per message type ("purchase", "completion", "reversal", "callback", "status") that differ on your terminal. */
        public Builder macFields(Map<String, List<String>> macFields) {
            this.macFields = macFields == null ? Collections.emptyMap() : macFields;
            return this;
        }

        /** For tests: the time used in TIMESTAMP and the 24-hour refund rule. */
        public Builder clock(Clock clock) {
            this.clock = clock;
            return this;
        }

        /** For tests: the NONCE generator. */
        public Builder nonce(Supplier<String> nonce) {
            this.nonce = nonce;
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

        public Azericard build() {
            return new Azericard(this);
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

    /** Length-prefixed MAC source: "11.48" → "511.48", empty or missing → "-". Lengths are in bytes. */
    public static String macSource(Map<String, String> fields, List<String> order) {
        StringBuilder out = new StringBuilder();
        for (String name : order) {
            String value = fields.get(name);
            if (value == null || value.isEmpty()) {
                out.append('-');
            } else {
                out.append(value.getBytes(StandardCharsets.UTF_8).length).append(value);
            }
        }
        return out.toString();
    }

    /** GMT timestamp YYYYMMDDHHMMSS. */
    public static String timestamp(Instant instant) {
        return TIMESTAMP.format(instant.atOffset(ZoneOffset.UTC));
    }

    /** Status of one transaction type from a TRTYPE 90 reply (field names as returned by the test gateway). */
    public static PaymentStatus mapStatusResponse(Map<String, Object> data, String tranTrtype) {
        if (approved(data)) {
            return "0".equals(tranTrtype) ? PaymentStatus.AUTHORIZED : PaymentStatus.PAID;
        }
        if ("-40".equals(data.get("responseCode"))) {
            return PaymentStatus.PENDING; // "Client side transaction form in progress"
        }
        return PaymentStatus.FAILED;
    }

    @Override
    public CreatePaymentResult createPayment(CreatePaymentRequest request) {
        String orderId = request.orderId();
        if (!ORDER.matcher(orderId).matches()) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST,
                    "azericard: orderId must be 6–32 digits (got \"" + orderId + "\"); its last 6 digits must be unique per terminal per day");
        }
        Money amount = request.amount();
        Map<String, String> fields = new LinkedHashMap<>();
        put(fields, "AMOUNT", amount.toDecimalString());
        put(fields, "CURRENCY", amount.currency().name());
        put(fields, "ORDER", orderId);
        put(fields, "DESC", Values.truncate(request.description() != null ? request.description() : "Order " + orderId, 50));
        put(fields, "MERCH_NAME", Values.truncate(merchantName, 50));
        put(fields, "MERCH_URL", merchantUrl);
        put(fields, "TERMINAL", terminal);
        put(fields, "EMAIL", email);
        put(fields, "TRTYPE", request.intent() == Intent.AUTHORIZE ? "0" : "1");
        put(fields, "COUNTRY", country);
        put(fields, "MERCH_GMT", merchantGmt);
        put(fields, "TIMESTAMP", timestamp(clock.instant()));
        put(fields, "NONCE", nonce.get());
        put(fields, "BACKREF", request.callbackUrl() != null ? request.callbackUrl() : request.returnUrl());
        put(fields, "LANG", request.language() == null ? "az" : request.language().code());
        fields.put("P_SIGN", sign(fields, "purchase"));
        return new CreatePaymentResult(orderId, PaymentStatus.PENDING, NextAction.form(url, fields), Collections.singletonMap("fields", new LinkedHashMap<>(fields)));
    }

    @Override
    public PaymentEvent verifyCallback(IncomingCallback callback) {
        if (gatewayKey == null) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST, "azericard: gatewayPublicKey is required to verify callbacks; never accept them unverified");
        }
        Map<String, String> fields = readFields(callback);
        String signature = fields.getOrDefault("P_SIGN", "");
        byte[] signatureBytes = HEX.matcher(signature).matches() ? Crypto.unhex(signature) : null;
        if (signatureBytes == null) {
            throw new ManatError(ManatError.Code.INVALID_SIGNATURE, "azericard: callback has no valid P_SIGN", NAME, null, null, fields, null);
        }
        if (!Crypto.verifySha256(gatewayKey, macSource(fields, macFields.get("callback")), signatureBytes)) {
            throw new ManatError(ManatError.Code.INVALID_SIGNATURE, "azericard: callback P_SIGN does not match", NAME, null, null, fields, null);
        }
        String callbackTerminal = fields.getOrDefault("TERMINAL", "");
        if (!callbackTerminal.equals(terminal)) {
            throw new ManatError(ManatError.Code.INVALID_SIGNATURE, "azericard: callback is for terminal " + callbackTerminal + ", not " + terminal, NAME, null, null,
                    fields, null);
        }
        String order = fields.get("ORDER");
        if (isEmpty(order)) {
            throw new ManatError(ManatError.Code.INVALID_SIGNATURE, "azericard: callback has no ORDER", NAME, null, null, fields, null);
        }

        Money amount = parseAmount(fields.get("AMOUNT"), fields.get("CURRENCY"));
        // ACTION is unsigned; the signed APPROVAL code is what proves an approval.
        boolean approved = "0".equals(fields.get("ACTION")) && !isEmpty(fields.get("APPROVAL"));
        String providerRef = null;
        if (approved && !isEmpty(fields.get("RRN")) && !isEmpty(fields.get("INT_REF")) && amount != null && !isEmpty(fields.get("TIMESTAMP"))) {
            providerRef = encodeRef(fields.get("RRN"), fields.get("INT_REF"), amount, fields.get("TIMESTAMP"), fields.getOrDefault("TRTYPE", "1"));
        }
        PaymentStatus status = approved ? statusForTrtype(fields.get("TRTYPE")) : PaymentStatus.FAILED;
        return new PaymentEvent(order, order, status, amount, providerRef, fields);
    }

    /**
     * TRTYPE 90. Works for 24 hours after the payment. The original TRTYPE is not known from the paymentId alone, so a
     * purchase (1) is asked for first and a pre-auth (0) on "context mismatch". The answer only describes the transaction
     * type asked about, so completion (21) and the reversals (22 online, 24 offline) are queried too. Only the latest
     * offline reversal is visible this way, so refundedAmount can be lower than the truth after several 24s.
     */
    @Override
    public PaymentState getStatus(String paymentId) {
        if (!ORDER.matcher(paymentId).matches()) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST, "azericard: paymentId must be the numeric ORDER, got \"" + paymentId + "\"");
        }
        String trtype = "1";
        Map<String, Object> data = queryStatus(paymentId, trtype);
        if ("-24".equals(data.get("responseCode"))) {
            trtype = "0";
            data = queryStatus(paymentId, trtype);
        }
        PaymentStatus status = mapStatusResponse(data, trtype);
        Money amount = parseAmount(data.get("amount"), data.get("currency"));
        Map<String, Object> raw = new LinkedHashMap<>();
        raw.put(trtype, data);
        if ((status != PaymentStatus.PAID && status != PaymentStatus.AUTHORIZED) || amount == null) {
            return new PaymentState(paymentId, status, amount, null, null, raw);
        }

        Map<String, Map<String, Object>> approvedRelated = new LinkedHashMap<>();
        for (String type : "0".equals(trtype) ? Arrays.asList("21", "22", "24") : Arrays.asList("22", "24")) {
            Map<String, Object> related = queryStatus(paymentId, type);
            raw.put(type, related);
            if (approved(related)) {
                approvedRelated.put(type, related);
            }
        }
        if (status == PaymentStatus.AUTHORIZED && approvedRelated.containsKey("21")) {
            status = PaymentStatus.PAID;
        }
        long refunded = approvedAmount(approvedRelated.get("22"), amount.currency()) + approvedAmount(approvedRelated.get("24"), amount.currency());
        Money refundedAmount = null;
        if (refunded > 0) {
            refundedAmount = Money.of(refunded, amount.currency());
            if (status == PaymentStatus.AUTHORIZED) {
                status = PaymentStatus.CANCELLED;
            } else {
                status = refunded >= amount.amount() ? PaymentStatus.REFUNDED : PaymentStatus.PARTIALLY_REFUNDED;
            }
        }
        String providerRef = null;
        String paidAt = data.get("tranDate") != null ? bakuTimeToGmt(Values.text(data.get("tranDate"))) : Values.text(data.get("timestamp"));
        String rrn = Values.text(data.get("rrn"));
        String intRef = Values.text(data.get("intRef"));
        if (!isEmpty(rrn) && !isEmpty(intRef) && !isEmpty(paidAt)) {
            providerRef = encodeRef(rrn, intRef, amount, paidAt, approvedRelated.containsKey("21") ? "21" : trtype);
        }
        return new PaymentState(paymentId, status, amount, refundedAmount, providerRef, raw);
    }

    @Override
    public PaymentState capture(PaymentAction action) {
        Ref ref = requireRef(action.providerRef());
        Money amount = action.amount() != null ? action.amount() : ref.amount;
        send("completion", "21", action.paymentId(), ref, amount);
        return new PaymentState(action.paymentId(), PaymentStatus.PAID, amount, null, encodeRef(ref.rrn, ref.intRef, amount, ref.timestamp, "21"), null);
    }

    @Override
    public PaymentState cancel(PaymentAction action) {
        Ref ref = requireRef(action.providerRef());
        send("reversal", "22", action.paymentId(), ref, ref.amount);
        return new PaymentState(action.paymentId(), PaymentStatus.CANCELLED, ref.amount, null, null, null);
    }

    @Override
    public RefundResult refund(RefundRequest request) {
        Ref ref = requireRef(request.providerRef());
        Money amount = request.amount() != null ? request.amount() : ref.amount;
        if (amount.currency() != ref.amount.currency() || amount.amount() > ref.amount.amount()) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST, "azericard: refund amount exceeds the paid amount or uses another currency");
        }
        // Online reversal (22) is only possible before settlement and only once per transaction
        // (a second 22 fails with ACTION 3 on the gateway); everything else is an offline reversal (24).
        Instant paidAt = parseTimestamp(ref.timestamp);
        String trtype = "24";
        if (paidAt != null && clock.instant().getEpochSecond() - paidAt.getEpochSecond() < DAY_SECONDS) {
            trtype = approved(queryStatus(request.paymentId(), "22")) ? "24" : "22";
        }
        String action = send("reversal", trtype, request.paymentId(), ref, amount);
        PaymentStatus status = amount.amount() < ref.amount.amount() ? PaymentStatus.PARTIALLY_REFUNDED : PaymentStatus.REFUNDED;
        Map<String, Object> raw = new LinkedHashMap<>();
        raw.put("trtype", trtype);
        raw.put("action", action);
        return new RefundResult(request.paymentId(), null, status, raw);
    }

    private Map<String, Object> queryStatus(String order, String tranTrtype) {
        Map<String, String> fields = new LinkedHashMap<>();
        fields.put("TRAN_TRTYPE", tranTrtype);
        fields.put("ORDER", order);
        fields.put("TERMINAL", terminal);
        fields.put("TRTYPE", "90");
        fields.put("TIMESTAMP", timestamp(clock.instant()));
        fields.put("NONCE", nonce.get());
        fields.put("P_SIGN", sign(fields, "status"));
        Http.JsonResponse response = http.json("POST", url, Collections.emptyMap(), null, fields);
        Map<String, Object> data = Values.map(response.data);
        if (response.status >= 400 || data == null) {
            throw new ManatError(ManatError.Code.PROVIDER_ERROR, "azericard: status request failed (HTTP " + response.status + ")", NAME, null, null, response.data, null);
        }
        return data;
    }

    private String send(String message, String trtype, String order, Ref ref, Money amount) {
        Map<String, String> fields = new LinkedHashMap<>();
        fields.put("AMOUNT", amount.toDecimalString());
        fields.put("CURRENCY", amount.currency().name());
        fields.put("ORDER", order);
        fields.put("RRN", ref.rrn);
        fields.put("INT_REF", ref.intRef);
        fields.put("TERMINAL", terminal);
        fields.put("TRTYPE", trtype);
        fields.put("TIMESTAMP", timestamp(clock.instant()));
        fields.put("NONCE", nonce.get());
        fields.put("P_SIGN", sign(fields, message));
        HttpResponse response = http.send("POST", url, Collections.emptyMap(), null, fields);
        String action = response.body().trim();
        if (response.status() >= 400) {
            throw new ManatError(ManatError.Code.PROVIDER_ERROR, "azericard: HTTP " + response.status(), NAME, null, null, response.body(), null);
        }
        if (!action.equals("0")) {
            String text = ACTION_MESSAGES.getOrDefault(action, "Unexpected response \"" + cut(action, 100) + "\"");
            throw new ManatError(ManatError.Code.PROVIDER_ERROR, "azericard: " + text, NAME, cut(action, 20), text, response.body(), null);
        }
        return action;
    }

    private String sign(Map<String, String> fields, String message) {
        try {
            return Crypto.hex(Crypto.signSha256(privateKey, macSource(fields, macFields.get(message))));
        } catch (GeneralSecurityException failed) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST, "azericard: could not sign with privateKey", NAME, null, null, null, failed);
        }
    }

    private static boolean approved(Map<String, Object> data) {
        return "0".equals(data.get("actionCode")) && "00".equals(data.get("responseCode"));
    }

    private static long approvedAmount(Map<String, Object> approved, Currency currency) {
        if (approved == null) {
            return 0;
        }
        Object code = approved.get("currency") != null ? approved.get("currency") : currency.name();
        Money money = parseAmount(approved.get("amount"), code);
        return money != null && money.currency() == currency ? money.amount() : 0;
    }

    /** "2026.10.26 19:06:40" in Baku time (UTC+4, no DST) → "20261026150640" GMT. */
    private static String bakuTimeToGmt(String value) {
        try {
            return TIMESTAMP.format(LocalDateTime.parse(value == null ? "" : value, BAKU_TIME).minusHours(4));
        } catch (DateTimeParseException invalid) {
            return null;
        }
    }

    private static PaymentStatus statusForTrtype(String trtype) {
        if ("0".equals(trtype)) {
            return PaymentStatus.AUTHORIZED;
        }
        if ("22".equals(trtype) || "24".equals(trtype)) {
            return PaymentStatus.REFUNDED;
        }
        return PaymentStatus.PAID;
    }

    private static String encodeRef(String rrn, String intRef, Money amount, String timestamp, String trtype) {
        return String.join("|", "az1", rrn, intRef, amount.toDecimalString(), amount.currency().name(), timestamp, trtype);
    }

    /** What providerRef carries: the payment's RRN and INT_REF, its amount, its GMT timestamp and TRTYPE. */
    private static final class Ref {
        final String rrn;
        final String intRef;
        final Money amount;
        final String timestamp;

        Ref(String rrn, String intRef, Money amount, String timestamp) {
            this.rrn = rrn;
            this.intRef = intRef;
            this.amount = amount;
            this.timestamp = timestamp;
        }
    }

    private static Ref requireRef(String value) {
        String[] parts = value == null ? new String[0] : value.split("\\|", -1);
        if (parts.length != 7 || !parts[0].equals("az1") || parts[1].isEmpty() || parts[2].isEmpty()) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST,
                    "azericard: providerRef from the payment callback is required (store event.providerRef with the order)");
        }
        return new Ref(parts[1], parts[2], Money.fromDecimal(parts[3], currency(parts[4])), parts[5]);
    }

    private static Money parseAmount(Object amountValue, Object currencyValue) {
        String amount = Values.text(amountValue);
        String code = Values.text(currencyValue);
        if (isEmpty(amount) || isEmpty(code)) {
            return null;
        }
        String alpha = THREE_DIGITS.matcher(code).matches() ? (Currency.fromNumeric(code) == null ? null : Currency.fromNumeric(code).name()) : code;
        return alpha == null ? null : Money.fromDecimal(amount, currency(alpha));
    }

    private static Currency currency(String code) {
        Currency currency = Currency.find(code);
        if (currency == null) {
            throw new ManatError(ManatError.Code.PROVIDER_ERROR, "azericard: unsupported currency \"" + code + "\"", NAME, null, null, null, null);
        }
        return currency;
    }

    private static Instant parseTimestamp(String value) {
        try {
            return LocalDateTime.parse(value, TIMESTAMP).toInstant(ZoneOffset.UTC);
        } catch (DateTimeParseException invalid) {
            return null;
        }
    }

    /** Upper-case keys, trimmed values; query first, body after. */
    private static Map<String, String> readFields(IncomingCallback callback) {
        Map<String, String> out = new LinkedHashMap<>();
        for (Map.Entry<String, List<String>> entry : callback.query().entrySet()) {
            if (!entry.getValue().isEmpty()) {
                out.put(entry.getKey().toUpperCase(Locale.ROOT), entry.getValue().get(0).trim());
            }
        }
        Map<String, ?> body = callback.bodyText() != null ? Form.decode(callback.bodyText()) : callback.bodyMap();
        if (body != null) {
            for (Map.Entry<String, ?> entry : body.entrySet()) {
                Object value = entry.getValue();
                if (value instanceof List && !((List<?>) value).isEmpty()) {
                    value = ((List<?>) value).get(0);
                } else if (value instanceof String[] && ((String[]) value).length > 0) {
                    value = ((String[]) value)[0];
                }
                if (value instanceof String) {
                    out.put(entry.getKey().toUpperCase(Locale.ROOT), ((String) value).trim());
                }
            }
        }
        return out;
    }

    private static void put(Map<String, String> fields, String name, String value) {
        if (value != null && !value.isEmpty()) {
            fields.put(name, value);
        }
    }

    private static String cut(String value, int max) {
        return value.length() > max ? value.substring(0, max) : value;
    }

    private static boolean isEmpty(String value) {
        return value == null || value.isEmpty();
    }
}
