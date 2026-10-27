<?php

declare(strict_types=1);

namespace Manat\Providers;

use Manat\Callback;
use Manat\CreatePaymentResult;
use Manat\Http\HttpClient;
use Manat\ManatError;
use Manat\Money;
use Manat\NextAction;
use Manat\PaymentEvent;
use Manat\PaymentProvider;
use Manat\PaymentState;
use Manat\PaymentStatus;
use Manat\RefundResult;

/**
 * GoldenPay. AZN only.
 * The redirect back is unsigned, so verifyCallback re-reads the payment.
 */
final class GoldenPay implements PaymentProvider
{
    private const BASE_URLS = [
        'test' => 'https://rest-newpgtest.goldenpay.az',
        'production' => 'https://rest-pg.goldenpay.az',
    ];
    private const SUCCESS = 1;
    private const REVERSED = 817;
    /** getPaymentResult codes that describe a payment; others are request errors. */
    private const PAYMENT_CODES = [1, 813, 814, 815, 816, 817, 818, 819];
    /** Description limit in bytes. */
    private const MAX_DESCRIPTION_BYTES = 500;
    private const PAYMENT_KEY = '/^[A-Za-z0-9-]{1,64}$/';

    private readonly HttpClient $http;
    private readonly string $baseUrl;

    /**
     * @param array{authKey: string, merchantName: string, environment?: 'test'|'production', baseUrl?: string} $config
     * @param array{transport?: \Manat\Http\Transport, timeout?: float} $options
     */
    public function __construct(private readonly array $config, array $options = [])
    {
        if (empty($config['authKey'])) {
            throw new ManatError(ManatError::INVALID_REQUEST, 'goldenpay: authKey is required');
        }
        if (empty($config['merchantName'])) {
            throw new ManatError(ManatError::INVALID_REQUEST, 'goldenpay: merchantName is required');
        }
        $this->baseUrl = rtrim($config['baseUrl'] ?? self::BASE_URLS[$config['environment'] ?? 'production'], '/');
        $this->http = new HttpClient('goldenpay', $options);
    }

    public function name(): string
    {
        return 'goldenpay';
    }

    public function capabilities(): array
    {
        return ['createPayment', 'verifyCallback', 'getStatus', 'refund'];
    }

    public static function mapStatus(?int $code): string
    {
        return match ($code) {
            self::SUCCESS => PaymentStatus::PAID,
            813, 818 => PaymentStatus::PENDING, // not finished / in progress
            814, 819 => PaymentStatus::CANCELLED, // cancelled by the customer / payment page timed out
            815, 816 => PaymentStatus::FAILED,
            self::REVERSED => PaymentStatus::REFUNDED,
            default => throw new ManatError(ManatError::PROVIDER_ERROR, 'goldenpay: status code ' . ($code ?? 'null') . ' does not describe a payment', provider: 'goldenpay'),
        };
    }

    public function createPayment(array $request): CreatePaymentResult
    {
        if (($request['intent'] ?? 'purchase') === 'authorize') {
            throw new ManatError(ManatError::UNSUPPORTED_OPERATION, 'goldenpay: pre-authorisation is not supported', provider: 'goldenpay');
        }
        /** @var Money $amount */
        $amount = $request['amount'];
        self::assertAzn($amount);
        // GoldenPay has no order id field; the description is the only text that reaches its panel.
        $description = self::truncateBytes($request['description'] ?? $request['orderId'], self::MAX_DESCRIPTION_BYTES);
        $body = [
            'merchantName' => $this->config['merchantName'],
            'amount' => $amount->amount,
            'lang' => $request['language'] ?? 'az',
            'description' => $description,
            'redirectUrl' => $request['returnUrl'],
            'hashCode' => md5($this->config['authKey'] . $this->config['merchantName'] . $amount->amount . $description),
        ];
        $response = $this->call('POST', 'getPaymentKey', $body);
        if (($response['status']['code'] ?? null) !== self::SUCCESS || empty($response['paymentKey'])) {
            throw $this->error('payment was not created', $response);
        }
        $key = (string) $response['paymentKey'];
        return new CreatePaymentResult($key, PaymentStatus::PENDING, NextAction::redirect("{$this->baseUrl}/pay/" . rawurlencode($key)), $response);
    }

    public function verifyCallback(array $callback): PaymentEvent
    {
        $key = Callback::query($callback, 'payment_key') ?? Callback::query($callback, 'paymentKey')
            ?? Callback::body($callback, 'payment_key') ?? Callback::body($callback, 'paymentKey');
        if ($key === null || !preg_match(self::PAYMENT_KEY, $key)) {
            throw new ManatError(ManatError::INVALID_SIGNATURE, 'goldenpay: redirect has no payment_key', provider: 'goldenpay', raw: $callback);
        }
        // The redirect is unsigned; trust only what getPaymentResult says about this key.
        return PaymentEvent::fromState($this->getStatus(['paymentId' => $key]));
    }

    public function getStatus(array $input): PaymentState
    {
        $paymentId = $input['paymentId'];
        if (!preg_match(self::PAYMENT_KEY, $paymentId)) {
            throw new ManatError(ManatError::INVALID_REQUEST, "goldenpay: invalid paymentId \"{$paymentId}\"");
        }
        $query = http_build_query(['payment_key' => $paymentId, 'hash_code' => md5($this->config['authKey'] . $paymentId)]);
        $response = $this->call('GET', "getPaymentResult?{$query}");
        $code = $response['status']['code'] ?? null;
        if (!in_array($code, self::PAYMENT_CODES, true)) {
            throw $this->error('status is not available', $response);
        }
        if (!empty($response['paymentKey']) && $response['paymentKey'] !== $paymentId) {
            throw $this->error("result is for payment {$response['paymentKey']}, not {$paymentId}", $response);
        }
        $amount = is_int($response['amount'] ?? null) && $response['amount'] > 0 ? new Money($response['amount'], 'AZN') : null;
        return new PaymentState($paymentId, self::mapStatus($code), $response, $amount);
    }

    public function refund(array $request): RefundResult
    {
        $paymentId = $request['paymentId'];
        if (isset($request['amount'])) {
            self::assertAzn($request['amount']);
        }
        // reversePaymentKey needs an amount, and GoldenPay has no partial-refund status, so the payment is read first.
        $payment = $this->getStatus(['paymentId' => $paymentId]);
        $amount = $request['amount'] ?? $payment->amount;
        if ($amount === null) {
            throw new ManatError(ManatError::PROVIDER_ERROR, "goldenpay: payment {$paymentId} has no amount to refund", provider: 'goldenpay', raw: $payment->raw);
        }
        $hashCode = md5($this->config['authKey'] . $paymentId);
        $response = $this->call('POST', 'reversePaymentKey', [
            'paymentKey' => $paymentId,
            'hashCode' => $hashCode,
            'amount' => $amount->amount,
            'secretKey' => md5($hashCode . $amount->amount),
        ]);
        // 817 (reversed) and 1 both mean success.
        $code = $response['status']['code'] ?? null;
        if ($code !== self::REVERSED && $code !== self::SUCCESS) {
            throw $this->error('refund failed', $response);
        }
        $partial = $payment->amount !== null && $amount->amount < $payment->amount->amount;
        return new RefundResult($paymentId, $partial ? PaymentStatus::PARTIALLY_REFUNDED : PaymentStatus::REFUNDED, ['refund' => $response, 'payment' => $payment->raw]);
    }

    private function call(string $method, string $path, ?array $body = null): array
    {
        [$status, $data] = $this->http->json($method, "{$this->baseUrl}/{$path}", [], $body);
        if (!is_array($data) || !isset($data['status']) || !is_array($data['status'])) {
            throw new ManatError(ManatError::PROVIDER_ERROR, "goldenpay: unexpected response (HTTP {$status})", provider: 'goldenpay', raw: $data);
        }
        if ($status >= 400) {
            throw $this->error("HTTP {$status}", $data);
        }
        return $data;
    }

    private function error(string $what, array $response): ManatError
    {
        $code = $response['status']['code'] ?? null;
        $message = isset($response['status']['message']) ? (string) $response['status']['message'] : null;
        // Code 1 is GoldenPay saying "success" (e.g. a result for another key), so only $what describes the problem.
        $fromProvider = $code !== null && $code !== self::SUCCESS;
        return new ManatError(
            ManatError::PROVIDER_ERROR,
            'goldenpay: ' . ($fromProvider && $message !== null ? $message : $what),
            provider: 'goldenpay',
            providerCode: $fromProvider ? (string) $code : null,
            providerMessage: $fromProvider ? $message : null,
            raw: $response,
        );
    }

    private static function assertAzn(Money $money): void
    {
        if ($money->currency !== 'AZN') {
            throw new ManatError(ManatError::INVALID_REQUEST, "goldenpay: only AZN is supported, got {$money->currency}");
        }
    }

    /** Cuts to at most $max UTF-8 bytes without splitting a character. */
    private static function truncateBytes(string $value, int $max): string
    {
        if (strlen($value) <= $max) {
            return $value;
        }
        preg_match_all('/./us', $value, $chars);
        $out = '';
        foreach ($chars[0] as $char) {
            if (strlen($out . $char) > $max) {
                break;
            }
            $out .= $char;
        }
        return $out;
    }
}
