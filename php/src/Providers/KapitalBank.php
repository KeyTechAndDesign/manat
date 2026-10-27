<?php

declare(strict_types=1);

namespace Manat\Providers;

use Manat\Callback;
use Manat\CreatePaymentResult;
use Manat\Http\Form;
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
 * Kapital Bank (TXPG REST API).
 * The redirect back (?ID=…&STATUS=…) is unsigned, so verifyCallback re-reads the order.
 */
final class KapitalBank implements PaymentProvider
{
    private const BASE_URLS = [
        'test' => 'https://txpgtst.kapitalbank.az/api',
        'production' => 'https://e-commerce.kapitalbank.az/api',
    ];

    private readonly HttpClient $http;
    private readonly string $baseUrl;
    private readonly string $authHeader;

    /**
     * @param array{username: string, password: string, environment?: 'test'|'production', baseUrl?: string} $config
     * @param array{transport?: \Manat\Http\Transport, timeout?: float} $options
     */
    public function __construct(array $config, array $options = [])
    {
        if (empty($config['username']) || empty($config['password'])) {
            throw new ManatError(ManatError::INVALID_REQUEST, 'kapitalbank: username and password are required');
        }
        $this->baseUrl = rtrim($config['baseUrl'] ?? self::BASE_URLS[$config['environment'] ?? 'production'], '/');
        $this->authHeader = 'Basic ' . base64_encode($config['username'] . ':' . $config['password']);
        $this->http = new HttpClient('kapitalbank', $options);
    }

    public function name(): string
    {
        return 'kapitalbank';
    }

    public function capabilities(): array
    {
        return ['createPayment', 'verifyCallback', 'getStatus', 'refund', 'capture', 'cancel'];
    }

    /** Maps an order's status. */
    public static function mapStatus(array $order): string
    {
        switch ($order['status'] ?? null) {
            case 'Preparing':
                return PaymentStatus::PENDING;
            case 'Authorized':
                return PaymentStatus::AUTHORIZED;
            case 'PartPaid':
            case 'FullyPaid':
            case 'Funded':
            case 'Closed':
                $refunded = (float) ($order['clearedRefundAmount'] ?? 0);
                if ($refunded > 0) {
                    $charged = isset($order['clearedChargeAmount']) ? (float) $order['clearedChargeAmount'] : INF;
                    return $refunded >= $charged ? PaymentStatus::REFUNDED : PaymentStatus::PARTIALLY_REFUNDED;
                }
                return PaymentStatus::PAID;
            case 'Refunded':
                return PaymentStatus::REFUNDED;
            case 'Cancelled':
            case 'Voided':
            case 'Expired':
                return PaymentStatus::CANCELLED;
            case 'Rejected':
            case 'Refused':
            case 'Declined':
                return PaymentStatus::FAILED;
            default:
                $status = var_export($order['status'] ?? null, true);
                throw new ManatError(ManatError::PROVIDER_ERROR, "kapitalbank: unknown order status {$status}", provider: 'kapitalbank', raw: $order);
        }
    }

    public function createPayment(array $request): CreatePaymentResult
    {
        /** @var Money $amount */
        $amount = $request['amount'];
        $order = [
            'typeRid' => ($request['intent'] ?? 'purchase') === 'authorize' ? 'Order_DMS' : 'Order_SMS',
            'amount' => $amount->toDecimalString(),
            'currency' => $amount->currency,
            'language' => $request['language'] ?? 'az',
            'description' => $request['description'] ?? $request['orderId'],
            'hppRedirectUrl' => $request['returnUrl'],
        ];
        $data = $this->call('POST', '/order', ['order' => $order]);
        $created = $data['order'] ?? null;
        if (!is_array($created) || empty($created['id']) || empty($created['hppUrl']) || empty($created['password'])) {
            throw new ManatError(ManatError::PROVIDER_ERROR, 'kapitalbank: order response is missing id, hppUrl or password', provider: 'kapitalbank', raw: $data);
        }
        $url = self::withQuery((string) $created['hppUrl'], ['id' => (string) $created['id'], 'password' => (string) $created['password']]);
        return new CreatePaymentResult(
            (string) $created['id'],
            isset($created['status']) ? self::mapStatus($created) : PaymentStatus::PENDING,
            NextAction::redirect($url),
            $data,
        );
    }

    public function verifyCallback(array $callback): PaymentEvent
    {
        $id = Callback::query($callback, 'ID') ?? Callback::query($callback, 'id') ?? Callback::body($callback, 'ID');
        if ($id === null || !preg_match('/^\d+$/', $id)) {
            throw new ManatError(ManatError::INVALID_SIGNATURE, 'kapitalbank: callback has no numeric ID', provider: 'kapitalbank', raw: $callback);
        }
        // The redirect is unsigned; the API is the source of truth.
        return PaymentEvent::fromState($this->getStatus(['paymentId' => $id]));
    }

    public function getStatus(array $input): PaymentState
    {
        return $this->toState($this->fetchOrder($input['paymentId']));
    }

    public function refund(array $request): RefundResult
    {
        $paymentId = $request['paymentId'];
        $amount = $request['amount'] ?? $this->remainingRefundable($paymentId);
        $data = $this->exec($paymentId, ['phase' => 'Single', 'amount' => $amount->toDecimalString(), 'type' => 'Refund']);
        $state = $this->getStatus(['paymentId' => $paymentId]);
        $actionId = $data['tran']['match']['tranActionId'] ?? null;
        return new RefundResult($paymentId, $state->status, ['exec' => $data, 'order' => $state->raw], is_string($actionId) ? $actionId : null);
    }

    public function capture(array $input): PaymentState
    {
        $paymentId = $input['paymentId'];
        $amount = $input['amount'] ?? $this->getStatus(['paymentId' => $paymentId])->amount;
        if ($amount === null) {
            throw new ManatError(ManatError::PROVIDER_ERROR, "kapitalbank: order {$paymentId} has no amount to capture", provider: 'kapitalbank');
        }
        $this->exec($paymentId, ['phase' => 'Clearing', 'amount' => $amount->toDecimalString()]);
        return $this->getStatus(['paymentId' => $paymentId]);
    }

    public function cancel(array $input): PaymentState
    {
        $paymentId = $input['paymentId'];
        $current = $this->getStatus(['paymentId' => $paymentId]);
        // Reversal of a pre-auth hold uses phase Auth; a same-day purchase uses phase Single.
        $phase = $current->status === PaymentStatus::AUTHORIZED ? 'Auth' : 'Single';
        $this->exec($paymentId, ['phase' => $phase, 'voidKind' => 'Full']);
        return $this->getStatus(['paymentId' => $paymentId]);
    }

    private function remainingRefundable(string $paymentId): Money
    {
        $order = $this->fetchOrder($paymentId);
        $currency = self::currency($order['currency'] ?? null, $paymentId);
        $charged = Money::fromDecimal($order['clearedChargeAmount'] ?? $order['amount'] ?? 0, $currency)->amount;
        $refunded = !empty($order['clearedRefundAmount']) ? Money::fromDecimal($order['clearedRefundAmount'], $currency)->amount : 0;
        $remaining = $charged - $refunded;
        if ($remaining <= 0) {
            throw new ManatError(ManatError::INVALID_REQUEST, "kapitalbank: order {$paymentId} has nothing left to refund", provider: 'kapitalbank', raw: $order);
        }
        return new Money($remaining, $currency);
    }

    private function fetchOrder(string $paymentId): array
    {
        self::assertOrderId($paymentId);
        $data = $this->call('GET', "/order/{$paymentId}?tranDetailLevel=2&tokenDetailLevel=2&orderDetailLevel=2");
        if (!isset($data['order']) || !is_array($data['order'])) {
            throw new ManatError(ManatError::PROVIDER_ERROR, "kapitalbank: no order in response for {$paymentId}", provider: 'kapitalbank', raw: $data);
        }
        $order = $data['order'];
        $order['id'] ??= $paymentId;
        return $order;
    }

    private function toState(array $order): PaymentState
    {
        $id = (string) $order['id'];
        $amount = null;
        $refunded = null;
        if (isset($order['amount'], $order['currency'])) {
            $amount = Money::fromDecimal($order['amount'], self::currency($order['currency'], $id));
        }
        if (!empty($order['clearedRefundAmount']) && isset($order['currency'])) {
            $refunded = Money::fromDecimal($order['clearedRefundAmount'], self::currency($order['currency'], $id));
        }
        return new PaymentState($id, self::mapStatus($order), $order, $amount, $refunded);
    }

    private function exec(string $paymentId, array $tran): array
    {
        self::assertOrderId($paymentId);
        return $this->call('POST', "/order/{$paymentId}/exec-tran", ['tran' => $tran]);
    }

    private function call(string $method, string $path, ?array $body = null): array
    {
        [$status, $data] = $this->http->json($method, $this->baseUrl . $path, ['authorization' => $this->authHeader], $body);
        if ($status >= 400 || (is_array($data) && !empty($data['errorCode']))) {
            $code = is_array($data) && isset($data['errorCode']) ? (string) $data['errorCode'] : null;
            $message = is_array($data) && isset($data['errorDescription']) ? (string) $data['errorDescription'] : null;
            throw new ManatError(ManatError::PROVIDER_ERROR, 'kapitalbank: ' . ($message ?? "HTTP {$status}"), provider: 'kapitalbank', providerCode: $code, providerMessage: $message, raw: $data);
        }
        return is_array($data) ? $data : [];
    }

    private static function assertOrderId(string $paymentId): void
    {
        if (!preg_match('/^\d+$/', $paymentId)) {
            throw new ManatError(ManatError::INVALID_REQUEST, "kapitalbank: paymentId must be the numeric order id, got \"{$paymentId}\"");
        }
    }

    private static function currency(mixed $code, string $paymentId): string
    {
        if (is_string($code) && in_array($code, Money::CURRENCIES, true)) {
            return $code;
        }
        $shown = is_scalar($code) ? (string) $code : gettype($code);
        throw new ManatError(ManatError::PROVIDER_ERROR, "kapitalbank: unexpected currency \"{$shown}\" on order {$paymentId}", provider: 'kapitalbank');
    }

    /** Sets query parameters on a URL, replacing existing ones with the same name (like URL.searchParams.set). */
    private static function withQuery(string $url, array $params): string
    {
        $fragment = '';
        if (($hash = strpos($url, '#')) !== false) {
            $fragment = substr($url, $hash);
            $url = substr($url, 0, $hash);
        }
        [$base, $query] = array_pad(explode('?', $url, 2), 2, '');
        if (preg_match('#^[a-z][a-z0-9+.-]*://[^/]+$#i', $base)) {
            $base .= '/';
        }
        $existing = $query === '' ? [] : Form::decode($query);
        return $base . '?' . Form::encode(array_merge($existing, $params)) . $fragment;
    }
}
