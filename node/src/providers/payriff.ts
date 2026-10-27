/**
 * Payriff gateway, API v3.
 * Docs: https://docs.payriff.com (cross-checked with Payriff's own Node SDK, @payriff-sdk/node).
 *
 * - `Authorization: <secret key>` (no "Bearer"); the same API host serves sandbox and live,
 *   the key decides which ("Development" applications are test-only).
 * - Every response is `{code, message, payload}`; only code "00000" is success, the payment
 *   result itself is `payload.paymentStatus`.
 * - The webhook to callbackUrl is a JSON POST with `payload.orderId` and no signature; the docs
 *   say to confirm with GET /orders/{id} before fulfilling, so verifyCallback re-reads the order.
 * - Amounts are decimal numbers in major units ("amount": 25.5).
 * - Same-day refunds are reversals on Payriff's side, handled by the refund endpoint. There is no
 *   documented way to void an uncaptured pre-auth, so cancel is not supported.
 * - Test mode accepts one refund per order: after a partial refund, any further refund answers
 *   01000 "Transaction status is not approved", and the order reads REFUNDED with a PARTIAL_REFUND
 *   transaction (reported as partially_refunded).
 */
import {ManatError} from '../errors.js';
import {HttpClient, type HttpOptions} from '../http.js';
import {fromDecimal, toDecimalString} from '../money.js';
import type {
    Capability,
    CreatePaymentRequest,
    CreatePaymentResult,
    Currency,
    IncomingCallback,
    PaymentAction,
    PaymentEvent,
    PaymentProvider,
    PaymentState,
    PaymentStatus,
    RefundRequest,
    RefundResult,
} from '../types.js';

export interface PayriffConfig {
    /** Application secret key from the Payriff dashboard. */
    secretKey: string;
    /** Override the API host (default https://api.payriff.com). */
    baseUrl?: string;
}

const DEFAULT_BASE_URL = 'https://api.payriff.com';
const SUCCESS = '00000';
const CAPABILITIES: ReadonlySet<Capability> = new Set(['createPayment', 'verifyCallback', 'getStatus', 'refund', 'capture']);

interface PayriffEnvelope<T> {
    code?: string;
    message?: string;
    internalMessage?: string | null;
    responseId?: string;
    payload?: T;
}

interface PayriffOrder {
    orderId: string;
    amount?: number;
    currencyType?: string;
    paymentStatus?: string;
    operationType?: string;
    paidAmount?: number;
    transactions?: {uuid?: string; status?: string; requestRrn?: string; responseRrn?: string}[];
}

/** Payment statuses from the Enums reference plus "PAID" (used in the docs' own examples) and "IN_REVIEW" (SDK). */
export function mapPayriffStatus(status: string | undefined): PaymentStatus {
    switch (status) {
        case 'CREATED':
        case 'PENDING':
        case 'ACCEPTED':
        case 'IN_REVIEW':
            return 'pending';
        case 'APPROVED':
        case 'PAID':
        case 'PARTIAL': // pre-auth partially captured
        case 'CASH':
            return 'paid';
        case 'PREAUTH_APPROVED':
            return 'authorized';
        case 'REFUNDED':
        case 'REFUND_IN_PROGRESS':
            return 'refunded';
        case 'PARTIAL_REFUND':
            return 'partially_refunded';
        case 'CANCELED':
        case 'EXPIRED':
        case 'REVERSE':
        case 'PREAUTH_EXPIRED':
            return 'cancelled';
        case 'DECLINED':
            return 'failed';
        default:
            throw new ManatError('provider_error', `payriff: unknown paymentStatus "${status}"`, {provider: 'payriff'});
    }
}

export class PayriffProvider implements PaymentProvider {
    readonly name = 'payriff';
    readonly capabilities = CAPABILITIES;
    private readonly http: HttpClient;
    private readonly baseUrl: string;

    constructor(private readonly config: PayriffConfig, options: HttpOptions = {}) {
        if (!config?.secretKey) throw new ManatError('invalid_request', 'payriff: secretKey is required');
        this.baseUrl = (config.baseUrl ?? DEFAULT_BASE_URL).replace(/\/+$/, '');
        this.http = new HttpClient(this.name, options);
    }

    async createPayment(request: CreatePaymentRequest): Promise<CreatePaymentResult> {
        if (request.callbackUrl !== undefined && !request.callbackUrl.startsWith('https://')) {
            throw new ManatError('invalid_request', 'payriff: callbackUrl must be an HTTPS URL');
        }
        const body: Record<string, unknown> = {
            amount: Number(toDecimalString(request.amount)),
            currency: request.amount.currency,
            language: (request.language ?? 'az').toUpperCase(),
            operation: request.intent === 'authorize' ? 'PRE_AUTH' : 'PURCHASE',
            description: request.description ?? `Order ${request.orderId}`,
            redirectUrl: request.returnUrl,
        };
        if (request.callbackUrl) body.callbackUrl = request.callbackUrl;
        if (request.metadata && Object.keys(request.metadata).length) body.metadata = request.metadata;
        const payload = await this.call<{orderId?: string; paymentUrl?: string; sessionId?: string; transactionId?: number}>('POST', '/api/v3/orders', body);
        if (!payload?.orderId || !payload.paymentUrl) {
            throw new ManatError('provider_error', 'payriff: order response has no orderId or paymentUrl', {provider: this.name, raw: payload});
        }
        return {paymentId: payload.orderId, status: 'pending', next: {type: 'redirect', url: payload.paymentUrl}, raw: payload};
    }

    async verifyCallback(callback: IncomingCallback): Promise<PaymentEvent> {
        const orderId = callbackOrderId(callback);
        if (!orderId) throw new ManatError('invalid_signature', 'payriff: callback has no payload.orderId', {provider: this.name, raw: callback.body});
        // The webhook is unsigned; trust only what the API says about this order.
        const state = await this.getStatus({paymentId: orderId});
        const event: PaymentEvent = {paymentId: state.paymentId, status: state.status, raw: state.raw};
        if (state.amount) event.amount = state.amount;
        return event;
    }

    async getStatus({paymentId}: {paymentId: string}): Promise<PaymentState> {
        const order = await this.call<PayriffOrder>('GET', `/api/v3/orders/${encodeId(paymentId)}`);
        if (!order?.orderId) throw new ManatError('provider_error', `payriff: no order in response for ${paymentId}`, {provider: this.name, raw: order});
        const state: PaymentState = {paymentId: order.orderId, status: orderStatus(order), raw: order};
        if (order.amount !== undefined && order.currencyType) state.amount = fromDecimal(String(order.amount), toCurrency(order.currencyType));
        return state;
    }

    async refund(request: RefundRequest): Promise<RefundResult> {
        const body: Record<string, unknown> = {orderId: request.paymentId, refundReason: 'Refund'};
        if (request.amount) body.amount = Number(toDecimalString(request.amount));
        const envelope = await this.callEnvelope<unknown>('POST', '/api/v3/refund', body);
        const state = await this.getStatus({paymentId: request.paymentId});
        const result: RefundResult = {paymentId: request.paymentId, status: state.status, raw: {refund: envelope, order: state.raw}};
        if (envelope.responseId) result.refundId = envelope.responseId;
        return result;
    }

    async capture({paymentId, amount}: PaymentAction): Promise<PaymentState> {
        const body: Record<string, unknown> = {orderId: paymentId};
        if (amount) body.amount = Number(toDecimalString(amount));
        await this.call('POST', '/api/v3/complete', body);
        return this.getStatus({paymentId});
    }

    private async call<T>(method: 'GET' | 'POST', path: string, body?: unknown): Promise<T> {
        const envelope = await this.callEnvelope<T>(method, path, body);
        return envelope.payload as T;
    }

    private async callEnvelope<T>(method: 'GET' | 'POST', path: string, body?: unknown): Promise<PayriffEnvelope<T>> {
        const {status, data} = await this.http.json<PayriffEnvelope<T>>({
            method,
            url: `${this.baseUrl}${path}`,
            headers: {authorization: this.config.secretKey},
            body,
        });
        if (!data || typeof data !== 'object' || !('code' in data)) {
            throw new ManatError('provider_error', `payriff: unexpected response (HTTP ${status})`, {provider: this.name, raw: data});
        }
        if (status >= 400 || data.code !== SUCCESS) {
            throw new ManatError('provider_error', `payriff: ${data.message ?? `code ${data.code}`}`, {
                provider: this.name,
                ...(data.code ? {providerCode: data.code} : {}),
                ...(data.message ? {providerMessage: data.message} : {}),
                raw: data,
            });
        }
        return data;
    }
}

export function payriff(config: PayriffConfig, options?: HttpOptions): PayriffProvider {
    return new PayriffProvider(config, options);
}

/** After a partial refund Payriff reports the order as REFUNDED; only its transaction says PARTIAL_REFUND. */
function orderStatus(order: PayriffOrder): PaymentStatus {
    const status = mapPayriffStatus(order.paymentStatus);
    if (status === 'refunded' && order.transactions?.some((transaction) => transaction.status === 'PARTIAL_REFUND')) return 'partially_refunded';
    return status;
}

function callbackOrderId(callback: IncomingCallback): string | undefined {
    let body: unknown = callback.body;
    if (typeof body === 'string') {
        try {
            body = JSON.parse(body);
        } catch {
            body = undefined;
        }
    }
    const payload = body && typeof body === 'object' ? (body as {payload?: {orderId?: unknown}}).payload : undefined;
    const fromBody = payload && typeof payload.orderId === 'string' ? payload.orderId : undefined;
    const query = callback.query?.orderId;
    return fromBody ?? (typeof query === 'string' ? query : undefined);
}

function encodeId(paymentId: string): string {
    if (!/^[A-Za-z0-9-]{1,64}$/.test(paymentId)) throw new ManatError('invalid_request', `payriff: invalid paymentId "${paymentId}"`);
    return paymentId;
}

function toCurrency(code: string): Currency {
    if (code === 'AZN' || code === 'USD' || code === 'EUR' || code === 'RUB' || code === 'TRY' || code === 'GBP') return code;
    throw new ManatError('provider_error', `payriff: unsupported currency "${code}"`, {provider: 'payriff'});
}
