<?php

declare(strict_types=1);

namespace Manat\Tests;

use PHPUnit\Framework\Assert;

/**
 * Comparisons with the vectors' JSON semantics: numbers compare by value (1 equals 1.0), but a
 * string never equals a number, unlike PHPUnit's loose assertEquals.
 */
final class Values
{
    /** Deep equality, like JavaScript's toEqual. */
    public static function assertEqual(mixed $expected, mixed $actual, string $path): void
    {
        self::compare($expected, $actual, $path, false);
    }

    /** Every key in $expected must be in $actual with a matching value, like JavaScript's toMatchObject. */
    public static function assertSubset(mixed $expected, mixed $actual, string $path): void
    {
        self::compare($expected, $actual, $path, true);
    }

    private static function compare(mixed $expected, mixed $actual, string $path, bool $subset): void
    {
        if (is_array($expected)) {
            Assert::assertIsArray($actual, "{$path}: expected an object or list, got " . var_export($actual, true));
            if (array_is_list($expected) && $expected !== []) {
                Assert::assertTrue(array_is_list($actual), "{$path}: expected a list");
                Assert::assertCount(count($expected), $actual, "{$path}: list length");
            } elseif (!$subset) {
                $missing = array_diff(array_map('strval', array_keys($expected)), array_map('strval', array_keys($actual)));
                $extra = array_diff(array_map('strval', array_keys($actual)), array_map('strval', array_keys($expected)));
                Assert::assertSame([], array_values($missing), "{$path}: missing keys");
                Assert::assertSame([], array_values($extra), "{$path}: unexpected keys");
            }
            foreach ($expected as $key => $value) {
                Assert::assertArrayHasKey($key, $actual, "{$path}.{$key} is missing");
                self::compare($value, $actual[$key], "{$path}.{$key}", $subset);
            }
            return;
        }
        if ((is_int($expected) || is_float($expected)) && (is_int($actual) || is_float($actual))) {
            Assert::assertTrue($expected == $actual, "{$path}: expected {$expected}, got {$actual}");
            return;
        }
        Assert::assertSame($expected, $actual, $path);
    }
}
