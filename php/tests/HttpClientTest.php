<?php

declare(strict_types=1);

namespace Manat\Tests;

use Manat\Http\HttpClient;
use Manat\Http\HttpResponse;
use Manat\Http\Transport;
use PHPUnit\Framework\TestCase;

final class HttpClientTest extends TestCase
{
    public function testIdentifiesTheSdkInEveryRequest(): void
    {
        $transport = new class implements Transport {
            public array $headers = [];

            public function send(string $method, string $url, array $headers, ?string $body, float $timeoutSeconds): HttpResponse
            {
                $this->headers = $headers;
                return new HttpResponse(200, '{}');
            }
        };
        (new HttpClient('test', ['transport' => $transport]))->json('GET', 'https://example.com');
        self::assertSame('keytd-manat/' . HttpClient::VERSION . ' (php; +https://github.com/KeyTechAndDesign/manat)', $transport->headers['user-agent']);
        self::assertMatchesRegularExpression('/^\d+\.\d+\.\d+$/', HttpClient::VERSION);
    }
}
