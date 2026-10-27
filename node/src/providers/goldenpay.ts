/**
 * GoldenPay. AZN only.
 * The redirect back is unsigned, so verifyCallback re-reads the payment.
 */
import {ManatError} from '../errors.js';
import {hash} from '../crypto.js';
import {HttpClient, type HttpOptions} from '../http.js';
import type {
    Capability,
    CreatePaymentRequest,
    CreatePaymentResult,
    IncomingCallback,
    Money,
    PaymentEvent,
    PaymentProvider,
    PaymentState,
    PaymentStatus,
    RefundRequest,
    RefundResult,
} from '../types.js';

export interface GoldenPayConfig {
    /** Auth key from the GoldenPay merchant panel; signs every request. */
    authKey: string;
    /** Merchant name issued by GoldenPay. */
    merchantName: string;
    environment?: 'test' | 'production';
    /** Override the API host (also used for the payment page). */
    baseUrl?: string;
}

const BASE_URLS = {
    test: 'https://rest-newpgtest.goldenpay.az',
    production: 'https://rest-pg.goldenpay.az',
} as const;

const CAPABILITIES: ReadonlySet<Capability> = new Set(['createPayment', 'verifyCallback', 'getStatus', 'refund']);
const SUCCESS = 1;
const REVERSED = 817;
/** Description limit in bytes. */
const MAX_DESCRIPTION_BYTES = 500;

interface GoldenPayResponse {
    status?: {code?: number; message?: string};
    paymentKey?: string | null;
    merchantName?: string | null;
    amount?: number | null;
    checkCount?: number | null;
    paymentDate?: string | null;
    cardNumber?: string | null;
    language?: string | null;
    description?: string | null;
    rrn?: string | null;
}

/** getPaymentResult codes that describe a payment; others are request errors. */
export function mapGoldenPayStatus(code: number | undefined): PaymentStatus {
    switch (code) {
        case SUCCESS:
            return 'paid';
        case 813: // payment page not opened yet, or waiting for payment
        case 818: // in progress
            return 'pending';
        case 814: // cancelled by the customer
        case 819: // payment page timed out
            return 'cancelled';
        case 815: // payment error
        case 816: // payment failed
            return 'failed';
        case REVERSED:
            return 'refunded';
        default:
            throw new ManatError('provider_error', `goldenpay: status code ${code} does not describe a payment`, {provider: 'goldenpay'});
    }
}

const PAYMENT_CODES = new Set([SUCCESS, 813, 814, 815, 816, REVERSED, 818, 819]);

export class GoldenPayProvider implements PaymentProvider {
    readonly name = 'goldenpay';
    readonly capabilities = CAPABILITIES;
    private readonly http: HttpClient;
    private readonly baseUrl: string;

    constructor(private readonly config: GoldenPayConfig, options: HttpOptions = {}) {
        if (!config?.authKey) throw new ManatError('invalid_request', 'goldenpay: authKey is required');
        if (!config.merchantName) throw new ManatError('invalid_request', 'goldenpay: merchantName is required');
        this.baseUrl = (config.baseUrl ?? BASE_URLS[config.environment ?? 'production']).replace(/\/+$/, '');
        this.http = new HttpClient(this.name, options);
    }

    async createPayment(request: CreatePaymentRequest): Promise<CreatePaymentResult> {
        if (request.intent === 'authorize') {
            throw new ManatError('unsupported_operation', 'goldenpay: pre-authorisation is not supported', {provider: this.name});
        }
        assertAzn(request.amount);
        // GoldenPay has no order id field; the description is the only text that reaches its panel.
        const description = truncateBytes(request.description ?? request.orderId, MAX_DESCRIPTION_BYTES);
        const amount = request.amount.amount;
        const body = {
            merchantName: this.config.merchantName,
            amount,
            lang: request.language ?? 'az',
            description,
            redirectUrl: request.returnUrl,
            hashCode: md5(this.config.authKey + this.config.merchantName + amount + description),
        };
        const response = await this.call('POST', 'getPaymentKey', body);
        if (response.status?.code !== SUCCESS || !response.paymentKey) throw this.error('payment was not created', response);
        const url = `${this.baseUrl}/pay/${encodeURIComponent(response.paymentKey)}`;
        return {paymentId: response.paymentKey, status: 'pending', next: {type: 'redirect', url}, raw: response};
    }

    async verifyCallback(callback: IncomingCallback): Promise<PaymentEvent> {
        const key = pick(callback.query, 'payment_key') ?? pick(callback.query, 'paymentKey') ?? pickBody(callback.body, 'payment_key') ?? pickBody(callback.body, 'paymentKey');
        if (!key || !PAYMENT_KEY.test(key)) {
            throw new ManatError('invalid_signature', 'goldenpay: redirect has no payment_key', {provider: this.name, raw: callback});
        }
        // The redirect is unsigned; trust only what getPaymentResult says about this key.
        const state = await this.getStatus({paymentId: key});
        const event: PaymentEvent = {paymentId: state.paymentId, status: state.status, raw: state.raw};
        if (state.amount) event.amount = state.amount;
        return event;
    }

    async getStatus({paymentId}: {paymentId: string}): Promise<PaymentState> {
        assertPaymentKey(paymentId);
        const query = new URLSearchParams({payment_key: paymentId, hash_code: md5(this.config.authKey + paymentId)});
        const response = await this.call('GET', `getPaymentResult?${query}`);
        const code = response.status?.code;
        if (code === undefined || !PAYMENT_CODES.has(code)) throw this.error('status is not available', response);
        if (response.paymentKey && response.paymentKey !== paymentId) throw this.error(`result is for payment ${response.paymentKey}, not ${paymentId}`, response);
        const state: PaymentState = {paymentId, status: mapGoldenPayStatus(code), raw: response};
        if (typeof response.amount === 'number' && response.amount > 0) state.amount = {amount: response.amount, currency: 'AZN'};
        return state;
    }

    async refund(request: RefundRequest): Promise<RefundResult> {
        if (request.amount) assertAzn(request.amount);
        // reversePaymentKey needs an amount, and GoldenPay has no partial-refund status, so the payment is read first.
        const payment = await this.getStatus({paymentId: request.paymentId});
        const amount = request.amount ?? payment.amount;
        if (!amount) throw new ManatError('provider_error', `goldenpay: payment ${request.paymentId} has no amount to refund`, {provider: this.name, raw: payment.raw});
        const hashCode = md5(this.config.authKey + request.paymentId);
        const response = await this.call('POST', 'reversePaymentKey', {
            paymentKey: request.paymentId,
            hashCode,
            amount: amount.amount,
            secretKey: md5(hashCode + amount.amount),
        });
        // 817 (reversed) and 1 both mean success.
        const code = response.status?.code;
        if (code !== REVERSED && code !== SUCCESS) throw this.error('refund failed', response);
        const partial = payment.amount !== undefined && amount.amount < payment.amount.amount;
        return {paymentId: request.paymentId, status: partial ? 'partially_refunded' : 'refunded', raw: {refund: response, payment: payment.raw}};
    }

    private async call(method: 'GET' | 'POST', path: string, body?: unknown): Promise<GoldenPayResponse> {
        const {status, data} = await this.http.json<GoldenPayResponse>({method, url: `${this.baseUrl}/${path}`, body});
        if (!data || typeof data !== 'object' || !data.status) throw new ManatError('provider_error', `goldenpay: unexpected response (HTTP ${status})`, {provider: this.name, raw: data});
        if (status >= 400) throw this.error(`HTTP ${status}`, data);
        return data;
    }

    private error(what: string, response: GoldenPayResponse): ManatError {
        const {code, message} = response.status ?? {};
        // Code 1 is GoldenPay saying "success" (e.g. a result for another key), so only `what` describes the problem.
        const fromProvider = code !== undefined && code !== SUCCESS;
        return new ManatError('provider_error', `goldenpay: ${fromProvider && message ? message : what}`, {
            provider: this.name,
            ...(fromProvider ? {providerCode: String(code)} : {}),
            ...(fromProvider && message ? {providerMessage: message} : {}),
            raw: response,
        });
    }
}

export function goldenPay(config: GoldenPayConfig, options?: HttpOptions): GoldenPayProvider {
    return new GoldenPayProvider(config, options);
}

const PAYMENT_KEY = /^[A-Za-z0-9-]{1,64}$/;

function assertPaymentKey(paymentId: string): void {
    if (!PAYMENT_KEY.test(paymentId)) throw new ManatError('invalid_request', `goldenpay: invalid paymentId "${paymentId}"`);
}

function assertAzn(money: Money): void {
    if (money?.currency !== 'AZN') throw new ManatError('invalid_request', `goldenpay: only AZN is supported, got ${money?.currency}`);
}

function md5(value: string): string {
    return hash('md5', value);
}

/** Cuts to at most `max` UTF-8 bytes without splitting a character. */
function truncateBytes(value: string, max: number): string {
    if (Buffer.byteLength(value, 'utf8') <= max) return value;
    let out = '';
    for (const char of value) {
        if (Buffer.byteLength(out + char, 'utf8') > max) break;
        out += char;
    }
    return out;
}

function pick(source: Record<string, unknown> | undefined, key: string): string | undefined {
    const value = source?.[key];
    const first: unknown = Array.isArray(value) ? value[0] : value;
    return typeof first === 'string' ? first : undefined;
}

function pickBody(body: IncomingCallback['body'], key: string): string | undefined {
    if (!body) return undefined;
    if (typeof body === 'string') return new URLSearchParams(body).get(key) ?? undefined;
    const value = body[key];
    return typeof value === 'string' ? value : undefined;
}
