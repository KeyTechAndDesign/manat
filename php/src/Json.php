<?php

declare(strict_types=1);

namespace Manat;

/** @internal JSON helpers shared by the adapters. */
final class Json
{
    /**
     * Compact JSON with `/` and non-ASCII left as they are, so signed payloads match the other languages
     * (JavaScript's JSON.stringify, Python's json.dumps(..., separators=(",", ":"), ensure_ascii=False)).
     */
    public static function encode(mixed $value): string
    {
        return json_encode($value, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_LINE_TERMINATORS | JSON_THROW_ON_ERROR);
    }

    /** Decodes to arrays; returns null for invalid JSON. */
    public static function decode(string $text): mixed
    {
        try {
            return json_decode($text, true, 512, JSON_THROW_ON_ERROR | JSON_BIGINT_AS_STRING);
        } catch (\JsonException) {
            return null;
        }
    }

    /**
     * Decimal text for an amount that came in JSON as a number: 1.5 → "1.5", 149.99 → "149.99", 5.0 → "5".
     * Does not depend on the serialize_precision ini setting.
     */
    public static function number(int|float $value): string
    {
        if (is_int($value)) {
            return (string) $value;
        }
        return rtrim(rtrim(sprintf('%.6F', $value), '0'), '.');
    }
}
