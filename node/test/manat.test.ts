import {describe, expect, it} from 'vitest';
import {Manat, isManatError} from '../src/index.js';
import type {Capability, PaymentProvider} from '../src/index.js';

function fakeProvider(capabilities: Capability[]): PaymentProvider & {calls: string[]} {
    const calls: string[] = [];
    return {
        name: 'fake',
        capabilities: new Set(capabilities),
        calls,
        async createPayment(request) {
            calls.push('createPayment');
            return {paymentId: `p-${request.orderId}`, status: 'pending', next: {type: 'redirect', url: 'https://pay.example/1'}, raw: {}};
        },
        async verifyCallback() {
            calls.push('verifyCallback');
            return {paymentId: 'p-1', status: 'paid', raw: {}};
        },
        async getStatus({paymentId}) {
            calls.push('getStatus');
            return {paymentId, status: 'paid', raw: {}};
        },
    };
}

const valid = {orderId: 'A-1', amount: {amount: 2550, currency: 'AZN' as const}, returnUrl: 'https://shop.example/return'};

async function expectCode(promise: () => Promise<unknown>, code: string) {
    try {
        await promise();
        expect.unreachable();
    } catch (error) {
        expect(isManatError(error)).toBe(true);
        expect((error as {code: string}).code).toBe(code);
    }
}

describe('Manat facade', () => {
    it('delegates valid requests to the provider', async () => {
        const provider = fakeProvider(['createPayment', 'verifyCallback', 'getStatus']);
        const manat = new Manat(provider);
        const result = await manat.createPayment(valid);
        expect(result.paymentId).toBe('p-A-1');
        expect(provider.calls).toEqual(['createPayment']);
    });

    it('validates input before calling the provider', async () => {
        const provider = fakeProvider(['createPayment', 'verifyCallback']);
        const manat = new Manat(provider);
        await expectCode(async () => manat.createPayment({...valid, orderId: ''}), 'invalid_request');
        await expectCode(async () => manat.createPayment({...valid, amount: {amount: 25.5, currency: 'AZN'}}), 'invalid_request');
        await expectCode(async () => manat.createPayment({...valid, returnUrl: 'javascript:alert(1)'}), 'invalid_request');
        await expectCode(async () => manat.createPayment({...valid, callbackUrl: '/relative'}), 'invalid_request');
        expect(provider.calls).toEqual([]);
    });

    it('reports unsupported operations instead of calling undefined methods', async () => {
        const manat = new Manat(fakeProvider(['createPayment', 'verifyCallback']));
        expect(manat.supports('refund')).toBe(false);
        await expectCode(() => manat.refund({paymentId: 'p-1'}), 'unsupported_operation');
        await expectCode(() => manat.getStatus({paymentId: 'p-1'}), 'unsupported_operation');
    });

    it('requires a paymentId for follow-up operations', async () => {
        const manat = new Manat(fakeProvider(['createPayment', 'verifyCallback', 'getStatus']));
        await expectCode(async () => manat.getStatus({paymentId: ''}), 'invalid_request');
    });
});
