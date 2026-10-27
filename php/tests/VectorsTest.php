<?php

declare(strict_types=1);

namespace Manat\Tests;

use Manat\Manat;
use Manat\ManatError;
use Manat\PaymentProvider;
use Manat\Providers\Azericard;
use Manat\Providers\Epoint;
use Manat\Providers\GoldenPay;
use Manat\Providers\KapitalBank;
use Manat\Providers\Payriff;
use Manat\Providers\Yigim;
use PHPUnit\Framework\AssertionFailedError;
use PHPUnit\Framework\Attributes\DataProvider;
use PHPUnit\Framework\TestCase;

/** Runs every vector in spec/vectors, the same files the other languages run. */
final class VectorsTest extends TestCase
{
    private const VECTORS = __DIR__ . '/../../spec/vectors';

    /** @return iterable<string, array{string, array<string, mixed>}> */
    public static function vectors(): iterable
    {
        $files = glob(self::VECTORS . '/*/*.json') ?: [];
        sort($files);
        foreach ($files as $file) {
            $name = basename(dirname($file)) . '/' . basename($file);
            yield $name => [$name, json_decode((string) file_get_contents($file), true, 512, JSON_THROW_ON_ERROR)];
        }
    }

    public function testFindsVectors(): void
    {
        self::assertNotEmpty(iterator_to_array(self::vectors()));
    }

    #[DataProvider('vectors')]
    public function testVector(string $file, array $vector): void
    {
        $provider = $vector['provider'];
        $operation = $vector['operation'];

        if ($operation === 'mapStatus') {
            foreach ($vector['cases'] as $case) {
                self::assertSame($case['expected']['status'], self::mapStatus($provider, $case['input']), json_encode($case['input']));
            }
            return;
        }
        if ($operation === 'macSource') {
            foreach ($vector['cases'] as $case) {
                self::assertSame($case['expected']['source'], Azericard::macSource($case['input']['fields'], $case['input']['order']));
            }
            return;
        }
        if ($operation === 'sign') {
            foreach ($vector['cases'] as $case) {
                self::assertSame($case['expected'], Epoint::encode($vector['config']['privateKey'], $case['input']['json']));
            }
            return;
        }

        $transport = new ScriptedTransport($vector['http'] ?? []);
        $manat = new Manat(self::provider($provider, self::resolveConfig($provider, $vector['config']), $transport));
        $expected = $vector['expected'] ?? [];

        $run = static function () use ($manat, $operation, $vector) {
            try {
                return $manat->{$operation}($vector['input'] ?? []);
            } catch (ManatError $error) {
                // A failed request check inside the transport comes back wrapped as a network_error.
                if ($error->getPrevious() instanceof AssertionFailedError) {
                    throw $error->getPrevious();
                }
                throw $error;
            }
        };
        if (array_key_exists('error', $expected)) {
            try {
                $run();
                self::fail("expected ManatError {$expected['error']}, nothing was thrown");
            } catch (ManatError $error) {
                self::assertSame($expected['error'], $error->errorCode, $error->getMessage());
                if (isset($expected['providerCode'])) {
                    self::assertSame($expected['providerCode'], $error->providerCode);
                }
            }
        } else {
            Values::assertSubset($expected, $run()->toArray(), 'result');
        }
        $transport->assertDone();
    }

    private static function provider(string $name, array $config, ScriptedTransport $transport): PaymentProvider
    {
        $options = ['transport' => $transport];
        return match ($name) {
            'kapitalbank' => new KapitalBank($config, $options),
            'azericard' => new Azericard($config, $options),
            'payriff' => new Payriff($config, $options),
            'epoint' => new Epoint($config, $options),
            'goldenpay' => new GoldenPay($config, $options),
            'yigim' => new Yigim($config, $options),
        };
    }

    private static function mapStatus(string $provider, array $input): string
    {
        return match ($provider) {
            'kapitalbank' => KapitalBank::mapStatus($input),
            'payriff' => Payriff::mapStatus($input['paymentStatus']),
            'epoint' => Epoint::mapStatus($input['status']),
            'goldenpay' => GoldenPay::mapStatus($input['status']['code']),
            'yigim' => Yigim::mapStatus($input['status']),
        };
    }

    /**
     * Turns the JSON-friendly vector config into adapter config, the same way in every language:
     * `<name>File` is read relative to the provider's vector folder, `clock` (ISO time) and
     * `nonce` (string) become fixed generator functions.
     */
    private static function resolveConfig(string $provider, array $config): array
    {
        $out = [];
        foreach ($config as $key => $value) {
            if (str_ends_with($key, 'File') && is_string($value)) {
                $out[substr($key, 0, -4)] = (string) file_get_contents(self::VECTORS . "/{$provider}/{$value}");
            } elseif ($key === 'clock' && is_string($value)) {
                $out['clock'] = static fn (): \DateTimeImmutable => new \DateTimeImmutable($value);
            } elseif ($key === 'nonce' && is_string($value)) {
                $out['nonce'] = static fn (): string => $value;
            } else {
                $out[$key] = $value;
            }
        }
        return $out;
    }
}
