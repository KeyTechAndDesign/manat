<?php

declare(strict_types=1);

namespace Manat;

/**
 * Provider-neutral facade. Validates input once, then delegates to the adapter,
 * so every provider gets the same checks and the same errors.
 *
 *     $pay = new Manat(new Providers\KapitalBank(['username' => ..., 'password' => ...]));
 *     $result = $pay->createPayment(['orderId' => 'A-1001', 'amount' => Money::azn('25.50'), 'returnUrl' => 'https://shop.az/return']);
 *     header('Location: ' . $result->next->url);
 */
final class Manat
{
    public function __construct(public readonly PaymentProvider $provider)
    {
    }

    /** @return list<string> */
    public function capabilities(): array
    {
        return $this->provider->capabilities();
    }

    public function supports(string $capability): bool
    {
        return in_array($capability, $this->provider->capabilities(), true);
    }

    /**
     * @param array{orderId: string, amount: Money|array{amount: int, currency: string}, returnUrl: string, callbackUrl?: string,
     *     description?: string, language?: 'az'|'en'|'ru', intent?: 'purchase'|'authorize',
     *     customer?: array{email?: string, phone?: string}, metadata?: array<string, string>} $request
     */
    public function createPayment(array $request): CreatePaymentResult
    {
        if (!isset($request['orderId']) || !is_string($request['orderId']) || $request['orderId'] === '') {
            throw new ManatError(ManatError::INVALID_REQUEST, 'orderId is required');
        }
        $request['amount'] = Money::from($request['amount'] ?? null);
        self::assertUrl($request['returnUrl'] ?? null, 'returnUrl');
        if (array_key_exists('callbackUrl', $request) && $request['callbackUrl'] !== null) {
            self::assertUrl($request['callbackUrl'], 'callbackUrl');
        }
        return $this->provider->createPayment($request);
    }

    /** @param array{method: string, query?: array<string, mixed>, body?: array<string, mixed>|string, headers?: array<string, mixed>} $callback */
    public function verifyCallback(array $callback): PaymentEvent
    {
        if (!isset($callback['method']) || !is_string($callback['method'])) {
            throw new ManatError(ManatError::INVALID_REQUEST, 'callback method is required');
        }
        return $this->provider->verifyCallback($callback);
    }

    /** @param array{paymentId: string} $input */
    public function getStatus(array $input): PaymentState
    {
        self::requirePaymentId($input);
        return $this->call('getStatus', $input);
    }

    /** @param array{paymentId: string, providerRef?: string, amount?: Money|array{amount: int, currency: string}} $request no amount = full refund */
    public function refund(array $request): RefundResult
    {
        self::requirePaymentId($request);
        if (isset($request['amount'])) {
            $request['amount'] = Money::from($request['amount']);
        }
        return $this->call('refund', $request);
    }

    /** @param array{paymentId: string, providerRef?: string, amount?: Money|array{amount: int, currency: string}} $input */
    public function capture(array $input): PaymentState
    {
        self::requirePaymentId($input);
        if (isset($input['amount'])) {
            $input['amount'] = Money::from($input['amount']);
        }
        return $this->call('capture', $input);
    }

    /** @param array{paymentId: string, providerRef?: string} $input */
    public function cancel(array $input): PaymentState
    {
        self::requirePaymentId($input);
        return $this->call('cancel', $input);
    }

    private function call(string $operation, array $input): mixed
    {
        if (!$this->supports($operation) || !method_exists($this->provider, $operation)) {
            $name = $this->provider->name();
            throw new ManatError(ManatError::UNSUPPORTED_OPERATION, "{$name} does not support {$operation}", provider: $name);
        }
        return $this->provider->{$operation}($input);
    }

    private static function requirePaymentId(array $input): void
    {
        if (!isset($input['paymentId']) || !is_string($input['paymentId']) || $input['paymentId'] === '') {
            throw new ManatError(ManatError::INVALID_REQUEST, 'paymentId is required');
        }
    }

    private static function assertUrl(mixed $value, string $field): void
    {
        $parts = is_string($value) ? parse_url($value) : false;
        $scheme = is_array($parts) ? strtolower($parts['scheme'] ?? '') : '';
        if (!in_array($scheme, ['http', 'https'], true) || empty($parts['host'])) {
            throw new ManatError(ManatError::INVALID_REQUEST, "{$field} must be an absolute http(s) URL");
        }
    }
}
