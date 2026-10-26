import {ManatError} from './errors.js';
import {assertMoney} from './money.js';
import type {
    Capability,
    CreatePaymentRequest,
    CreatePaymentResult,
    IncomingCallback,
    Money,
    PaymentEvent,
    PaymentProvider,
    PaymentState,
    RefundRequest,
    RefundResult,
} from './types.js';

/**
 * Provider-neutral facade. Validates input once, then delegates to the adapter,
 * so every provider gets the same checks and the same errors.
 */
export class Manat {
    constructor(readonly provider: PaymentProvider) {}

    get capabilities(): ReadonlySet<Capability> {
        return this.provider.capabilities;
    }

    supports(capability: Capability): boolean {
        return this.provider.capabilities.has(capability);
    }

    createPayment(request: CreatePaymentRequest): Promise<CreatePaymentResult> {
        if (!request?.orderId || typeof request.orderId !== 'string') throw new ManatError('invalid_request', 'orderId is required');
        assertMoney(request.amount);
        assertUrl(request.returnUrl, 'returnUrl');
        if (request.callbackUrl !== undefined) assertUrl(request.callbackUrl, 'callbackUrl');
        return this.provider.createPayment(request);
    }

    verifyCallback(callback: IncomingCallback): Promise<PaymentEvent> {
        if (!callback || typeof callback.method !== 'string') throw new ManatError('invalid_request', 'callback.method is required');
        return this.provider.verifyCallback(callback);
    }

    getStatus(input: {paymentId: string}): Promise<PaymentState> {
        requirePaymentId(input);
        return this.call('getStatus', this.provider.getStatus, input);
    }

    refund(request: RefundRequest): Promise<RefundResult> {
        requirePaymentId(request);
        if (request.amount !== undefined) assertMoney(request.amount);
        return this.call('refund', this.provider.refund, request);
    }

    capture(input: {paymentId: string; amount?: Money}): Promise<PaymentState> {
        requirePaymentId(input);
        if (input.amount !== undefined) assertMoney(input.amount);
        return this.call('capture', this.provider.capture, input);
    }

    cancel(input: {paymentId: string}): Promise<PaymentState> {
        requirePaymentId(input);
        return this.call('cancel', this.provider.cancel, input);
    }

    private call<I, O>(name: Capability, fn: ((input: I) => Promise<O>) | undefined, input: I): Promise<O> {
        if (!fn || !this.provider.capabilities.has(name)) {
            return Promise.reject(new ManatError('unsupported_operation', `${this.provider.name} does not support ${name}`, {provider: this.provider.name}));
        }
        return fn.call(this.provider, input);
    }
}

function requirePaymentId(input: {paymentId?: unknown}): void {
    if (!input?.paymentId || typeof input.paymentId !== 'string') throw new ManatError('invalid_request', 'paymentId is required');
}

function assertUrl(value: unknown, field: string): void {
    try {
        const url = new URL(String(value));
        if (url.protocol !== 'https:' && url.protocol !== 'http:') throw new Error('scheme');
    } catch {
        throw new ManatError('invalid_request', `${field} must be an absolute http(s) URL`);
    }
}
