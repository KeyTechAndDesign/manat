package com.keytd.manat;

import org.junit.jupiter.api.Test;

import java.util.ArrayList;
import java.util.Collections;
import java.util.EnumSet;
import java.util.List;
import java.util.Set;
import java.util.function.Supplier;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertThrows;

class ManatTest {
    private static final CreatePaymentRequest VALID = CreatePaymentRequest.of("A-1", Money.of(2550, Currency.AZN), "https://shop.example/return");

    /** Records calls; offers createPayment, verifyCallback and getStatus. */
    private static final class FakeProvider implements PaymentProvider {
        final List<String> calls = new ArrayList<>();

        @Override
        public String name() {
            return "fake";
        }

        @Override
        public Set<Capability> capabilities() {
            return Collections.unmodifiableSet(EnumSet.of(Capability.CREATE_PAYMENT, Capability.VERIFY_CALLBACK, Capability.GET_STATUS));
        }

        @Override
        public CreatePaymentResult createPayment(CreatePaymentRequest request) {
            calls.add("createPayment");
            return new CreatePaymentResult("p-" + request.orderId(), PaymentStatus.PENDING, NextAction.redirect("https://pay.example/1"), null);
        }

        @Override
        public PaymentEvent verifyCallback(IncomingCallback callback) {
            calls.add("verifyCallback");
            return new PaymentEvent("p-1", null, PaymentStatus.PAID, null, null, null);
        }

        @Override
        public PaymentState getStatus(String paymentId) {
            calls.add("getStatus");
            return new PaymentState(paymentId, PaymentStatus.PAID, null, null, null, null);
        }
    }

    private static void expectCode(ManatError.Code code, Supplier<?> call) {
        assertEquals(code, assertThrows(ManatError.class, call::get).code());
    }

    @Test
    void delegatesValidRequestsToTheProvider() {
        FakeProvider provider = new FakeProvider();
        assertEquals("p-A-1", new Manat(provider).createPayment(VALID).paymentId());
        assertEquals(List.of("createPayment"), provider.calls);
    }

    @Test
    void validatesInputBeforeCallingTheProvider() {
        FakeProvider provider = new FakeProvider();
        Manat manat = new Manat(provider);
        expectCode(ManatError.Code.INVALID_REQUEST, () -> manat.createPayment(CreatePaymentRequest.of("", VALID.amount(), VALID.returnUrl())));
        expectCode(ManatError.Code.INVALID_REQUEST, () -> manat.createPayment(CreatePaymentRequest.of("A-1", null, VALID.returnUrl())));
        expectCode(ManatError.Code.INVALID_REQUEST, () -> manat.createPayment(CreatePaymentRequest.of("A-1", Money.of(0, Currency.AZN), VALID.returnUrl())));
        expectCode(ManatError.Code.INVALID_REQUEST, () -> manat.createPayment(CreatePaymentRequest.of("A-1", VALID.amount(), "javascript:alert(1)")));
        expectCode(ManatError.Code.INVALID_REQUEST, () -> manat.createPayment(VALID.withCallbackUrl("/relative")));
        expectCode(ManatError.Code.INVALID_REQUEST, () -> manat.verifyCallback(IncomingCallback.of(null)));
        assertEquals(List.of(), provider.calls);
    }

    @Test
    void reportsUnsupportedOperations() {
        Manat manat = new Manat(new FakeProvider());
        assertFalse(manat.supports(Capability.REFUND));
        expectCode(ManatError.Code.UNSUPPORTED_OPERATION, () -> manat.refund(RefundRequest.of("p-1")));
        expectCode(ManatError.Code.UNSUPPORTED_OPERATION, () -> manat.capture(PaymentAction.of("p-1")));
    }

    @Test
    void requiresAPaymentIdForFollowUpOperations() {
        Manat manat = new Manat(new FakeProvider());
        expectCode(ManatError.Code.INVALID_REQUEST, () -> manat.getStatus(""));
        expectCode(ManatError.Code.INVALID_REQUEST, () -> manat.refund(null));
    }
}
