package com.keytd.manat;

import java.util.Set;

/**
 * What every provider adapter implements. Use it through {@link Manat}, which validates input first.
 * Optional operations throw {@code unsupported_operation} unless the adapter overrides them.
 */
public interface PaymentProvider {
    /** Short lower-case name, such as {@code "kapitalbank"}. */
    String name();

    Set<Capability> capabilities();

    CreatePaymentResult createPayment(CreatePaymentRequest request);

    PaymentEvent verifyCallback(IncomingCallback callback);

    default PaymentState getStatus(String paymentId) {
        throw unsupported(Capability.GET_STATUS);
    }

    default RefundResult refund(RefundRequest request) {
        throw unsupported(Capability.REFUND);
    }

    default PaymentState capture(PaymentAction action) {
        throw unsupported(Capability.CAPTURE);
    }

    default PaymentState cancel(PaymentAction action) {
        throw unsupported(Capability.CANCEL);
    }

    /** The error for an operation this adapter does not have. */
    default ManatError unsupported(Capability capability) {
        return new ManatError(ManatError.Code.UNSUPPORTED_OPERATION, name() + " does not support " + capability.value(), name(), null, null, null, null);
    }
}
