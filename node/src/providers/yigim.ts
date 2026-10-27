/**
 * Yığım MAGNET payment gateway (api.pay.yigim.az).
 * Built from Yığım's MAGNET Merchant API specification (shared with merchants), checked against the
 * sandbox's replies and two community SDKs (The-Hasanov/yigim-magnet, paladium/yigim-gateway-go):
 *
 * - Every command is a GET with query parameters and the headers `X-Merchant`, `X-API-Key` and `X-Type: JSON`
 *   (without X-Type the gateway answers XML). Requests are not signed; the older SDKs' `X-Signature` predates the current API.
 * - Replies are HTTP 200 with `code` (0 = OK) and `message` at the top level. The specification wraps them in
 *   `response`, the sandbox does not; both shapes are read.
 * - The merchant's own `reference` (the orderId) is the payment id. Amounts are sent in qəpik with a numeric
 *   currency code; `payment/status` reports the amount in manat (100 qəpik reads "1").
 * - The webhook (`callback`) is a GET carrying only `reference` and is not signed, so verifyCallback re-reads the
 *   payment with `payment/status`. There is no per-payment return URL: the page the customer goes back to is part of
 *   the merchant's payment-page template, set up by Yığım, so `returnUrl` is not sent.
 * - `cancel` reverses a payment before the day's settlement (or releases a DMS hold); after settlement `refund` is
 *   the way back. Both take an optional amount.
 */
import {ManatError} from '../errors.js';
import {HttpClient, type HttpOptions} from '../http.js';
import {currencyFromNumeric, currencyNumericCode, fromDecimal} from '../money.js';
import type {
    Capability,
    CreatePaymentRequest,
    CreatePaymentResult,
    IncomingCallback,
    Money,
    PaymentAction,
    PaymentEvent,
    PaymentProvider,
    PaymentState,
    PaymentStatus,
    RefundRequest,
    RefundResult,
} from '../types.js';

export interface YigimConfig {
    /** Merchant name issued by Yığım (`X-Merchant`). */
    merchant: string;
    /** API key issued by Yığım (`X-API-Key`). */
    apiKey: string;
    /** Biller name provided by the acquirer. */
    biller: string;
    /** Payment page template; default `TPL0001`. */
    template?: string;
    environment?: 'test' | 'production';
    /** Override the API host. */
    baseUrl?: string;
}

const BASE_URLS = {
    test: 'https://sandbox.api.pay.yigim.az',
    production: 'https://api.pay.yigim.az',
} as const;

const CAPABILITIES: ReadonlySet<Capability> = new Set(['createPayment', 'verifyCallback', 'getStatus', 'refund', 'capture', 'cancel']);
const OK = 0;
const DEFAULT_TEMPLATE = 'TPL0001';
const REFERENCE = /^[^\s\u0000-\u001f]{1,255}$/;

interface YigimReply {
    code?: number;
    message?: string;
    url?: string;
    reference?: string;
    amount?: string | number;
    currency?: string;
    status?: string;
    refund?: unknown;
    [key: string]: unknown;
}

/** Authorization status codes (section 8.1). */
export function mapYigimStatus(status: string | undefined): PaymentStatus {
    switch (status) {
        case 'S0': // created, waiting for card data
        case 'S2': // in progress
            return 'pending';
        case 'S1': // pre-authorised DMS payment: charge or cancel within 30 days
            return 'authorized';
        case '00':
            return 'paid';
        case 'S4': // reversed
            return 'cancelled';
        case 'S3': // unknown error
        case 'S7': // system malfunction
        case '01': // refer to issuer
        case '02': // expired card
        case '03': // invalid amount
        case '04': // inactive card
        case '05': // insufficient funds
        case '06': // suspected fraud
        case '07': // exceeds withdrawal limit
        case '08': // format error
        case '09': // issuer timeout: Yığım asks the merchant to call cancel
            return 'failed';
        default:
            throw new ManatError('provider_error', `yigim: unknown payment status "${status}"`, {provider: 'yigim'});
    }
}

export class YigimProvider implements PaymentProvider {
    readonly name = 'yigim';
    readonly capabilities = CAPABILITIES;
    private readonly http: HttpClient;
    private readonly baseUrl: string;

    constructor(private readonly config: YigimConfig, options: HttpOptions = {}) {
        if (!config?.merchant) throw new ManatError('invalid_request', 'yigim: merchant is required');
        if (!config.apiKey) throw new ManatError('invalid_request', 'yigim: apiKey is required');
        if (!config.biller) throw new ManatError('invalid_request', 'yigim: biller is required');
        this.baseUrl = (config.baseUrl ?? BASE_URLS[config.environment ?? 'production']).replace(/\/+$/, '');
        this.http = new HttpClient(this.name, options);
    }

    async createPayment(request: CreatePaymentRequest): Promise<CreatePaymentResult> {
        assertReference(request.orderId);
        const params: Record<string, string> = {
            reference: request.orderId,
            type: request.intent === 'authorize' ? 'DMS' : 'SMS',
            amount: String(request.amount.amount),
            currency: currencyNumericCode(request.amount.currency),
            biller: this.config.biller,
        };
        if (request.description) params.description = request.description;
        params.template = this.config.template ?? DEFAULT_TEMPLATE;
        params.language = request.language ?? 'az';
        if (request.callbackUrl) params.callback = request.callbackUrl;
        const reply = await this.call('payment/create', params);
        if (!reply.url) throw this.error('payment page URL is missing', reply);
        return {paymentId: request.orderId, status: 'pending', next: {type: 'redirect', url: reply.url}, raw: reply};
    }

    async verifyCallback(callback: IncomingCallback): Promise<PaymentEvent> {
        const reference = pick(callback.query, 'reference') ?? pickBody(callback.body, 'reference');
        if (!reference || !REFERENCE.test(reference)) {
            throw new ManatError('invalid_signature', 'yigim: callback has no reference', {provider: this.name, raw: callback});
        }
        // The webhook is unsigned; trust only what payment/status says about this reference.
        const state = await this.getStatus({paymentId: reference});
        const event: PaymentEvent = {paymentId: state.paymentId, orderId: state.paymentId, status: state.status, raw: state.raw};
        if (state.amount) event.amount = state.amount;
        return event;
    }

    async getStatus({paymentId}: {paymentId: string}): Promise<PaymentState> {
        assertReference(paymentId);
        const reply = await this.call('payment/status', {reference: paymentId});
        if (reply.reference && reply.reference !== paymentId) throw this.error(`status is for payment ${reply.reference}, not ${paymentId}`, reply);
        let status = mapYigimStatus(reply.status);
        const state: PaymentState = {paymentId, status, raw: reply};
        const currency = typeof reply.currency === 'string' ? currencyFromNumeric(reply.currency) : undefined;
        if (reply.amount !== undefined && reply.amount !== '' && currency) state.amount = fromDecimal(String(reply.amount), currency);

        // A refund keeps status 00 and is listed under `refund`. Its entries have not been seen yet; each one's
        // `amount` is read in manat like the payment's own amount, and an entry without one counts as a full refund.
        const refunds = Array.isArray(reply.refund) ? reply.refund : [];
        if (status === 'paid' && refunds.length > 0) {
            const amounts = refunds.map((entry) => (entry && typeof entry === 'object' ? (entry as {amount?: unknown}).amount : undefined));
            if (state.amount && amounts.every((amount) => typeof amount === 'string' || typeof amount === 'number')) {
                const refunded = amounts.reduce<number>((sum, amount) => sum + fromDecimal(String(amount), state.amount!.currency).amount, 0);
                state.refundedAmount = {amount: refunded, currency: state.amount.currency};
                status = refunded >= state.amount.amount ? 'refunded' : 'partially_refunded';
            } else {
                status = 'refunded';
            }
            state.status = status;
        }
        return state;
    }

    async refund(request: RefundRequest): Promise<RefundResult> {
        const reply = await this.call('payment/refund', withAmount(request.paymentId, request.amount));
        const state = await this.getStatus({paymentId: request.paymentId});
        return {paymentId: request.paymentId, status: refundStatus(state, request.amount), raw: {refund: reply, payment: state.raw}};
    }

    async capture({paymentId, amount}: PaymentAction): Promise<PaymentState> {
        await this.call('payment/charge', withAmount(paymentId, amount));
        return this.getStatus({paymentId});
    }

    async cancel({paymentId}: PaymentAction): Promise<PaymentState> {
        await this.call('payment/cancel', {reference: paymentId});
        return this.getStatus({paymentId});
    }

    private async call(command: string, params: Record<string, string>): Promise<YigimReply> {
        assertReference(params.reference ?? '');
        const {status, data} = await this.http.json<unknown>({
            method: 'GET',
            url: `${this.baseUrl}/${command}?${new URLSearchParams(params)}`,
            headers: {'x-merchant': this.config.merchant, 'x-api-key': this.config.apiKey, 'x-type': 'JSON'},
        });
        const reply = unwrap(data);
        if (!reply || typeof reply.code !== 'number') {
            throw new ManatError('provider_error', `yigim: unexpected response (HTTP ${status})`, {provider: this.name, raw: data});
        }
        if (status >= 400 || reply.code !== OK) throw this.error(`HTTP ${status}`, reply);
        return reply;
    }

    private error(what: string, reply: YigimReply): ManatError {
        const fromProvider = typeof reply.code === 'number' && reply.code !== OK;
        return new ManatError('provider_error', `yigim: ${fromProvider && reply.message ? reply.message : what}`, {
            provider: this.name,
            ...(fromProvider ? {providerCode: String(reply.code)} : {}),
            ...(fromProvider && reply.message ? {providerMessage: reply.message} : {}),
            raw: reply,
        });
    }
}

export function yigim(config: YigimConfig, options?: HttpOptions): YigimProvider {
    return new YigimProvider(config, options);
}

/** The specification nests replies under `response`; the sandbox does not. */
function unwrap(data: unknown): YigimReply | undefined {
    if (!data || typeof data !== 'object') return undefined;
    const inner = (data as {response?: unknown}).response;
    return (inner && typeof inner === 'object' ? inner : data) as YigimReply;
}

function withAmount(reference: string, amount: Money | undefined): Record<string, string> {
    return amount ? {reference, amount: String(amount.amount)} : {reference};
}

/** The status after a refund; while payment/status still reads 00 the requested amount decides. */
function refundStatus(state: PaymentState, amount: Money | undefined): PaymentStatus {
    if (state.status !== 'paid') return state.status;
    return amount && state.amount && amount.amount < state.amount.amount ? 'partially_refunded' : 'refunded';
}

function assertReference(reference: string): void {
    if (!REFERENCE.test(reference)) throw new ManatError('invalid_request', `yigim: invalid reference "${reference}"`);
}

function pick(source: Record<string, string | string[] | undefined> | undefined, key: string): string | undefined {
    const value = source?.[key];
    return Array.isArray(value) ? value[0] : value;
}

function pickBody(body: IncomingCallback['body'], key: string): string | undefined {
    if (!body) return undefined;
    if (typeof body === 'string') return new URLSearchParams(body).get(key) ?? undefined;
    const value = body[key];
    return typeof value === 'string' ? value : undefined;
}
