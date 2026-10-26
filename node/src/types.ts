export type Currency = 'AZN' | 'USD' | 'EUR' | 'RUB' | 'TRY' | 'GBP';

/** Integer amount in minor units: 2550 = 25.50 AZN. */
export interface Money {
    amount: number;
    currency: Currency;
}

export type PaymentStatus =
    | 'pending'
    | 'authorized'
    | 'paid'
    | 'failed'
    | 'cancelled'
    | 'refunded'
    | 'partially_refunded';

export type Language = 'az' | 'en' | 'ru';

export interface CreatePaymentRequest {
    orderId: string;
    amount: Money;
    returnUrl: string;
    callbackUrl?: string;
    description?: string;
    language?: Language;
    intent?: 'purchase' | 'authorize';
    customer?: {email?: string; phone?: string};
    metadata?: Record<string, string>;
}

export type NextAction =
    | {type: 'redirect'; url: string}
    | {type: 'form'; url: string; method: 'POST'; fields: Record<string, string>}
    | {type: 'none'};

export interface CreatePaymentResult {
    paymentId: string;
    status: PaymentStatus;
    next: NextAction;
    raw: unknown;
}

export interface IncomingCallback {
    method: string;
    query?: Record<string, string | string[] | undefined>;
    body?: Record<string, unknown> | string;
    headers?: Record<string, string | string[] | undefined>;
}

export interface PaymentEvent {
    paymentId: string;
    orderId?: string;
    status: PaymentStatus;
    amount?: Money;
    /**
     * Opaque provider data needed for later operations (e.g. Azericard's RRN and INT_REF).
     * Store it with the order and pass it back to refund/capture/cancel unchanged.
     */
    providerRef?: string;
    raw: unknown;
}

export interface PaymentState {
    paymentId: string;
    status: PaymentStatus;
    amount?: Money;
    refundedAmount?: Money;
    providerRef?: string;
    raw: unknown;
}

/** Input for capture/cancel. */
export interface PaymentAction {
    paymentId: string;
    /** From PaymentEvent.providerRef, for providers that need it. */
    providerRef?: string;
    amount?: Money;
}

export interface RefundRequest {
    paymentId: string;
    /** From PaymentEvent.providerRef, for providers that need it. */
    providerRef?: string;
    /** Omit for a full refund. */
    amount?: Money;
}

export interface RefundResult {
    paymentId: string;
    refundId?: string;
    status: PaymentStatus;
    raw: unknown;
}

export type Capability = 'createPayment' | 'verifyCallback' | 'getStatus' | 'refund' | 'capture' | 'cancel';

/** What every provider adapter implements. Optional operations may be absent. */
export interface PaymentProvider {
    readonly name: string;
    readonly capabilities: ReadonlySet<Capability>;
    createPayment(request: CreatePaymentRequest): Promise<CreatePaymentResult>;
    verifyCallback(callback: IncomingCallback): Promise<PaymentEvent>;
    getStatus?(input: {paymentId: string}): Promise<PaymentState>;
    refund?(request: RefundRequest): Promise<RefundResult>;
    capture?(input: PaymentAction): Promise<PaymentState>;
    cancel?(input: PaymentAction): Promise<PaymentState>;
}
