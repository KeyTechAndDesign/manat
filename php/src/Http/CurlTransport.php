<?php

declare(strict_types=1);

namespace Manat\Http;

/** Default transport on ext-curl. TLS certificates are always verified. */
final class CurlTransport implements Transport
{
    public function send(string $method, string $url, array $headers, ?string $body, float $timeoutSeconds): HttpResponse
    {
        $handle = curl_init($url);
        if ($handle === false) {
            throw new \RuntimeException('curl_init failed');
        }
        $lines = [];
        foreach ($headers as $name => $value) {
            $lines[] = "{$name}: {$value}";
        }
        $options = [
            CURLOPT_CUSTOMREQUEST => $method,
            CURLOPT_HTTPHEADER => $lines,
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_FOLLOWLOCATION => false,
            CURLOPT_SSL_VERIFYPEER => true,
            CURLOPT_SSL_VERIFYHOST => 2,
            CURLOPT_TIMEOUT_MS => (int) ($timeoutSeconds * 1000),
            CURLOPT_CONNECTTIMEOUT_MS => (int) (min($timeoutSeconds, 10.0) * 1000),
        ];
        if ($body !== null) {
            $options[CURLOPT_POSTFIELDS] = $body;
        }
        curl_setopt_array($handle, $options);
        // The handle is freed when it goes out of scope (curl_close is a no-op since PHP 8).
        $text = curl_exec($handle);
        if ($text === false) {
            throw new \RuntimeException(curl_error($handle) ?: 'request failed');
        }
        return new HttpResponse((int) curl_getinfo($handle, CURLINFO_RESPONSE_CODE), (string) $text);
    }
}
