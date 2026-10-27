<?php

declare(strict_types=1);

namespace Manat;

use Manat\Http\Form;

/** @internal Reads fields from what a merchant's HTTP handler received. */
final class Callback
{
    /** First value of a query parameter, or null. */
    public static function query(array $callback, string $key): ?string
    {
        $value = $callback['query'][$key] ?? null;
        if (is_array($value)) {
            $value = $value[0] ?? null;
        }
        return is_string($value) ? $value : null;
    }

    /** A field of an urlencoded string body or a parsed body array, or null. */
    public static function body(array $callback, string $key): ?string
    {
        $body = $callback['body'] ?? null;
        if (is_string($body)) {
            $body = Form::decode($body);
        }
        $value = is_array($body) ? ($body[$key] ?? null) : null;
        return is_string($value) ? $value : null;
    }
}
