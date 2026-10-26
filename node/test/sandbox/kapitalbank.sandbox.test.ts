/**
 * Live check against Kapital Bank's public test environment. Skipped unless credentials are set:
 *
 *   KAPITAL_TEST_USER=... KAPITAL_TEST_PASSWORD=... npx vitest run test/sandbox
 *
 * Use the shared test user from https://pg.kapitalbank.az/docs (never production credentials).
 * It creates an order and prints the hosted payment page URL; pay with a test card from the docs,
 * then re-run with KAPITAL_TEST_ORDER=<id> to check the status.
 */
import {describe, expect, it} from 'vitest';
import {Manat, azn, kapitalBank} from '../../src/index.js';

const user = process.env.KAPITAL_TEST_USER;
const password = process.env.KAPITAL_TEST_PASSWORD;

describe.skipIf(!user || !password)('kapitalbank sandbox', () => {
    // Built lazily: describe bodies run at collection time even when the suite is skipped.
    const sandbox = () => new Manat(kapitalBank({username: user ?? '', password: password ?? '', environment: 'test'}));

    it('creates an order and returns a hosted page URL', async () => {
        const manat = sandbox();
        const result = await manat.createPayment({
            orderId: `manat-${Date.now()}`,
            amount: azn('1.00'),
            description: 'manat sandbox check',
            returnUrl: 'https://example.com/return',
        });
        expect(result.paymentId).toMatch(/^\d+$/);
        expect(result.next.type).toBe('redirect');
        console.log(`order ${result.paymentId}: ${result.next.type === 'redirect' ? result.next.url : ''}`);

        const state = await manat.getStatus({paymentId: result.paymentId});
        expect(state.status).toBe('pending');
    });

    it.skipIf(!process.env.KAPITAL_TEST_ORDER)('reads the status of a paid test order', async () => {
        const state = await sandbox().getStatus({paymentId: process.env.KAPITAL_TEST_ORDER ?? ''});
        console.log(state.status, state.amount);
        expect(['paid', 'refunded', 'partially_refunded', 'authorized']).toContain(state.status);
    });
});
