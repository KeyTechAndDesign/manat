<?php

declare(strict_types=1);

namespace Manat\Providers;

use Manat\Callback;
use Manat\CreatePaymentResult;
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
 * Payriff (API v3).
 * The webhook is unsigned, so verifyCallback re-reads the order. There is no void for a pre-auth.
 */
final class Payriff implements PaymentProvider
{
    private const DEFAULT_BASE_URL = 'https://api.payriff.com';
    private const SUCCESS = '00000';

    private readonly HttpClient $http;
    private readonly string $baseUrl;

    /**
     * @param array{secretKey: string, baseUrl?: string} $config
     * @param array{transport?: \Manat\Http\Transport, timeout?: float} $options
     */
    public function __construct(private readonly array $config, array $options = [])
    {
        if (empty($config['secretKey'])) {
            throw new ManatError(ManatError::INVALID_REQUEST, 'payriff: secretKey is required');
        }
        $this->baseUrl = rtrim($config['baseUrl'] ?? self::DEFAULT_BASE_URL, '/');
        $this->http = new HttpClient('payriff', $options);
    }

    public function name(): string
    {
        return 'payriff';
    }

    public function capabilities(): array
    {
        return ['createPayment', 'verifyCallback', 'getStatus', 'refund', 'capture'];
    }

    /** Maps payload.paymentStatus. */
    public static function mapStatus(?string $status): string
    {
        return match ($status) {
            'CREATED', 'PENDING', 'ACCEPTED', 'IN_REVIEW' => PaymentStatus::PENDING,
            'APPROVED', 'PAID', 'PARTIAL', 'CASH' => PaymentStatus::PAID, // PARTIAL: pre-auth partially captured
            'PREAUTH_APPROVED' => PaymentStatus::AUTHORIZED,
            'REFUNDED', 'REFUND_IN_PROGRESS' => PaymentStatus::REFUNDED,
            'PARTIAL_REFUND' => PaymentStatus::PARTIALLY_REFUNDED,
            'CANCELED', 'EXPIRED', 'REVERSE', 'PREAUTH_EXPIRED' => PaymentStatus::CANCELLED,
            'DECLINED' => PaymentStatus::FAILED,
            default => throw new ManatError(ManatError::PROVIDER_ERROR, 'payriff: unknown paymentStatus "' . ($status ?? 'null') . '"', provider: 'payriff'),
        };
    }

    public function createPayment(array $request): CreatePaymentResult
    {
        if (isset($request['callbackUrl']) && !str_starts_with($request['callbackUrl'], 'https://')) {
            throw new ManatError(ManatError::INVALID_REQUEST, 'payriff: callbackUrl must be an HTTPS URL');
        }
        /** @var Money $amount */
        $amount = $request['amount'];
        $body = [
            'amount' => $amount->toNumber(),
            'currency' => $amount->currency,
            'language' => strtoupper($request['language'] ?? 'az'),
            'operation' => ($request['intent'] ?? 'purchase') === 'authorize' ? 'PRE_AUTH' : 'PURCHASE',
            'description' => $request['description'] ?? "Order {$request['orderId']}",
            'redirectUrl' => $request['returnUrl'],
        ];
        if (isset($request['callbackUrl'])) {
            $body['callbackUrl'] = $request['callbackUrl'];
        }
        if (!empty($request['metadata'])) {
            $body['metadata'] = $request['metadata'];
        }
        $payload = $this->call('POST', '/api/v3/orders', $body)['payload'] ?? null;
        if (!is_array($payload) || empty($payload['orderId']) || empty($payload['paymentUrl'])) {
            throw new ManatError(ManatError::PROVIDER_ERROR, 'payriff: order response has no orderId or paymentUrl', provider: 'payriff', raw: $payload);
        }
        return new CreatePaymentResult((string) $payload['orderId'], PaymentStatus::PENDING, NextAction::redirect((string) $payload['paymentUrl']), $payload);
    }

    public function verifyCallback(array $callback): PaymentEvent
    {
        $orderId = self::callbackOrderId($callback);
        if ($orderId === null) {
            throw new ManatError(ManatError::INVALID_SIGNATURE, 'payriff: callback has no payload.orderId', provider: 'payriff', raw: $callback['body'] ?? null);
        }
        // The webhook is unsigned; trust only what the API says about this order.
        return PaymentEvent::fromState($this->getStatus(['paymentId' => $orderId]));
    }

    public function getStatus(array $input): PaymentState
    {
        $paymentId = $input['paymentId'];
        $order = $this->call('GET', '/api/v3/orders/' . self::encodeId($paymentId))['payload'] ?? null;
        if (!is_array($order) || empty($order['orderId'])) {
            throw new ManatError(ManatError::PROVIDER_ERROR, "payriff: no order in response for {$paymentId}", provider: 'payriff', raw: $order);
        }
        $amount = null;
        if (isset($order['amount'], $order['currencyType'])) {
            $amount = Money::fromDecimal($order['amount'], self::currency($order['currencyType']));
        }
        return new PaymentState((string) $order['orderId'], self::orderStatus($order), $order, $amount);
    }

    public function refund(array $request): RefundResult
    {
        $paymentId = $request['paymentId'];
        // Payriff rejects a refund without an amount ("Request refund amount is null"), so a full refund sends the order amount.
        $amount = $request['amount'] ?? $this->getStatus(['paymentId' => $paymentId])->amount;
        if ($amount === null) {
            throw new ManatError(ManatError::PROVIDER_ERROR, "payriff: order {$paymentId} has no amount to refund", provider: 'payriff');
        }
        $envelope = $this->call('POST', '/api/v3/refund', ['orderId' => $paymentId, 'refundReason' => 'Refund', 'amount' => $amount->toNumber()]);
        $state = $this->getStatus(['paymentId' => $paymentId]);
        $refundId = isset($envelope['responseId']) ? (string) $envelope['responseId'] : null;
        return new RefundResult($paymentId, $state->status, ['refund' => $envelope, 'order' => $state->raw], $refundId);
    }

    public function capture(array $input): PaymentState
    {
        $body = ['orderId' => $input['paymentId']];
        if (isset($input['amount'])) {
            $body['amount'] = $input['amount']->toNumber();
        }
        $this->call('POST', '/api/v3/complete', $body);
        return $this->getStatus(['paymentId' => $input['paymentId']]);
    }

    /** @return array<string, mixed> the whole envelope */
    private function call(string $method, string $path, ?array $body = null): array
    {
        [$status, $data] = $this->http->json($method, $this->baseUrl . $path, ['authorization' => $this->config['secretKey']], $body);
        if (!is_array($data) || !array_key_exists('code', $data)) {
            throw new ManatError(ManatError::PROVIDER_ERROR, "payriff: unexpected response (HTTP {$status})", provider: 'payriff', raw: $data);
        }
        if ($status >= 400 || $data['code'] !== self::SUCCESS) {
            $code = $data['code'] === null ? null : (string) $data['code'];
            $message = isset($data['message']) ? (string) $data['message'] : null;
            throw new ManatError(ManatError::PROVIDER_ERROR, 'payriff: ' . ($message ?? "code {$code}"), provider: 'payriff', providerCode: $code, providerMessage: $message, raw: $data);
        }
        return $data;
    }

    /** After a partial refund Payriff reports the order as REFUNDED; only its transaction says PARTIAL_REFUND. */
    private static function orderStatus(array $order): string
    {
        $status = self::mapStatus($order['paymentStatus'] ?? null);
        if ($status === PaymentStatus::REFUNDED) {
            foreach ($order['transactions'] ?? [] as $transaction) {
                if (($transaction['status'] ?? null) === 'PARTIAL_REFUND') {
                    return PaymentStatus::PARTIALLY_REFUNDED;
                }
            }
        }
        return $status;
    }

    private static function callbackOrderId(array $callback): ?string
    {
        $body = $callback['body'] ?? null;
        if (is_string($body)) {
            $body = Json::decode($body);
        }
        $fromBody = is_array($body) ? ($body['payload']['orderId'] ?? null) : null;
        if (is_string($fromBody)) {
            return $fromBody;
        }
        return Callback::query($callback, 'orderId');
    }

    private static function encodeId(string $paymentId): string
    {
        if (!preg_match('/^[A-Za-z0-9-]{1,64}$/', $paymentId)) {
            throw new ManatError(ManatError::INVALID_REQUEST, "payriff: invalid paymentId \"{$paymentId}\"");
        }
        return $paymentId;
    }

    private static function currency(mixed $code): string
    {
        if (is_string($code) && in_array($code, Money::CURRENCIES, true)) {
            return $code;
        }
        $shown = is_scalar($code) ? (string) $code : gettype($code);
        throw new ManatError(ManatError::PROVIDER_ERROR, "payriff: unsupported currency \"{$shown}\"", provider: 'payriff');
    }
}
