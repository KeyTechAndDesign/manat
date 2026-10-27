<?php

declare(strict_types=1);

namespace Manat\Tests;

use PHPUnit\Framework\TestCase;

/** The package is advertised as dependency-free: it may require PHP and its bundled extensions only. */
final class NoDependenciesTest extends TestCase
{
    public function testRequiresOnlyPhpAndExtensions(): void
    {
        $composer = json_decode((string) file_get_contents(__DIR__ . '/../composer.json'), true, 512, JSON_THROW_ON_ERROR);
        $packages = array_filter(array_keys($composer['require'] ?? []), static fn (string $name) => $name !== 'php' && !str_starts_with($name, 'ext-'));
        self::assertSame([], array_values($packages));
    }
}
