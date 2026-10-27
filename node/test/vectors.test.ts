import {readdirSync, readFileSync, existsSync} from 'node:fs';
import {join, dirname} from 'node:path';
import {fileURLToPath} from 'node:url';
import {describe, expect, it} from 'vitest';
import {Manat, isManatError} from '../src/index.js';
import type {FetchLike, PaymentProvider, PaymentStatus} from '../src/index.js';
import {KapitalBankProvider, mapKapitalStatus} from '../src/providers/kapitalbank.js';
import {AzericardProvider, azericardMacSource} from '../src/providers/azericard.js';
import {PayriffProvider, mapPayriffStatus} from '../src/providers/payriff.js';
import {EpointProvider, epointEncode, mapEpointStatus} from '../src/providers/epoint.js';
import {GoldenPayProvider, mapGoldenPayStatus} from '../src/providers/goldenpay.js';

const VECTORS = join(dirname(fileURLToPath(import.meta.url)), '../../spec/vectors');

interface HttpStep {
    expect: {method: string; url: string; headers?: Record<string, string>; json?: unknown; form?: Record<string, string>};
    respond: {status: number; json?: unknown; text?: string};
}

interface Vector {
    description: string;
    provider: string;
    operation: string;
    config: Record<string, unknown>;
    input?: unknown;
    http?: HttpStep[];
    cases?: {input: unknown; expected: {status?: PaymentStatus; source?: string; data?: string; signature?: string}}[];
    expected?: Record<string, unknown>;
}

type Factory = (config: never, fetch: FetchLike) => PaymentProvider;

const PROVIDERS: Record<string, {create: Factory; mapStatus?: (input: never) => PaymentStatus}> = {
    kapitalbank: {
        create: (config, fetch) => new KapitalBankProvider(config, {fetch}),
        mapStatus: (input) => mapKapitalStatus(input),
    },
    azericard: {
        create: (config, fetch) => new AzericardProvider(config, {fetch}),
    },
    payriff: {
        create: (config, fetch) => new PayriffProvider(config, {fetch}),
        mapStatus: (input: {paymentStatus: string}) => mapPayriffStatus(input.paymentStatus),
    },
    epoint: {
        create: (config, fetch) => new EpointProvider(config, {fetch}),
        mapStatus: (input: {status: string}) => mapEpointStatus(input.status),
    },
    goldenpay: {
        create: (config, fetch) => new GoldenPayProvider(config, {fetch}),
        mapStatus: (input: {status: {code: number}}) => mapGoldenPayStatus(input.status.code),
    },
};

/**
 * Turns the JSON-friendly vector config into adapter config, the same way in every language:
 * `<name>File` is read relative to the provider's vector folder, `clock` (ISO time) and
 * `nonce` (string) become fixed generator functions.
 */
function resolveConfig(provider: string, config: Record<string, unknown>): Record<string, unknown> {
    const out: Record<string, unknown> = {};
    for (const [key, value] of Object.entries(config)) {
        if (key.endsWith('File') && typeof value === 'string') out[key.slice(0, -4)] = readFileSync(join(VECTORS, provider, value), 'utf8');
        else if (key === 'clock' && typeof value === 'string') out.clock = () => new Date(value);
        else if (key === 'nonce' && typeof value === 'string') out.nonce = () => value;
        else out[key] = value;
    }
    return out;
}

/** Fake fetch that serves the scripted steps in order and checks each request. */
function scriptedFetch(steps: HttpStep[]): {fetch: FetchLike; done: () => void} {
    let index = 0;
    const fetch: FetchLike = async (url, init) => {
        const step = steps[index++];
        if (!step) throw new Error(`unexpected request #${index}: ${init?.method} ${url}`);
        expect(init?.method, `request #${index} method`).toBe(step.expect.method);
        expect(url, `request #${index} url`).toBe(step.expect.url);
        const headers = Object.fromEntries(Object.entries((init?.headers ?? {}) as Record<string, string>).map(([k, v]) => [k.toLowerCase(), v]));
        for (const [name, value] of Object.entries(step.expect.headers ?? {})) {
            expect(headers[name.toLowerCase()], `request #${index} header ${name}`).toBe(value);
        }
        if (step.expect.json !== undefined) expect(JSON.parse(String(init?.body)), `request #${index} body`).toEqual(step.expect.json);
        if (step.expect.form !== undefined) expect(Object.fromEntries(new URLSearchParams(String(init?.body))), `request #${index} form`).toEqual(step.expect.form);
        const body = step.respond.json !== undefined ? JSON.stringify(step.respond.json) : step.respond.text ?? '';
        return new Response(body, {status: step.respond.status, headers: {'content-type': 'application/json'}});
    };
    return {fetch, done: () => expect(index, 'all scripted requests were made').toBe(steps.length)};
}

function loadVectors(): Vector[] {
    if (!existsSync(VECTORS)) return [];
    return readdirSync(VECTORS).flatMap((provider) =>
        readdirSync(join(VECTORS, provider))
            .filter((file) => file.endsWith('.json'))
            .map((file) => ({...JSON.parse(readFileSync(join(VECTORS, provider, file), 'utf8')), file: `${provider}/${file}`})),
    );
}

const vectors = loadVectors();

describe('shared test vectors', () => {
    it('finds vectors', () => expect(vectors.length).toBeGreaterThan(0));

    for (const vector of vectors as (Vector & {file: string})[]) {
        it(`${vector.file}: ${vector.description}`, async () => {
            const provider = PROVIDERS[vector.provider];
            if (!provider) throw new Error(`no adapter registered for ${vector.provider}`);

            if (vector.operation === 'mapStatus') {
                if (!provider.mapStatus) throw new Error(`${vector.provider} has no mapStatus`);
                for (const testCase of vector.cases ?? []) {
                    expect(provider.mapStatus(testCase.input as never), JSON.stringify(testCase.input)).toBe(testCase.expected.status);
                }
                return;
            }
            if (vector.operation === 'macSource') {
                for (const testCase of vector.cases ?? []) {
                    const {fields, order} = testCase.input as {fields: Record<string, string>; order: string[]};
                    expect(azericardMacSource(fields, order)).toBe(testCase.expected.source);
                }
                return;
            }

            if (vector.operation === 'sign') {
                const {privateKey} = vector.config as {privateKey: string};
                for (const testCase of vector.cases ?? []) {
                    const {json} = testCase.input as {json: string};
                    expect(epointEncode(privateKey, json)).toEqual(testCase.expected);
                }
                return;
            }

            const {fetch, done} = scriptedFetch(vector.http ?? []);
            const manat = new Manat(provider.create(resolveConfig(vector.provider, vector.config) as never, fetch));
            const run = () => (manat as unknown as Record<string, (input: unknown) => Promise<unknown>>)[vector.operation]!(vector.input);
            const expected = vector.expected ?? {};

            if ('error' in expected) {
                let caught: unknown;
                try {
                    await run();
                } catch (error) {
                    caught = error;
                }
                expect(isManatError(caught), `expected ManatError, got ${String(caught)}`).toBe(true);
                expect(caught).toMatchObject({code: expected.error, ...(expected.providerCode ? {providerCode: expected.providerCode} : {})});
            } else {
                expect(await run()).toMatchObject(expected);
            }
            done();
        });
    }
});
