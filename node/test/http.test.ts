import {readFileSync} from 'node:fs';
import {describe, expect, it} from 'vitest';
import {HttpClient} from '../src/http.js';
import {VERSION} from '../src/version.js';

describe('HttpClient', () => {
    it('identifies the SDK in every request', async () => {
        let sent: Record<string, string> = {};
        const http = new HttpClient('test', {
            fetch: async (_url, init) => {
                sent = init?.headers as Record<string, string>;
                return new Response('{}', {status: 200});
            },
        });
        await http.json({method: 'GET', url: 'https://example.com'});
        expect(sent['user-agent']).toBe(`keytd-manat/${VERSION} (node; +https://github.com/KeyTechAndDesign/manat)`);
    });

    it('reports the version in package.json', () => {
        const pkg = JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8'));
        expect(VERSION).toBe(pkg.version);
    });
});
