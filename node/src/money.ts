import {ManatError} from './errors.js';
import type {Currency, Money} from './types.js';

const MINOR_DIGITS: Record<Currency, number> = {AZN: 2, USD: 2, EUR: 2, RUB: 2, TRY: 2, GBP: 2};

/** ISO 4217 numeric codes, which several bank APIs require instead of letters. */
const NUMERIC_CODES: Record<Currency, string> = {AZN: '944', USD: '840', EUR: '978', RUB: '643', TRY: '949', GBP: '826'};

export function assertMoney(money: Money, field = 'amount'): void {
    if (!money || typeof money !== 'object') throw new ManatError('invalid_request', `${field} is required`);
    if (!(money.currency in MINOR_DIGITS)) throw new ManatError('invalid_request', `${field}.currency "${money.currency}" is not supported`);
    if (!Number.isSafeInteger(money.amount) || money.amount < 1) {
        throw new ManatError('invalid_request', `${field}.amount must be a positive integer in minor units (2550 = 25.50), got ${money.amount}`);
    }
}

/** 2550 AZN → "25.50" (no floats involved). */
export function toDecimalString(money: Money): string {
    assertMoney(money);
    const digits = MINOR_DIGITS[money.currency];
    const units = money.amount.toString().padStart(digits + 1, '0');
    return `${units.slice(0, -digits)}.${units.slice(-digits)}`;
}

/** "25.5" / "25.50" / 25.5 → 2550. Rejects more decimals than the currency has. */
export function fromDecimal(value: string | number, currency: Currency): Money {
    const text = typeof value === 'number' ? value.toString() : value.trim();
    const digits = MINOR_DIGITS[currency];
    const match = /^(\d+)(?:\.(\d+))?$/.exec(text);
    if (!match || (match[2]?.length ?? 0) > digits) {
        throw new ManatError('invalid_request', `"${text}" is not a valid ${currency} amount`);
    }
    const fraction = (match[2] ?? '').padEnd(digits, '0');
    return {amount: Number(`${match[1]}${fraction}`), currency};
}

export function currencyNumericCode(currency: Currency): string {
    return NUMERIC_CODES[currency];
}

export function currencyFromNumeric(code: string): Currency | undefined {
    return (Object.keys(NUMERIC_CODES) as Currency[]).find((c) => NUMERIC_CODES[c] === code);
}

/** Convenience for callers: Manat.azn(25.5) → { amount: 2550, currency: 'AZN' }. */
export function azn(value: string | number): Money {
    return fromDecimal(value, 'AZN');
}
