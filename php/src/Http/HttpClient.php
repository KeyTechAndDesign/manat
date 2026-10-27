<?php

declare(strict_types=1);

namespace Manat\Http;

use Manat\Json;
use Manat\ManatError;

/** @internal Wraps a Transport: encodes bodies, applies the timeout and turns failures into ManatError. */
final class HttpClient
{
    private readonly Transport $transport;
    private readonly float $timeout;

    /** @param array{transport?: Transport, timeout?: float} $options timeout in seconds, default 30 */
    public function __construct(private readonly string $provider, array $options = [])
    {
        $this->transport = $options['transport'] ?? new CurlTransport();
        $this->timeout = (float) ($options['timeout'] ?? 30.0);
    }

    /**
     * @param array<string, string> $headers
     * @param mixed $json JSON body (encoded unless null)
     * @param array<string, string>|null $form urlencoded body
     */
    public function send(string $method, string $url, array $headers = [], mixed $json = null, ?array $form = null): HttpResponse
    {
        $headers = array_change_key_case($headers, CASE_LOWER);
        $body = null;
        if ($form !== null) {
            $body = Form::encode($form);
            $headers['content-type'] ??= 'application/x-www-form-urlencoded';
        } elseif ($json !== null) {
            $body = Json::encode($json);
            $headers['content-type'] ??= 'application/json';
        }
        try {
            return $this->transport->send($method, $url, $headers, $body, $this->timeout);
        } catch (ManatError $error) {
            throw $error;
        } catch (\Throwable $error) {
            throw new ManatError(ManatError::NETWORK_ERROR, "{$this->provider}: {$method} {$url} {$error->getMessage()}", provider: $this->provider, previous: $error);
        }
    }

    /**
     * Like send(), and parses the response as JSON.
     *
     * @param array<string, string> $headers
     * @param array<string, string>|null $form
     * @return array{0: int, 1: mixed} [HTTP status, decoded body]
     */
    public function json(string $method, string $url, array $headers = [], mixed $json = null, ?array $form = null): array
    {
        $response = $this->send($method, $url, ['accept' => 'application/json'] + array_change_key_case($headers, CASE_LOWER), $json, $form);
        if ($response->body === '') {
            return [$response->status, null];
        }
        $data = Json::decode($response->body);
        if ($data === null && trim($response->body) !== 'null') {
            $excerpt = substr($response->body, 0, 200);
            throw new ManatError(ManatError::NETWORK_ERROR, "{$this->provider}: expected JSON, got HTTP {$response->status} \"{$excerpt}\"", provider: $this->provider, raw: $response->body);
        }
        return [$response->status, $data];
    }
}
