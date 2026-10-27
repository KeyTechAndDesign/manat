/**
 * Azericard (form POST, RSA-signed).
 * Each message is signed over the length-prefixed values of a fixed field list (macFields).
 * The callback signature does not cover ORDER or ACTION, so only a signed APPROVAL counts as approved.
 */
import {createPrivateKey, createPublicKey, randomBytes, sign, verify, type KeyObject} from 'node:crypto';
import {ManatError} from '../errors.js';
import {HttpClient, type HttpOptions} from '../http.js';
import {currencyFromNumeric, fromDecimal, toDecimalString} from '../money.js';
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

export type AzericardMessage = 'purchase' | 'completion' | 'reversal' | 'callback' | 'status';

export interface AzericardConfig {
    /** TERMINAL id assigned by the bank (8 characters). */
    terminal: string;
    /** Merchant RSA private key (PEM, at least 2048-bit) used to sign requests. */
    privateKey: string;
    /** Gateway (MPI) public key (PEM) used to verify callbacks. Required for verifyCallback. */
    gatewayPublicKey?: string;
    /** MERCH_NAME shown to the cardholder (1–50 chars). */
    merchantName: string;
    /** MERCH_URL: the shop's main website. */
    merchantUrl: string;
    environment?: 'test' | 'production';
    /** Override the cgi_link URL. */
    gatewayUrl?: string;
    /** EMAIL: where the gateway may send result notifications. */
    email?: string;
    /** COUNTRY: only if the shop is outside Azerbaijan. */
    country?: string;
    /** MERCH_GMT: only if the shop's time zone differs from the gateway's (e.g. "+4"). */
    merchantGmt?: string;
    /** Override MAC field orders if your terminal was configured differently. */
    macFields?: Partial<Record<AzericardMessage, string[]>>;
    /** Testing hooks: fixed clock and nonce. */
    clock?: () => Date;
    nonce?: () => string;
}

const GATEWAY_URLS = {
    test: 'https://testmpi.3dsecure.az/cgi-bin/cgi_link',
    production: 'https://mpi.3dsecure.az/cgi-bin/cgi_link',
} as const;

/** MAC field orders per message type. */
export const AZERICARD_MAC_FIELDS: Readonly<Record<AzericardMessage, readonly string[]>> = {
    purchase: ['AMOUNT', 'CURRENCY', 'TERMINAL', 'TRTYPE', 'TIMESTAMP', 'NONCE', 'MERCH_URL'],
    completion: ['AMOUNT', 'CURRENCY', 'TERMINAL', 'TRTYPE', 'ORDER', 'RRN', 'INT_REF'],
    reversal: ['AMOUNT', 'CURRENCY', 'TERMINAL', 'TRTYPE', 'ORDER', 'RRN', 'INT_REF'],
    callback: ['AMOUNT', 'TERMINAL', 'APPROVAL', 'RRN', 'INT_REF'],
    status: ['ORDER', 'TERMINAL', 'TRTYPE', 'TIMESTAMP', 'NONCE'],
};

const ACTION_MESSAGES: Record<string, string> = {
    '0': 'Approved',
    '1': 'Duplicate transaction',
    '2': 'Transaction declined',
    '3': 'Transaction processing error',
    '6': 'Repeat of a declined transaction',
    '7': 'Repeat of a transaction with an authentication error',
    '8': 'Repeat of a transaction that terminated without a response',
};

const CAPABILITIES: ReadonlySet<Capability> = new Set(['createPayment', 'verifyCallback', 'getStatus', 'refund', 'capture', 'cancel']);
const DAY_MS = 24 * 60 * 60 * 1000;

/** Length-prefixed MAC source: "11.48" → "511.48", empty → "-". Lengths are in bytes. */
export function azericardMacSource(fields: Record<string, string | undefined>, order: readonly string[]): string {
    return order
        .map((name) => {
            const value = fields[name] ?? '';
            return value === '' ? '-' : `${Buffer.byteLength(value, 'utf8')}${value}`;
        })
        .join('');
}

/** GMT timestamp YYYYMMDDHHMMSS. */
export function azericardTimestamp(date: Date): string {
    return date.toISOString().replace(/[-:T]/g, '').slice(0, 14);
}

interface ProviderRef {
    rrn: string;
    intRef: string;
    amount: Money;
    timestamp: string;
    trtype: string;
}

export class AzericardProvider implements PaymentProvider {
    readonly name = 'azericard';
    readonly capabilities = CAPABILITIES;
    private readonly http: HttpClient;
    private readonly url: string;
    private readonly privateKey: KeyObject;
    private readonly gatewayKey: KeyObject | undefined;
    private readonly macFields: Record<AzericardMessage, readonly string[]>;
    private readonly clock: () => Date;
    private readonly makeNonce: () => string;

    constructor(private readonly config: AzericardConfig, options: HttpOptions = {}) {
        if (!config?.terminal) throw new ManatError('invalid_request', 'azericard: terminal is required');
        if (!config.merchantName || !config.merchantUrl) throw new ManatError('invalid_request', 'azericard: merchantName and merchantUrl are required');
        if (!config.privateKey) throw new ManatError('invalid_request', 'azericard: privateKey (PEM) is required');
        this.privateKey = loadKey(() => createPrivateKey(config.privateKey), 'privateKey');
        assertRsa2048(this.privateKey, 'privateKey');
        this.gatewayKey = config.gatewayPublicKey ? loadKey(() => createPublicKey(config.gatewayPublicKey!), 'gatewayPublicKey') : undefined;
        this.url = config.gatewayUrl ?? GATEWAY_URLS[config.environment ?? 'production'];
        this.macFields = {...AZERICARD_MAC_FIELDS, ...config.macFields};
        this.clock = config.clock ?? (() => new Date());
        this.makeNonce = config.nonce ?? (() => randomBytes(16).toString('hex'));
        this.http = new HttpClient(this.name, options);
    }

    async createPayment(request: CreatePaymentRequest): Promise<CreatePaymentResult> {
        if (!/^\d{6,32}$/.test(request.orderId)) {
            throw new ManatError(
                'invalid_request',
                `azericard: orderId must be 6–32 digits (got "${request.orderId}"); its last 6 digits must be unique per terminal per day`,
            );
        }
        const fields = compact({
            AMOUNT: toDecimalString(request.amount),
            CURRENCY: request.amount.currency,
            ORDER: request.orderId,
            DESC: truncate(request.description ?? `Order ${request.orderId}`, 50),
            MERCH_NAME: truncate(this.config.merchantName, 50),
            MERCH_URL: this.config.merchantUrl,
            TERMINAL: this.config.terminal,
            EMAIL: this.config.email,
            TRTYPE: request.intent === 'authorize' ? '0' : '1',
            COUNTRY: this.config.country,
            MERCH_GMT: this.config.merchantGmt,
            TIMESTAMP: azericardTimestamp(this.clock()),
            NONCE: this.makeNonce(),
            BACKREF: request.callbackUrl ?? request.returnUrl,
            LANG: request.language ?? 'az',
        });
        fields.P_SIGN = this.sign(fields, 'purchase');
        return {
            paymentId: request.orderId,
            status: 'pending',
            next: {type: 'form', url: this.url, method: 'POST', fields},
            raw: {fields},
        };
    }

    async verifyCallback(callback: IncomingCallback): Promise<PaymentEvent> {
        if (!this.gatewayKey) {
            throw new ManatError('invalid_request', 'azericard: gatewayPublicKey is required to verify callbacks; never accept them unverified');
        }
        const fields = readFields(callback);
        const signature = fields.P_SIGN;
        if (!signature || !/^[0-9a-fA-F]+$/.test(signature) || signature.length % 2 !== 0) {
            throw new ManatError('invalid_signature', 'azericard: callback has no valid P_SIGN', {provider: this.name, raw: fields});
        }
        const source = azericardMacSource(fields, this.macFields.callback);
        if (!verify('sha256', Buffer.from(source, 'utf8'), this.gatewayKey, Buffer.from(signature, 'hex'))) {
            throw new ManatError('invalid_signature', 'azericard: callback P_SIGN does not match', {provider: this.name, raw: fields});
        }
        if (fields.TERMINAL !== this.config.terminal) {
            throw new ManatError('invalid_signature', `azericard: callback is for terminal ${fields.TERMINAL}, not ${this.config.terminal}`, {provider: this.name, raw: fields});
        }
        if (!fields.ORDER) throw new ManatError('invalid_signature', 'azericard: callback has no ORDER', {provider: this.name, raw: fields});

        const amount = parseAmount(fields.AMOUNT, fields.CURRENCY);
        // ACTION is unsigned; the signed APPROVAL code is what proves an approval.
        const approved = fields.ACTION === '0' && !!fields.APPROVAL;
        const event: PaymentEvent = {paymentId: fields.ORDER, orderId: fields.ORDER, status: approved ? statusForTrtype(fields.TRTYPE) : 'failed', raw: fields};
        if (amount) event.amount = amount;
        if (approved && fields.RRN && fields.INT_REF && amount && fields.TIMESTAMP) {
            event.providerRef = encodeRef({rrn: fields.RRN, intRef: fields.INT_REF, amount, timestamp: fields.TIMESTAMP, trtype: fields.TRTYPE ?? '1'});
        }
        return event;
    }

    /** TRTYPE 90 answers for one transaction type, so the purchase (or pre-auth), completion and reversals are each queried. */
    async getStatus({paymentId}: {paymentId: string}): Promise<PaymentState> {
        if (!/^\d{6,32}$/.test(paymentId)) throw new ManatError('invalid_request', `azericard: paymentId must be the numeric ORDER, got "${paymentId}"`);
        let trtype = '1';
        let data = await this.queryStatus(paymentId, trtype);
        if (data.responseCode === '-24') {
            trtype = '0';
            data = await this.queryStatus(paymentId, trtype);
        }
        let status = mapStatusResponse(data, trtype);
        const amount = parseAmount(data.amount, data.currency);
        const state: PaymentState = {paymentId, status, raw: {[trtype]: data}};
        if (amount) state.amount = amount;
        if ((status === 'paid' || status === 'authorized') && amount) {
            const related = await this.relatedTransactions(paymentId, trtype === '0' ? ['21', '22', '24'] : ['22', '24']);
            state.raw = {[trtype]: data, ...related.raw};
            if (status === 'authorized' && related.approved.has('21')) status = 'paid';
            const refunded = related.approvedAmount('22', amount.currency) + related.approvedAmount('24', amount.currency);
            if (refunded > 0) {
                state.refundedAmount = {amount: refunded, currency: amount.currency};
                if (status === 'authorized') status = 'cancelled';
                else status = refunded >= amount.amount ? 'refunded' : 'partially_refunded';
            }
            state.status = status;
            const paidAt = data.tranDate ? bakuTimeToGmt(data.tranDate) : data.timestamp;
            if (data.rrn && data.intRef && paidAt) {
                state.providerRef = encodeRef({rrn: data.rrn, intRef: data.intRef, amount, timestamp: paidAt, trtype: related.approved.has('21') ? '21' : trtype});
            }
        }
        return state;
    }

    private async relatedTransactions(order: string, types: string[]) {
        const raw: Record<string, AzericardStatusResponse> = {};
        const approved = new Map<string, AzericardStatusResponse>();
        for (const type of types) {
            const data = await this.queryStatus(order, type);
            raw[type] = data;
            if (data.actionCode === '0' && data.responseCode === '00') approved.set(type, data);
        }
        return {
            raw,
            approved,
            approvedAmount(type: string, currency: Currency): number {
                const entry = approved.get(type);
                const money = entry ? parseAmount(entry.amount, entry.currency ?? currency) : undefined;
                return money && money.currency === currency ? money.amount : 0;
            },
        };
    }

    private async queryStatus(order: string, tranTrtype: string): Promise<AzericardStatusResponse> {
        const fields: Record<string, string> = {
            TRAN_TRTYPE: tranTrtype,
            ORDER: order,
            TERMINAL: this.config.terminal,
            TRTYPE: '90',
            TIMESTAMP: azericardTimestamp(this.clock()),
            NONCE: this.makeNonce(),
        };
        fields.P_SIGN = this.sign(fields, 'status');
        const {status, data} = await this.http.json<AzericardStatusResponse>({method: 'POST', url: this.url, body: new URLSearchParams(fields)});
        if (status >= 400 || !data || typeof data !== 'object') {
            throw new ManatError('provider_error', `azericard: status request failed (HTTP ${status})`, {provider: this.name, raw: data});
        }
        return data;
    }

    async capture(input: PaymentAction): Promise<PaymentState> {
        const ref = requireRef(input.providerRef);
        const amount = input.amount ?? ref.amount;
        await this.send('completion', '21', input.paymentId, ref, amount);
        return {paymentId: input.paymentId, status: 'paid', amount, providerRef: encodeRef({...ref, amount, trtype: '21'}), raw: null};
    }

    async cancel(input: PaymentAction): Promise<PaymentState> {
        const ref = requireRef(input.providerRef);
        await this.send('reversal', '22', input.paymentId, ref, ref.amount);
        return {paymentId: input.paymentId, status: 'cancelled', amount: ref.amount, raw: null};
    }

    async refund(request: RefundRequest): Promise<RefundResult> {
        const ref = requireRef(request.providerRef);
        const amount = request.amount ?? ref.amount;
        if (amount.currency !== ref.amount.currency || amount.amount > ref.amount.amount) {
            throw new ManatError('invalid_request', 'azericard: refund amount exceeds the paid amount or uses another currency');
        }
        // Online reversal (22) works once, before settlement; otherwise it is offline (24).
        const paidAt = parseTimestamp(ref.timestamp);
        let trtype = '24';
        if (paidAt && this.clock().getTime() - paidAt.getTime() < DAY_MS) {
            const online = await this.queryStatus(request.paymentId, '22');
            trtype = online.actionCode === '0' && online.responseCode === '00' ? '24' : '22';
        }
        const text = await this.send('reversal', trtype, request.paymentId, ref, amount);
        return {paymentId: request.paymentId, status: amount.amount < ref.amount.amount ? 'partially_refunded' : 'refunded', raw: {trtype, action: text}};
    }

    private async send(message: AzericardMessage, trtype: string, order: string, ref: ProviderRef, amount: Money): Promise<string> {
        const fields: Record<string, string> = {
            AMOUNT: toDecimalString(amount),
            CURRENCY: amount.currency,
            ORDER: order,
            RRN: ref.rrn,
            INT_REF: ref.intRef,
            TERMINAL: this.config.terminal,
            TRTYPE: trtype,
            TIMESTAMP: azericardTimestamp(this.clock()),
            NONCE: this.makeNonce(),
        };
        fields.P_SIGN = this.sign(fields, message);
        const response = await this.http.send({method: 'POST', url: this.url, body: new URLSearchParams(fields)});
        const action = response.text.trim();
        if (response.status >= 400) {
            throw new ManatError('provider_error', `azericard: HTTP ${response.status}`, {provider: this.name, raw: response.text});
        }
        if (action !== '0') {
            const message = ACTION_MESSAGES[action] ?? `Unexpected response "${action.slice(0, 100)}"`;
            throw new ManatError('provider_error', `azericard: ${message}`, {provider: this.name, providerCode: action.slice(0, 20), providerMessage: message, raw: response.text});
        }
        return action;
    }

    private sign(fields: Record<string, string>, message: AzericardMessage): string {
        const source = azericardMacSource(fields, this.macFields[message]);
        return sign('sha256', Buffer.from(source, 'utf8'), this.privateKey).toString('hex');
    }
}

export function azericard(config: AzericardConfig, options?: HttpOptions): AzericardProvider {
    return new AzericardProvider(config, options);
}

/** TRTYPE 90 reply. */
export interface AzericardStatusResponse {
    terminal?: string;
    actionCode?: string;
    responseCode?: string;
    statusMsg?: string;
    approval_id?: string;
    card?: string;
    amount?: string;
    currency?: string;
    tranDate?: string;
    rrn?: string;
    intRef?: string;
    tr_type?: string;
    nonce?: string;
    timestamp?: string;
    signature?: string;
}

export function mapStatusResponse(data: AzericardStatusResponse, tranTrtype: string): PaymentStatus {
    if (data.actionCode === '0' && data.responseCode === '00') return tranTrtype === '0' ? 'authorized' : 'paid';
    if (data.responseCode === '-40') return 'pending'; // "Client side transaction form in progress"
    return 'failed';
}

/** "2026.10.26 19:06:40" in Baku time (UTC+4, no DST) → "20261026150640" GMT. */
function bakuTimeToGmt(value: string): string | undefined {
    const m = /^(\d{4})\.(\d{2})\.(\d{2}) (\d{2}):(\d{2}):(\d{2})$/.exec(value);
    if (!m) return undefined;
    return azericardTimestamp(new Date(Date.UTC(+m[1]!, +m[2]! - 1, +m[3]!, +m[4]! - 4, +m[5]!, +m[6]!)));
}

function statusForTrtype(trtype: string | undefined): PaymentStatus {
    switch (trtype) {
        case '0':
            return 'authorized';
        case '22':
        case '24':
            return 'refunded';
        default:
            return 'paid';
    }
}

function encodeRef(ref: ProviderRef): string {
    return ['az1', ref.rrn, ref.intRef, toDecimalString(ref.amount), ref.amount.currency, ref.timestamp, ref.trtype].join('|');
}

function requireRef(value: string | undefined): ProviderRef {
    const parts = value?.split('|') ?? [];
    if (parts.length !== 7 || parts[0] !== 'az1' || !parts[1] || !parts[2]) {
        throw new ManatError('invalid_request', 'azericard: providerRef from the payment callback is required (store event.providerRef with the order)');
    }
    const [, rrn, intRef, amount, currency, timestamp, trtype] = parts as [string, string, string, string, string, string, string];
    return {rrn, intRef, amount: fromDecimal(amount, toCurrency(currency)), timestamp, trtype};
}

function parseAmount(amount: string | undefined, currency: string | undefined): Money | undefined {
    if (!amount || !currency) return undefined;
    const alpha = /^\d{3}$/.test(currency) ? currencyFromNumeric(currency) : currency;
    return alpha ? fromDecimal(amount, toCurrency(alpha)) : undefined;
}

function toCurrency(code: string): Currency {
    if (code === 'AZN' || code === 'USD' || code === 'EUR' || code === 'RUB' || code === 'TRY' || code === 'GBP') return code;
    throw new ManatError('provider_error', `azericard: unsupported currency "${code}"`, {provider: 'azericard'});
}

function parseTimestamp(value: string): Date | undefined {
    const m = /^(\d{4})(\d{2})(\d{2})(\d{2})(\d{2})(\d{2})$/.exec(value);
    return m ? new Date(Date.UTC(+m[1]!, +m[2]! - 1, +m[3]!, +m[4]!, +m[5]!, +m[6]!)) : undefined;
}

function readFields(callback: IncomingCallback): Record<string, string> {
    const out: Record<string, string> = {};
    const add = (key: string, value: unknown) => {
        const v = Array.isArray(value) ? value[0] : value;
        if (typeof v === 'string') out[key.toUpperCase()] = v.trim();
    };
    for (const [k, v] of Object.entries(callback.query ?? {})) add(k, v);
    if (typeof callback.body === 'string') new URLSearchParams(callback.body).forEach((v, k) => add(k, v));
    else for (const [k, v] of Object.entries(callback.body ?? {})) add(k, v);
    return out;
}

function compact(fields: Record<string, string | undefined>): Record<string, string> {
    return Object.fromEntries(Object.entries(fields).filter((entry): entry is [string, string] => entry[1] !== undefined && entry[1] !== ''));
}

function truncate(value: string, max: number): string {
    const chars = Array.from(value);
    return chars.length > max ? chars.slice(0, max).join('') : value;
}

function loadKey<T>(load: () => T, name: string): T {
    try {
        return load();
    } catch (error) {
        throw new ManatError('invalid_request', `azericard: ${name} is not a valid PEM key`, {cause: error});
    }
}

function assertRsa2048(key: KeyObject, name: string): void {
    const bits = key.asymmetricKeyDetails?.modulusLength ?? 0;
    if (key.asymmetricKeyType !== 'rsa' || bits < 2048) throw new ManatError('invalid_request', `azericard: ${name} must be an RSA key of at least 2048 bits`);
}
