<?php

declare(strict_types=1);

namespace Manat\Tests;

use Manat\Http\Form;
use Manat\Http\HttpResponse;
use Manat\Http\Transport;
use Manat\Json;
use PHPUnit\Framework\Assert;

/** Serves a vector's `http` steps in order and checks that each request matches `expect`. */
final class ScriptedTransport implements Transport
{
    private int $index = 0;

    /** @param list<array{expect: array<string, mixed>, respond: array<string, mixed>}> $steps */
    public function __construct(private readonly array $steps)
    {
    }

    public function send(string $method, string $url, array $headers, ?string $body, float $timeoutSeconds): HttpResponse
    {
        $step = $this->steps[$this->index++] ?? null;
        $n = $this->index;
        Assert::assertNotNull($step, "unexpected request #{$n}: {$method} {$url}");
        $expect = $step['expect'];
        Assert::assertSame($expect['method'], $method, "request #{$n} method");
        Assert::assertSame($expect['url'], $url, "request #{$n} url");
        foreach ($expect['headers'] ?? [] as $name => $value) {
            Assert::assertSame($value, $headers[strtolower($name)] ?? null, "request #{$n} header {$name}");
        }
        if (array_key_exists('json', $expect)) {
            Values::assertEqual($expect['json'], Json::decode((string) $body), "request #{$n} body");
        }
        if (array_key_exists('form', $expect)) {
            Values::assertEqual($expect['form'], Form::decode((string) $body), "request #{$n} form");
        }
        $respond = $step['respond'];
        $text = array_key_exists('json', $respond) ? Json::encode($respond['json']) : (string) ($respond['text'] ?? '');
        return new HttpResponse((int) $respond['status'], $text);
    }

    public function assertDone(): void
    {
        Assert::assertSame(count($this->steps), $this->index, 'all scripted requests were made');
    }
}
