/**
 * Epoint gateway, API v1.
 * Docs: https://epoint.az/en/authentication (cross-checked with the rafoabbas/epoint-php and
 * integrify/epoint community SDKs).
 *
 * - Every call is a form POST of `data` (base64 of a JSON object) and `signature` =
 *   base64(sha1(private_key + data + private_key)). Epoint signs its callbacks the same way.
 * - The callback goes to the result URL set in the Epoint merchant panel, not to a per-request URL,
 *   so `callbackUrl` is not sent. The browser redirect back to the shop carries no signed data.
 * - Amounts are sent as decimal strings ("25.50"), as in the docs' own signed example, so no
 *   float formatting is involved and every language produces the same `data`.
 * - Neither the status response nor the callback names a currency, so `amount` is left out of
 *   events and states (it is in `raw.amount`).
 * - Refunds use `/reverse` (full or partial). `/refund-request` is a payout to a saved card, not a
 *   refund, and is not used. Pre-auth (`/pre-auth-request`, `/pre-auth-complete`) is left out: the
 *   documented statuses have no value for a held but uncaptured payment.
 */
import {ManatError} from '../errors.js';
import {hash, safeEqual} from '../crypto.js';
import {HttpClient, type HttpOptions} from '../http.js';
import {toDecimalString} from '../money.js';
import type {
    Capability,
    CreatePaymentRequest,
    CreatePaymentResult,
    IncomingCallback,
    PaymentEvent,
    PaymentProvider,
    PaymentState,
    PaymentStatus,
    RefundRequest,
    RefundResult,
} from '../types.js';

export interface EpointConfig {
    /** Merchant public key, e.g. "i000000001". */
    publicKey: string;
    /** Merchant private key from the Epoint panel; signs requests and verifies callbacks. */
    privateKey: string;
    /** Override the API base (default https://epoint.az/api/1). */
    baseUrl?: string;
}

const DEFAULT_BASE_URL = 'https://epoint.az/api/1';
const CAPABILITIES: ReadonlySet<Capability> = new Set(['createPayment', 'verifyCallback', 'getStatus', 'refund']);
const CURRENCIES = new Set(['AZN', 'USD', 'EUR', 'RUB']);

interface EpointResponse {
    status?: string;
    message?: string;
    code?: string;
    transaction?: string;
    redirect_url?: string;
    order_id?: string;
    trace_id?: string;
}

/** base64(sha1(privateKey + data + privateKey)), the signature on every request and callback. */
export function epointSignature(privateKey: string, data: string): string {
    return hash('sha1', privateKey + data + privateKey, 'base64');
}

/** Encodes a payload the way the adapter sends it: compact JSON, then base64, then signed. */
export function epointEncode(privateKey: string, json: string): {data: string; signature: string} {
    const data = Buffer.from(json, 'utf8').toString('base64');
    return {data, signature: epointSignature(privateKey, data)};
}

/** Payment statuses from the callback and get-status docs. "server_error" is handled by getStatus. */
export function mapEpointStatus(status: string | undefined): PaymentStatus {
    switch (status) {
        case 'new':
            return 'pending';
        case 'success':
            return 'paid';
        case 'returned':
            return 'refunded';
        case 'failed':
        case 'error':
            return 'failed';
        default:
            throw new ManatError('provider_error', `epoint: unknown status "${status}"`, {provider: 'epoint'});
    }
}

export class EpointProvider implements PaymentProvider {
    readonly name = 'epoint';
    readonly capabilities = CAPABILITIES;
    private readonly http: HttpClient;
    private readonly baseUrl: string;

    constructor(private readonly config: EpointConfig, options: HttpOptions = {}) {
        if (!config?.publicKey) throw new ManatError('invalid_request', 'epoint: publicKey is required');
        if (!config.privateKey) throw new ManatError('invalid_request', 'epoint: privateKey is required');
        this.baseUrl = (config.baseUrl ?? DEFAULT_BASE_URL).replace(/\/+$/, '');
        this.http = new HttpClient(this.name, options);
    }

    async createPayment(request: CreatePaymentRequest): Promise<CreatePaymentResult> {
        if (request.intent === 'authorize') {
            throw new ManatError('unsupported_operation', 'epoint: pre-authorisation is not supported (Epoint statuses do not tell a hold from a capture)', {provider: this.name});
        }
        if (!CURRENCIES.has(request.amount.currency)) {
            throw new ManatError('invalid_request', `epoint: currency ${request.amount.currency} is not supported (AZN, USD, EUR, RUB)`);
        }
        if (request.orderId.length > 255) throw new ManatError('invalid_request', 'epoint: orderId is longer than 255 characters');
        const payload: Record<string, string> = {
            public_key: this.config.publicKey,
            amount: toDecimalString(request.amount),
            currency: request.amount.currency,
            language: request.language ?? 'az',
            order_id: request.orderId,
        };
        if (request.description) payload.description = truncate(request.description, 1000);
        payload.success_redirect_url = request.returnUrl;
        payload.error_redirect_url = request.returnUrl;

        const response = await this.call('request', payload);
        if (response.status !== 'success' || !response.redirect_url || !response.transaction) {
            throw this.error('payment was not created', response);
        }
        return {paymentId: response.transaction, status: 'pending', next: {type: 'redirect', url: response.redirect_url}, raw: response};
    }

    async verifyCallback(callback: IncomingCallback): Promise<PaymentEvent> {
        const fields = formFields(callback.body);
        const data = fields.data;
        const signature = fields.signature;
        // An empty data still has a valid signature (sha1(key + key)), so it is rejected outright.
        if (!data || !signature) {
            throw new ManatError('invalid_signature', 'epoint: callback has no data/signature (the browser redirect is not signed; use getStatus)', {provider: this.name, raw: callback.body});
        }
        if (!safeEqual(signature, epointSignature(this.config.privateKey, data))) {
            throw new ManatError('invalid_signature', 'epoint: callback signature does not match', {provider: this.name, raw: callback.body});
        }
        const payload = decodeData(data);
        if (!payload?.transaction) throw new ManatError('provider_error', 'epoint: signed callback has no transaction', {provider: this.name, raw: payload});
        const event: PaymentEvent = {paymentId: payload.transaction, status: mapEpointStatus(payload.status), raw: payload};
        if (payload.order_id) event.orderId = String(payload.order_id);
        return event;
    }

    async getStatus({paymentId}: {paymentId: string}): Promise<PaymentState> {
        const response = await this.call('get-status', {public_key: this.config.publicKey, transaction: paymentId});
        // server_error means Epoint could not check the payment with the bank; the outcome is unknown.
        if (response.status === 'server_error' || !response.transaction) throw this.error('status is not available', response);
        if (response.transaction !== paymentId) throw this.error(`status is for transaction ${response.transaction}, not ${paymentId}`, response);
        return {paymentId, status: mapEpointStatus(response.status), raw: response};
    }

    async refund(request: RefundRequest): Promise<RefundResult> {
        const payload: Record<string, string> = {
            public_key: this.config.publicKey,
            language: 'az',
            transaction: request.paymentId,
        };
        if (request.amount) payload.amount = toDecimalString(request.amount);
        payload.currency = request.amount?.currency ?? 'AZN';

        const response = await this.call('reverse', payload);
        if (response.status !== 'success') throw this.error('refund failed', response);
        const state = await this.getStatus({paymentId: request.paymentId});
        // Epoint has no partial-refund status: a reversed payment still reported as "success" was partly refunded.
        const status = state.status === 'paid' ? 'partially_refunded' : state.status;
        return {paymentId: request.paymentId, status, raw: {refund: response, payment: state.raw}};
    }

    private async call(method: string, payload: Record<string, string>): Promise<EpointResponse> {
        const {data, signature} = epointEncode(this.config.privateKey, JSON.stringify(payload));
        const {status, data: body} = await this.http.json<EpointResponse>({
            method: 'POST',
            url: `${this.baseUrl}/${method}`,
            body: new URLSearchParams({data, signature}),
        });
        if (!body || typeof body !== 'object') throw new ManatError('provider_error', `epoint: unexpected response (HTTP ${status})`, {provider: this.name, raw: body});
        if (status >= 400) throw this.error(`HTTP ${status}`, body);
        return body;
    }

    private error(what: string, response: EpointResponse): ManatError {
        return new ManatError('provider_error', `epoint: ${response.message ?? what}`, {
            provider: this.name,
            ...(response.code ? {providerCode: String(response.code)} : {}),
            ...(response.message ? {providerMessage: response.message} : {}),
            raw: response,
        });
    }
}

export function epoint(config: EpointConfig, options?: HttpOptions): EpointProvider {
    return new EpointProvider(config, options);
}

function formFields(body: IncomingCallback['body']): Record<string, string | undefined> {
    if (typeof body === 'string') return Object.fromEntries(new URLSearchParams(body));
    if (!body || typeof body !== 'object') return {};
    const pick = (key: string) => (typeof body[key] === 'string' ? (body[key] as string) : undefined);
    return {data: pick('data'), signature: pick('signature')};
}

function decodeData(data: string): (EpointResponse & Record<string, unknown>) | undefined {
    if (!/^[A-Za-z0-9+/]+={0,2}$/.test(data)) throw new ManatError('provider_error', 'epoint: callback data is not base64', {provider: 'epoint', raw: data});
    try {
        const value: unknown = JSON.parse(Buffer.from(data, 'base64').toString('utf8'));
        return value && typeof value === 'object' && !Array.isArray(value) ? (value as EpointResponse & Record<string, unknown>) : undefined;
    } catch {
        throw new ManatError('provider_error', 'epoint: callback data is not JSON', {provider: 'epoint', raw: data});
    }
}

function truncate(value: string, max: number): string {
    const chars = Array.from(value);
    return chars.length > max ? chars.slice(0, max).join('') : value;
}
