<?php

declare(strict_types=1);

namespace Manat\Http;

/**
 * Sends one HTTP request. The default is CurlTransport; pass your own (for example a wrapper around
 * a PSR-18 client, or a fake in tests) as the adapter's `transport` option.
 * Throw any exception on a network failure; the client turns it into a network_error.
 */
interface Transport
{
    /** @param array<string, string> $headers lower-case names */
    public function send(string $method, string $url, array $headers, ?string $body, float $timeoutSeconds): HttpResponse;
}
