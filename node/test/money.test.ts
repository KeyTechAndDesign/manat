import {describe, expect, it} from 'vitest';
import {azn, fromDecimal, isManatError, toDecimalString} from '../src/index.js';
import {currencyFromNumeric, currencyNumericCode} from '../src/money.js';

describe('money', () => {
    it('formats minor units without float maths', () => {
        expect(toDecimalString({amount: 2550, currency: 'AZN'})).toBe('25.50');
        expect(toDecimalString({amount: 5, currency: 'AZN'})).toBe('0.05');
        expect(toDecimalString({amount: 100, currency: 'USD'})).toBe('1.00');
        expect(toDecimalString({amount: 123456789, currency: 'AZN'})).toBe('1234567.89');
    });

    it('parses decimals into minor units', () => {
        expect(azn('25.5')).toEqual({amount: 2550, currency: 'AZN'});
        expect(azn(25.5)).toEqual({amount: 2550, currency: 'AZN'});
        expect(azn('0.05')).toEqual({amount: 5, currency: 'AZN'});
        expect(fromDecimal('10', 'EUR')).toEqual({amount: 1000, currency: 'EUR'});
    });

    it('rejects amounts that cannot be represented exactly', () => {
        for (const bad of ['25.555', '-1', 'abc', '', '1e3', 0.1 + 0.2]) {
            expect(() => azn(bad)).toThrow();
        }
    });

    it('rejects non-integer or non-positive minor units', () => {
        for (const amount of [0, -5, 25.5, Number.NaN]) {
            try {
                toDecimalString({amount, currency: 'AZN'});
                expect.unreachable();
            } catch (error) {
                expect(isManatError(error, 'invalid_request')).toBe(true);
            }
        }
    });

    it('maps ISO 4217 numeric codes both ways', () => {
        expect(currencyNumericCode('AZN')).toBe('944');
        expect(currencyFromNumeric('944')).toBe('AZN');
        expect(currencyFromNumeric('000')).toBeUndefined();
    });
});
