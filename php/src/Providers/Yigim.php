<?php

declare(strict_types=1);

namespace Manat\Providers;

use Manat\Callback;
use Manat\CreatePaymentResult;
use Manat\Http\Form;
use Manat\Http\HttpClient;
use Manat\Json;
use Manat\ManatError;
use Manat\Money;
use Manat\NextAction;
use Manat\PaymentEvent;
use Manat\PaymentProvider;
use Manat\PaymentState;
use Manat\PaymentStatus;
use Manat\RefundResult;

/**
 * Yığım (MAGNET).
 * The webhook carries only `reference` and is unsigned, so verifyCallback re-reads the payment.
 * 09 (issuer timeout) and S7 are not final: the money may have moved, so they stay pending until cancelled.
 */
final class Yigim implements PaymentProvider
{
    private const BASE_URLS = [
        'test' => 'https://sandbox.api.pay.yigim.az',
        'production' => 'https://api.pay.yigim.az',
    ];
    private const OK = 0;
    private const DEFAULT_TEMPLATE = 'TPL0001';
    private const REFERENCE = '/^[^\s\x00-\x1f]{1,255}$/u';

    private readonly HttpClient $http;
    private readonly string $baseUrl;

    /**
     * @param array{merchant: string, apiKey: string, biller: string, template?: string, environment?: 'test'|'production', baseUrl?: string} $config
     * @param array{transport?: \Manat\Http\Transport, timeout?: float} $options
     */
    public function __construct(private readonly array $config, array $options = [])
    {
        foreach (['merchant', 'apiKey', 'biller'] as $key) {
            if (empty($config[$key])) {
                throw new ManatError(ManatError::INVALID_REQUEST, "yigim: {$key} is required");
            }
        }
        $this->baseUrl = rtrim($config['baseUrl'] ?? self::BASE_URLS[$config['environment'] ?? 'production'], '/');
        $this->http = new HttpClient('yigim', $options);
    }

    public function name(): string
    {
        return 'yigim';
    }

    public function capabilities(): array
    {
        return ['createPayment', 'verifyCallback', 'getStatus', 'refund', 'capture', 'cancel'];
    }

    /** Maps a payment status code. */
    public static function mapStatus(?string $status): string
    {
        return match ($status) {
            'S0', 'S2', 'S7', '09' => PaymentStatus::PENDING, // waiting for card data, in progress, malfunction, issuer timeout
            'S1' => PaymentStatus::AUTHORIZED, // pre-authorised DMS payment: charge or cancel within 30 days
            '00' => PaymentStatus::PAID,
            'S4' => PaymentStatus::CANCELLED, // reversed
            'S3', '01', '02', '03', '04', '05', '06', '07', '08' => PaymentStatus::FAILED, // unknown error, declines
            default => throw new ManatError(ManatError::PROVIDER_ERROR, 'yigim: unknown payment status "' . ($status ?? 'null') . '"', provider: 'yigim'),
        };
    }

    public function createPayment(array $request): CreatePaymentResult
    {
        self::assertReference($request['orderId']);
        // The return URL travels in `extra` (name=value;name=value), which the gateway also cuts at `&`.
        if (preg_match('/[&;]/', $request['returnUrl'])) {
            throw new ManatError(ManatError::INVALID_REQUEST, 'yigim: returnUrl may carry at most one query parameter and no ";"');
        }
        /** @var Money $amount */
        $amount = $request['amount'];
        $params = [
            'reference' => $request['orderId'],
            'type' => ($request['intent'] ?? 'purchase') === 'authorize' ? 'DMS' : 'SMS',
            'amount' => (string) $amount->amount,
            'currency' => $amount->numericCurrency(),
            'biller' => $this->config['biller'],
        ];
        if (!empty($request['description'])) {
            $params['description'] = $request['description'];
        }
        $params['template'] = $this->config['template'] ?? self::DEFAULT_TEMPLATE;
        $params['language'] = $request['language'] ?? 'az';
        // The gateway appends `&reference=…` to the callback URL as is, so it needs a query string.
        if (!empty($request['callbackUrl'])) {
            $params['callback'] = str_contains($request['callbackUrl'], '?') ? $request['callbackUrl'] : $request['callbackUrl'] . '?';
        }
        $params['extra'] = "back-url={$request['returnUrl']};fail-url={$request['returnUrl']}";
        $reply = $this->call('payment/create', $params);
        if (empty($reply['url']) || !is_string($reply['url'])) {
            throw $this->error('payment page URL is missing', $reply);
        }
        return new CreatePaymentResult($request['orderId'], PaymentStatus::PENDING, NextAction::redirect($reply['url']), $reply);
    }

    public function verifyCallback(array $callback): PaymentEvent
    {
        $reference = Callback::query($callback, 'reference') ?? Callback::body($callback, 'reference');
        if ($reference === null || !preg_match(self::REFERENCE, $reference)) {
            throw new ManatError(ManatError::INVALID_SIGNATURE, 'yigim: callback has no reference', provider: 'yigim', raw: $callback);
        }
        // The webhook is unsigned; trust only what payment/status says about this reference.
        $state = $this->getStatus(['paymentId' => $reference]);
        return new PaymentEvent($state->paymentId, $state->status, $state->raw, $state->paymentId, $state->amount);
    }

    public function getStatus(array $input): PaymentState
    {
        $paymentId = $input['paymentId'];
        self::assertReference($paymentId);
        $reply = $this->call('payment/status', ['reference' => $paymentId]);
        if (!empty($reply['reference']) && $reply['reference'] !== $paymentId) {
            throw $this->error("status is for payment {$reply['reference']}, not {$paymentId}", $reply);
        }
        $status = self::mapStatus(isset($reply['status']) ? (string) $reply['status'] : null);
        $currency = is_string($reply['currency'] ?? null) ? Money::currencyFromNumeric($reply['currency']) : null;
        $amount = null;
        if (isset($reply['amount']) && $reply['amount'] !== '' && $currency !== null) {
            $amount = Money::fromDecimal(is_string($reply['amount']) ? $reply['amount'] : Json::number($reply['amount']), $currency);
        }

        return new PaymentState($paymentId, $status, $reply, $amount);
    }

    public function refund(array $request): RefundResult
    {
        $reply = $this->call('payment/refund', self::withAmount($request['paymentId'], $request['amount'] ?? null));
        $state = $this->getStatus(['paymentId' => $request['paymentId']]);
        // While payment/status still reads 00 the requested amount decides.
        $status = $state->status;
        if ($status === PaymentStatus::PAID) {
            $partial = isset($request['amount']) && $state->amount !== null && $request['amount']->amount < $state->amount->amount;
            $status = $partial ? PaymentStatus::PARTIALLY_REFUNDED : PaymentStatus::REFUNDED;
        }
        return new RefundResult($request['paymentId'], $status, ['refund' => $reply, 'payment' => $state->raw]);
    }

    public function capture(array $input): PaymentState
    {
        $this->call('payment/charge', self::withAmount($input['paymentId'], $input['amount'] ?? null));
        return $this->getStatus(['paymentId' => $input['paymentId']]);
    }

    public function cancel(array $input): PaymentState
    {
        $this->call('payment/cancel', ['reference' => $input['paymentId']]);
        return $this->getStatus(['paymentId' => $input['paymentId']]);
    }

    /** @param array<string, string> $params */
    private function call(string $command, array $params): array
    {
        self::assertReference($params['reference'] ?? '');
        $headers = ['x-merchant' => $this->config['merchant'], 'x-api-key' => $this->config['apiKey'], 'x-type' => 'JSON'];
        [$status, $data] = $this->http->json('GET', "{$this->baseUrl}/{$command}?" . Form::encode($params), $headers);
        // Replies may be nested under `response`.
        $reply = is_array($data) && is_array($data['response'] ?? null) ? $data['response'] : $data;
        if (!is_array($reply) || self::code($reply) === null) {
            throw new ManatError(ManatError::PROVIDER_ERROR, "yigim: unexpected response (HTTP {$status})", provider: 'yigim', raw: $data);
        }
        if ($status >= 400 || self::code($reply) !== self::OK) {
            throw $this->error("HTTP {$status}", $reply);
        }
        return $reply;
    }

    private function error(string $what, array $reply): ManatError
    {
        $code = self::code($reply);
        $message = isset($reply['message']) && $reply['message'] !== '' ? (string) $reply['message'] : null;
        $fromProvider = $code !== null && $code !== self::OK;
        return new ManatError(
            ManatError::PROVIDER_ERROR,
            'yigim: ' . ($fromProvider && $message !== null ? $message : $what),
            provider: 'yigim',
            providerCode: $fromProvider ? (string) $code : null,
            providerMessage: $fromProvider ? $message : null,
            raw: $reply,
        );
    }

    /** `code` comes as 0 or "0". */
    private static function code(array $reply): ?int
    {
        $code = $reply['code'] ?? null;
        if (is_int($code)) {
            return $code;
        }
        return is_string($code) && preg_match('/^-?\d+$/', $code) ? (int) $code : null;
    }

    /** @return array<string, string> */
    private static function withAmount(string $reference, ?Money $amount): array
    {
        return $amount === null ? ['reference' => $reference] : ['reference' => $reference, 'amount' => (string) $amount->amount];
    }

    private static function assertReference(string $reference): void
    {
        if (!preg_match(self::REFERENCE, $reference)) {
            throw new ManatError(ManatError::INVALID_REQUEST, "yigim: invalid reference \"{$reference}\"");
        }
    }
}
