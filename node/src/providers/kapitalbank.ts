/**
 * Kapital Bank (TXPG REST API).
 * The redirect back (?ID=…&STATUS=…) is unsigned, so verifyCallback re-reads the order.
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
    Money,
    PaymentAction,
    PaymentEvent,
    PaymentProvider,
    PaymentState,
    PaymentStatus,
    RefundRequest,
    RefundResult,
} from '../types.js';

export interface KapitalBankConfig {
    /** API user issued by the bank, e.g. "TerminalSys/xxxx". */
    username: string;
    password: string;
    environment?: 'test' | 'production';
    /** Override the API base URL (ends with /api). */
    baseUrl?: string;
}

const BASE_URLS = {
    test: 'https://txpgtst.kapitalbank.az/api',
    production: 'https://e-commerce.kapitalbank.az/api',
} as const;

const CAPABILITIES: ReadonlySet<Capability> = new Set(['createPayment', 'verifyCallback', 'getStatus', 'refund', 'capture', 'cancel']);

interface KbOrder {
    id: number | string;
    status?: string;
    hppUrl?: string;
    password?: string;
    amount?: number | string;
    currency?: string;
    typeRid?: string;
    clearedChargeAmount?: number;
    clearedRefundAmount?: number;
    authorizedChargeAmount?: number;
}

interface KbError {
    errorCode?: string;
    errorDescription?: string;
}

/** Maps an order's status. */
export function mapKapitalStatus(order: Pick<KbOrder, 'status' | 'clearedChargeAmount' | 'clearedRefundAmount'>): PaymentStatus {
    switch (order.status) {
        case 'Preparing':
            return 'pending';
        case 'Authorized':
            return 'authorized';
        case 'PartPaid':
        case 'FullyPaid':
        case 'Funded':
        case 'Closed': {
            const refunded = Number(order.clearedRefundAmount ?? 0);
            if (refunded > 0) return refunded >= Number(order.clearedChargeAmount ?? Infinity) ? 'refunded' : 'partially_refunded';
            return 'paid';
        }
        case 'Refunded':
            return 'refunded';
        case 'Cancelled':
        case 'Voided':
        case 'Expired':
            return 'cancelled';
        case 'Rejected':
        case 'Refused':
        case 'Declined':
            return 'failed';
        default:
            throw new ManatError('provider_error', `kapitalbank: unknown order status "${order.status}"`, {provider: 'kapitalbank', raw: order});
    }
}

export class KapitalBankProvider implements PaymentProvider {
    readonly name = 'kapitalbank';
    readonly capabilities = CAPABILITIES;
    private readonly http: HttpClient;
    private readonly baseUrl: string;
    private readonly authHeader: string;

    constructor(config: KapitalBankConfig, options: HttpOptions = {}) {
        if (!config?.username || !config.password) throw new ManatError('invalid_request', 'kapitalbank: username and password are required');
        this.baseUrl = (config.baseUrl ?? BASE_URLS[config.environment ?? 'production']).replace(/\/+$/, '');
        this.authHeader = `Basic ${Buffer.from(`${config.username}:${config.password}`, 'utf8').toString('base64')}`;
        this.http = new HttpClient(this.name, options);
    }

    async createPayment(request: CreatePaymentRequest): Promise<CreatePaymentResult> {
        const order: Record<string, unknown> = {
            typeRid: request.intent === 'authorize' ? 'Order_DMS' : 'Order_SMS',
            amount: toDecimalString(request.amount),
            currency: request.amount.currency,
            language: request.language ?? 'az',
            description: request.description ?? request.orderId,
            hppRedirectUrl: request.returnUrl,
        };
        const data = await this.call<{order: KbOrder}>('POST', '/order', {order});
        const created = data.order;
        if (!created?.id || !created.hppUrl || !created.password) {
            throw new ManatError('provider_error', 'kapitalbank: order response is missing id, hppUrl or password', {provider: this.name, raw: data});
        }
        const url = new URL(created.hppUrl);
        url.searchParams.set('id', String(created.id));
        url.searchParams.set('password', created.password);
        return {
            paymentId: String(created.id),
            status: created.status ? mapKapitalStatus(created) : 'pending',
            next: {type: 'redirect', url: url.toString()},
            raw: data,
        };
    }

    async verifyCallback(callback: IncomingCallback): Promise<PaymentEvent> {
        const id = pick(callback.query, 'ID') ?? pick(callback.query, 'id') ?? pickBody(callback.body, 'ID');
        if (!id || !/^\d+$/.test(id)) {
            throw new ManatError('invalid_signature', 'kapitalbank: callback has no numeric ID', {provider: this.name, raw: callback});
        }
        // The redirect is unsigned; the API is the source of truth.
        const state = await this.getStatus({paymentId: id});
        const event: PaymentEvent = {paymentId: state.paymentId, status: state.status, raw: state.raw};
        if (state.amount) event.amount = state.amount;
        return event;
    }

    async getStatus({paymentId}: {paymentId: string}): Promise<PaymentState> {
        const order = await this.fetchOrder(paymentId);
        return this.toState(order);
    }

    async refund(request: RefundRequest): Promise<RefundResult> {
        const amount = request.amount ?? (await this.remainingRefundable(request.paymentId));
        const data = await this.exec(request.paymentId, {phase: 'Single', amount: toDecimalString(amount), type: 'Refund'});
        const state = await this.getStatus({paymentId: request.paymentId});
        const result: RefundResult = {paymentId: request.paymentId, status: state.status, raw: {exec: data, order: state.raw}};
        const actionId = (data as {tran?: {match?: {tranActionId?: string}}}).tran?.match?.tranActionId;
        if (actionId) result.refundId = actionId;
        return result;
    }

    async capture({paymentId, amount}: PaymentAction): Promise<PaymentState> {
        const tran: Record<string, unknown> = {phase: 'Clearing'};
        tran.amount = toDecimalString(amount ?? (await this.getStatus({paymentId})).amount ?? missingAmount(paymentId));
        await this.exec(paymentId, tran);
        return this.getStatus({paymentId});
    }

    async cancel({paymentId}: PaymentAction): Promise<PaymentState> {
        const current = await this.getStatus({paymentId});
        // Reversal of a pre-auth hold uses phase Auth; a same-day purchase uses phase Single.
        const phase = current.status === 'authorized' ? 'Auth' : 'Single';
        await this.exec(paymentId, {phase, voidKind: 'Full'});
        return this.getStatus({paymentId});
    }

    private async remainingRefundable(paymentId: string): Promise<Money> {
        const order = await this.fetchOrder(paymentId);
        const currency = toCurrency(order.currency, paymentId);
        const charged = fromDecimal(String(order.clearedChargeAmount ?? order.amount ?? 0), currency).amount;
        const refunded = order.clearedRefundAmount ? fromDecimal(String(order.clearedRefundAmount), currency).amount : 0;
        const remaining = charged - refunded;
        if (remaining <= 0) throw new ManatError('invalid_request', `kapitalbank: order ${paymentId} has nothing left to refund`, {provider: this.name, raw: order});
        return {amount: remaining, currency};
    }

    private async fetchOrder(paymentId: string): Promise<KbOrder> {
        assertOrderId(paymentId);
        const data = await this.call<{order: KbOrder}>('GET', `/order/${paymentId}?tranDetailLevel=2&tokenDetailLevel=2&orderDetailLevel=2`);
        if (!data?.order) throw new ManatError('provider_error', `kapitalbank: no order in response for ${paymentId}`, {provider: this.name, raw: data});
        return {...data.order, id: data.order.id ?? paymentId};
    }

    private toState(order: KbOrder): PaymentState {
        const state: PaymentState = {paymentId: String(order.id), status: mapKapitalStatus(order), raw: order};
        if (order.amount !== undefined && order.currency) {
            state.amount = fromDecimal(String(order.amount), toCurrency(order.currency, String(order.id)));
        }
        if (order.clearedRefundAmount && order.currency) {
            state.refundedAmount = fromDecimal(String(order.clearedRefundAmount), toCurrency(order.currency, String(order.id)));
        }
        return state;
    }

    private exec(paymentId: string, tran: Record<string, unknown>): Promise<unknown> {
        assertOrderId(paymentId);
        return this.call('POST', `/order/${paymentId}/exec-tran`, {tran});
    }

    private async call<T>(method: 'GET' | 'POST', path: string, body?: unknown): Promise<T> {
        const {status, data} = await this.http.json<T & KbError>({
            method,
            url: `${this.baseUrl}${path}`,
            headers: {authorization: this.authHeader},
            body,
        });
        if (status >= 400 || (data && typeof data === 'object' && 'errorCode' in data && data.errorCode)) {
            const err = (data ?? {}) as KbError;
            throw new ManatError('provider_error', `kapitalbank: ${err.errorDescription ?? `HTTP ${status}`}`, {
                provider: this.name,
                ...(err.errorCode ? {providerCode: err.errorCode} : {}),
                ...(err.errorDescription ? {providerMessage: err.errorDescription} : {}),
                raw: data,
            });
        }
        return data;
    }
}

export function kapitalBank(config: KapitalBankConfig, options?: HttpOptions): KapitalBankProvider {
    return new KapitalBankProvider(config, options);
}

function assertOrderId(paymentId: string): void {
    if (!/^\d+$/.test(paymentId)) throw new ManatError('invalid_request', `kapitalbank: paymentId must be the numeric order id, got "${paymentId}"`);
}

function toCurrency(code: string | undefined, paymentId: string): Currency {
    if (code === 'AZN' || code === 'USD' || code === 'EUR' || code === 'RUB' || code === 'TRY' || code === 'GBP') return code;
    throw new ManatError('provider_error', `kapitalbank: unexpected currency "${code}" on order ${paymentId}`, {provider: 'kapitalbank'});
}

function missingAmount(paymentId: string): never {
    throw new ManatError('provider_error', `kapitalbank: order ${paymentId} has no amount to capture`, {provider: 'kapitalbank'});
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
