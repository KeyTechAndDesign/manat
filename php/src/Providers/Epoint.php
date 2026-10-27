<?php

declare(strict_types=1);

namespace Manat\Providers;

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
 * Epoint gateway, API v1.
 * Docs: https://epoint.az/en/authentication (cross-checked with the rafoabbas/epoint-php and
 * integrify/epoint community SDKs).
 *
 * - Every call is a form POST of `data` (base64 of a JSON object) and `signature` =
 *   base64(sha1(private_key + data + private_key)). Epoint signs its callbacks the same way.
 * - The callback goes to the result URL set in the Epoint merchant panel, not to a per-request URL,
 *   so `callbackUrl` is not sent. The browser redirect back to the shop carries no signed data.
 * - Amounts are sent as decimal strings ("25.50"), as in the docs' own signed example, so no
 *   float formatting is involved and every language produces the same `data`.
 * - Neither the status response nor the callback names a currency, so `amount` is left out of
 *   events and states (it is in `raw['amount']`).
 * - Refunds use `/reverse` (full or partial). `/refund-request` is a payout to a saved card, not a
 *   refund, and is not used. Pre-auth (`/pre-auth-request`, `/pre-auth-complete`) is left out: the
 *   documented statuses have no value for a held but uncaptured payment.
 */
final class Epoint implements PaymentProvider
{
    private const DEFAULT_BASE_URL = 'https://epoint.az/api/1';
    private const CURRENCIES = ['AZN', 'USD', 'EUR', 'RUB'];

    private readonly HttpClient $http;
    private readonly string $baseUrl;

    /**
     * @param array{publicKey: string, privateKey: string, baseUrl?: string} $config
     * @param array{transport?: \Manat\Http\Transport, timeout?: float} $options
     */
    public function __construct(private readonly array $config, array $options = [])
    {
        if (empty($config['publicKey'])) {
            throw new ManatError(ManatError::INVALID_REQUEST, 'epoint: publicKey is required');
        }
        if (empty($config['privateKey'])) {
            throw new ManatError(ManatError::INVALID_REQUEST, 'epoint: privateKey is required');
        }
        $this->baseUrl = rtrim($config['baseUrl'] ?? self::DEFAULT_BASE_URL, '/');
        $this->http = new HttpClient('epoint', $options);
    }

    public function name(): string
    {
        return 'epoint';
    }

    public function capabilities(): array
    {
        return ['createPayment', 'verifyCallback', 'getStatus', 'refund'];
    }

    /** base64(sha1(privateKey + data + privateKey)), the signature on every request and callback. */
    public static function signature(string $privateKey, string $data): string
    {
        return base64_encode(sha1($privateKey . $data . $privateKey, true));
    }

    /**
     * Encodes a payload the way the adapter sends it: JSON text, then base64, then signed.
     *
     * @return array{data: string, signature: string}
     */
    public static function encode(string $privateKey, string $json): array
    {
        $data = base64_encode($json);
        return ['data' => $data, 'signature' => self::signature($privateKey, $data)];
    }

    /** Payment statuses from the callback and get-status docs. "server_error" is handled by getStatus. */
    public static function mapStatus(?string $status): string
    {
        return match ($status) {
            'new' => PaymentStatus::PENDING,
            'success' => PaymentStatus::PAID,
            'returned' => PaymentStatus::REFUNDED,
            'failed', 'error' => PaymentStatus::FAILED,
            default => throw new ManatError(ManatError::PROVIDER_ERROR, 'epoint: unknown status "' . ($status ?? 'null') . '"', provider: 'epoint'),
        };
    }

    public function createPayment(array $request): CreatePaymentResult
    {
        if (($request['intent'] ?? 'purchase') === 'authorize') {
            throw new ManatError(ManatError::UNSUPPORTED_OPERATION, 'epoint: pre-authorisation is not supported (Epoint statuses do not tell a hold from a capture)', provider: 'epoint');
        }
        /** @var Money $amount */
        $amount = $request['amount'];
        if (!in_array($amount->currency, self::CURRENCIES, true)) {
            throw new ManatError(ManatError::INVALID_REQUEST, "epoint: currency {$amount->currency} is not supported (AZN, USD, EUR, RUB)");
        }
        if (strlen($request['orderId']) > 255) {
            throw new ManatError(ManatError::INVALID_REQUEST, 'epoint: orderId is longer than 255 characters');
        }
        $payload = [
            'public_key' => $this->config['publicKey'],
            'amount' => $amount->toDecimalString(),
            'currency' => $amount->currency,
            'language' => $request['language'] ?? 'az',
            'order_id' => $request['orderId'],
        ];
        if (isset($request['description']) && $request['description'] !== '') {
            $payload['description'] = self::truncate($request['description'], 1000);
        }
        $payload['success_redirect_url'] = $request['returnUrl'];
        $payload['error_redirect_url'] = $request['returnUrl'];

        $response = $this->call('request', $payload);
        if (($response['status'] ?? null) !== 'success' || empty($response['redirect_url']) || empty($response['transaction'])) {
            throw $this->error('payment was not created', $response);
        }
        return new CreatePaymentResult((string) $response['transaction'], PaymentStatus::PENDING, NextAction::redirect((string) $response['redirect_url']), $response);
    }

    public function verifyCallback(array $callback): PaymentEvent
    {
        $body = $callback['body'] ?? null;
        $fields = is_string($body) ? Form::decode($body) : (is_array($body) ? $body : []);
        $data = is_string($fields['data'] ?? null) ? $fields['data'] : '';
        $signature = is_string($fields['signature'] ?? null) ? $fields['signature'] : '';
        // An empty data still has a valid signature (sha1(key + key)), so it is rejected outright.
        if ($data === '' || $signature === '') {
            throw new ManatError(ManatError::INVALID_SIGNATURE, 'epoint: callback has no data/signature (the browser redirect is not signed; use getStatus)', provider: 'epoint', raw: $body);
        }
        if (!hash_equals(self::signature($this->config['privateKey'], $data), $signature)) {
            throw new ManatError(ManatError::INVALID_SIGNATURE, 'epoint: callback signature does not match', provider: 'epoint', raw: $body);
        }
        $payload = self::decodeData($data);
        if (empty($payload['transaction'])) {
            throw new ManatError(ManatError::PROVIDER_ERROR, 'epoint: signed callback has no transaction', provider: 'epoint', raw: $payload);
        }
        $orderId = isset($payload['order_id']) && $payload['order_id'] !== '' ? (string) $payload['order_id'] : null;
        return new PaymentEvent((string) $payload['transaction'], self::mapStatus($payload['status'] ?? null), $payload, $orderId);
    }

    public function getStatus(array $input): PaymentState
    {
        $paymentId = $input['paymentId'];
        $response = $this->call('get-status', ['public_key' => $this->config['publicKey'], 'transaction' => $paymentId]);
        // server_error means Epoint could not check the payment with the bank; the outcome is unknown.
        if (($response['status'] ?? null) === 'server_error' || empty($response['transaction'])) {
            throw $this->error('status is not available', $response);
        }
        if ($response['transaction'] !== $paymentId) {
            throw $this->error("status is for transaction {$response['transaction']}, not {$paymentId}", $response);
        }
        return new PaymentState($paymentId, self::mapStatus($response['status'] ?? null), $response);
    }

    public function refund(array $request): RefundResult
    {
        $payload = [
            'public_key' => $this->config['publicKey'],
            'language' => 'az',
            'transaction' => $request['paymentId'],
        ];
        if (isset($request['amount'])) {
            $payload['amount'] = $request['amount']->toDecimalString();
        }
        $payload['currency'] = isset($request['amount']) ? $request['amount']->currency : 'AZN';

        $response = $this->call('reverse', $payload);
        if (($response['status'] ?? null) !== 'success') {
            throw $this->error('refund failed', $response);
        }
        $state = $this->getStatus(['paymentId' => $request['paymentId']]);
        // Epoint has no partial-refund status: a reversed payment still reported as "success" was partly refunded.
        $status = $state->status === PaymentStatus::PAID ? PaymentStatus::PARTIALLY_REFUNDED : $state->status;
        return new RefundResult($request['paymentId'], $status, ['refund' => $response, 'payment' => $state->raw]);
    }

    private function call(string $method, array $payload): array
    {
        $form = self::encode($this->config['privateKey'], Json::encode($payload));
        [$status, $body] = $this->http->json('POST', "{$this->baseUrl}/{$method}", [], null, $form);
        if (!is_array($body)) {
            throw new ManatError(ManatError::PROVIDER_ERROR, "epoint: unexpected response (HTTP {$status})", provider: 'epoint', raw: $body);
        }
        if ($status >= 400) {
            throw $this->error("HTTP {$status}", $body);
        }
        return $body;
    }

    private function error(string $what, array $response): ManatError
    {
        $message = isset($response['message']) ? (string) $response['message'] : null;
        $code = isset($response['code']) && $response['code'] !== '' ? (string) $response['code'] : null;
        return new ManatError(ManatError::PROVIDER_ERROR, 'epoint: ' . ($message ?? $what), provider: 'epoint', providerCode: $code, providerMessage: $message, raw: $response);
    }

    private static function decodeData(string $data): ?array
    {
        if (!preg_match('#^[A-Za-z0-9+/]+={0,2}$#', $data)) {
            throw new ManatError(ManatError::PROVIDER_ERROR, 'epoint: callback data is not base64', provider: 'epoint', raw: $data);
        }
        $value = Json::decode((string) base64_decode($data, true));
        if ($value === null) {
            throw new ManatError(ManatError::PROVIDER_ERROR, 'epoint: callback data is not JSON', provider: 'epoint', raw: $data);
        }
        return is_array($value) && !array_is_list($value) ? $value : null;
    }

    private static function truncate(string $value, int $max): string
    {
        preg_match_all('/./us', $value, $chars);
        return count($chars[0]) > $max ? implode('', array_slice($chars[0], 0, $max)) : $value;
    }
}
