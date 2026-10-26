/**
 * Live check against Payriff with a Development-mode (test) application key. Skipped unless set:
 *
 *   PAYRIFF_TEST_SECRET=... npx vitest run test/sandbox/payriff
 *
 * Use the secret key of an application still in "Development" status (test mode only), never a live key.
 * It creates a 1.00 AZN order and prints the payment URL; pay with a test card from docs.payriff.com,
 * then re-run with PAYRIFF_TEST_ORDER=<orderId> to read the status.
 */
import {describe, expect, it} from 'vitest';
import {Manat, azn, payriff} from '../../src/index.js';

const secretKey = process.env.PAYRIFF_TEST_SECRET;

describe.skipIf(!secretKey)('payriff sandbox', () => {
    const sandbox = () => new Manat(payriff({secretKey: secretKey ?? ''}));

    it('creates an order and returns a payment URL', async () => {
        const result = await sandbox().createPayment({
            orderId: `manat-${Date.now()}`,
            amount: azn('1.00'),
            description: 'manat sandbox check',
            returnUrl: 'https://example.com/return',
        });
        expect(result.next.type).toBe('redirect');
        console.log(`order ${result.paymentId}: ${result.next.type === 'redirect' ? result.next.url : ''}`);
        const state = await sandbox().getStatus({paymentId: result.paymentId});
        expect(state.status).toBe('pending');
    });

    it.skipIf(!process.env.PAYRIFF_TEST_ORDER)('reads the status of a test order', async () => {
        const state = await sandbox().getStatus({paymentId: process.env.PAYRIFF_TEST_ORDER ?? ''});
        console.log(state.status, state.amount);
    });
});
