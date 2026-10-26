import {createHash, createHmac, timingSafeEqual} from 'node:crypto';

export type HashAlgorithm = 'sha1' | 'sha256' | 'sha512' | 'md5';
export type Encoding = 'hex' | 'base64';

export function hmac(algorithm: HashAlgorithm, key: string | Buffer, data: string, encoding: Encoding = 'hex'): string {
    return createHmac(algorithm, key).update(data, 'utf8').digest(encoding);
}

export function hash(algorithm: HashAlgorithm, data: string | Buffer, encoding: Encoding = 'hex'): string {
    return createHash(algorithm).update(data).digest(encoding);
}

/** Constant-time comparison; case-insensitive for hex signatures. */
export function safeEqual(actual: string | undefined | null, expected: string, options: {caseInsensitive?: boolean} = {}): boolean {
    if (typeof actual !== 'string') return false;
    const a = Buffer.from(options.caseInsensitive ? actual.toLowerCase() : actual, 'utf8');
    const b = Buffer.from(options.caseInsensitive ? expected.toLowerCase() : expected, 'utf8');
    if (a.length !== b.length) return false;
    return timingSafeEqual(a, b);
}
