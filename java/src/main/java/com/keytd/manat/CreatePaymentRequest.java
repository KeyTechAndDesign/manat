package com.keytd.manat;

import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.Map;

/**
 * Input for {@link Manat#createPayment}. Immutable: every {@code with…} method returns a copy.
 *
 * <pre>{@code
 * CreatePaymentRequest.of("A-1001", Money.azn("25.50"), "https://shop.az/payment/return")
 *     .withDescription("Order A-1001")
 *     .withLanguage(Language.EN);
 * }</pre>
 */
public final class CreatePaymentRequest {
    private final String orderId;
    private final Money amount;
    private final String returnUrl;
    private final String callbackUrl;
    private final String description;
    private final Language language;
    private final Intent intent;
    private final String customerEmail;
    private final String customerPhone;
    private final Map<String, String> metadata;

    private CreatePaymentRequest(String orderId, Money amount, String returnUrl, String callbackUrl, String description, Language language,
                                 Intent intent, String customerEmail, String customerPhone, Map<String, String> metadata) {
        this.orderId = orderId;
        this.amount = amount;
        this.returnUrl = returnUrl;
        this.callbackUrl = callbackUrl;
        this.description = description;
        this.language = language;
        this.intent = intent;
        this.customerEmail = customerEmail;
        this.customerPhone = customerPhone;
        this.metadata = metadata;
    }

    /**
     * @param orderId   the merchant's own id, unique per payment attempt
     * @param amount    what to charge
     * @param returnUrl where the customer lands after paying
     */
    public static CreatePaymentRequest of(String orderId, Money amount, String returnUrl) {
        return new CreatePaymentRequest(orderId, amount, returnUrl, null, null, null, Intent.PURCHASE, null, null, Collections.emptyMap());
    }

    /** Server-to-server notification URL, for providers that take one per payment. */
    public CreatePaymentRequest withCallbackUrl(String callbackUrl) {
        return new CreatePaymentRequest(orderId, amount, returnUrl, callbackUrl, description, language, intent, customerEmail, customerPhone, metadata);
    }

    /** Shown to the customer where the provider supports it. */
    public CreatePaymentRequest withDescription(String description) {
        return new CreatePaymentRequest(orderId, amount, returnUrl, callbackUrl, description, language, intent, customerEmail, customerPhone, metadata);
    }

    /** Payment page language; adapters default to Azerbaijani. */
    public CreatePaymentRequest withLanguage(Language language) {
        return new CreatePaymentRequest(orderId, amount, returnUrl, callbackUrl, description, language, intent, customerEmail, customerPhone, metadata);
    }

    /** {@link Intent#AUTHORIZE} holds the amount for a later capture; the default is {@link Intent#PURCHASE}. */
    public CreatePaymentRequest withIntent(Intent intent) {
        return new CreatePaymentRequest(orderId, amount, returnUrl, callbackUrl, description, language, intent == null ? Intent.PURCHASE : intent,
                customerEmail, customerPhone, metadata);
    }

    public CreatePaymentRequest withCustomer(String email, String phone) {
        return new CreatePaymentRequest(orderId, amount, returnUrl, callbackUrl, description, language, intent, email, phone, metadata);
    }

    /** Passed through to providers that keep metadata with the order. */
    public CreatePaymentRequest withMetadata(Map<String, String> metadata) {
        Map<String, String> copy = metadata == null ? Collections.emptyMap() : Collections.unmodifiableMap(new LinkedHashMap<>(metadata));
        return new CreatePaymentRequest(orderId, amount, returnUrl, callbackUrl, description, language, intent, customerEmail, customerPhone, copy);
    }

    public String orderId() {
        return orderId;
    }

    public Money amount() {
        return amount;
    }

    public String returnUrl() {
        return returnUrl;
    }

    /** Or null. */
    public String callbackUrl() {
        return callbackUrl;
    }

    /** Or null. */
    public String description() {
        return description;
    }

    /** Or null for the provider's default. */
    public Language language() {
        return language;
    }

    public Intent intent() {
        return intent;
    }

    /** Or null. */
    public String customerEmail() {
        return customerEmail;
    }

    /** Or null. */
    public String customerPhone() {
        return customerPhone;
    }

    /** Never null; empty when not set. */
    public Map<String, String> metadata() {
        return metadata;
    }
}
