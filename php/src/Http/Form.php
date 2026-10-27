<?php

declare(strict_types=1);

namespace Manat\Http;

/** @internal application/x-www-form-urlencoded, without parse_str()'s renaming of keys with dots or spaces. */
final class Form
{
    /** @param array<string, string> $fields */
    public static function encode(array $fields): string
    {
        return http_build_query($fields, '', '&', PHP_QUERY_RFC1738);
    }

    /** @return array<string, string> later duplicates are ignored */
    public static function decode(string $body): array
    {
        $out = [];
        foreach (explode('&', $body) as $pair) {
            if ($pair === '') {
                continue;
            }
            $parts = explode('=', $pair, 2);
            $key = urldecode($parts[0]);
            if (!array_key_exists($key, $out)) {
                $out[$key] = urldecode($parts[1] ?? '');
            }
        }
        return $out;
    }
}
