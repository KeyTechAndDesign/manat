import {readFileSync} from 'node:fs';
import {describe, expect, it} from 'vitest';

// The package is advertised as dependency-free: nothing may be installed alongside it.
describe('package.json', () => {
    it('has no runtime dependencies', () => {
        const pkg = JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8'));
        expect(pkg.dependencies ?? {}).toEqual({});
        expect(pkg.peerDependencies ?? {}).toEqual({});
        expect(pkg.optionalDependencies ?? {}).toEqual({});
    });
});
