/**
 * Live check against Azericard's public test gateway. Skipped unless configured:
 *
 *   AZERICARD_TEST_KEY_FILE=/path/to/test-private.pem \
 *   AZERICARD_TEST_TERMINAL=17205356 \
 *   AZERICARD_TEST_BACKREF=https://your-request-bin.example/azericard \
 *   npx vitest run test/sandbox/azericard
 *
 * The key file is the *test* private key published at https://developer.azericard.com (section 4.2.1);
 * never point this at a production key. The test writes azericard-sandbox.html: open it in a browser,
 * pay with a test card from the docs (OTP 1111), and inspect what the gateway POSTs to BACKREF.
 */
import {readFileSync, writeFileSync} from 'node:fs';
import {describe, expect, it} from 'vitest';
import {Manat, azericard, azn} from '../../src/index.js';

const keyFile = process.env.AZERICARD_TEST_KEY_FILE;
const terminal = process.env.AZERICARD_TEST_TERMINAL ?? '17205356';
const backref = process.env.AZERICARD_TEST_BACKREF;

describe.skipIf(!keyFile || !backref)('azericard sandbox', () => {
    it('builds a signed payment form for the test gateway', async () => {
        const manat = new Manat(
            azericard({
                terminal,
                privateKey: readFileSync(keyFile ?? '', 'utf8'),
                merchantName: 'manat sandbox',
                merchantUrl: 'https://keytd.com',
                environment: 'test',
            }),
        );
        const orderId = `${Date.now()}`.slice(-12).padStart(12, '0');
        const result = await manat.createPayment({orderId, amount: azn('1.00'), description: 'manat sandbox check', returnUrl: backref ?? ''});
        expect(result.next.type).toBe('form');
        if (result.next.type !== 'form') return;

        const inputs = Object.entries(result.next.fields)
            .map(([name, value]) => `<input type="hidden" name="${name}" value="${value.replace(/"/g, '&quot;')}">`)
            .join('\n');
        writeFileSync(
            'azericard-sandbox.html',
            `<!doctype html><meta charset="utf-8"><form method="POST" action="${result.next.url}">${inputs}<button>Pay 1.00 AZN (test)</button></form>`,
        );
        console.log(`order ${orderId}: open azericard-sandbox.html and pay with a test card`);
    });
});
