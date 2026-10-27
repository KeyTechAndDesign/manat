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
 * Yığım MAGNET payment gateway (api.pay.yigim.az).
 * Built from Yığım's MAGNET Merchant API specification (shared with merchants), checked against the
 * sandbox's replies and two community SDKs (The-Hasanov/yigim-magnet, paladium/yigim-gateway-go):
 *
 * - Every command is a GET with query parameters and the headers `X-Merchant`, `X-API-Key` and `X-Type: JSON`
 *   (without X-Type the gateway answers XML). Requests are not signed; the older SDKs' `X-Signature` predates the current API.
 * - Replies are HTTP 200 with `code` (0 = OK) and `message` at the top level. The specification wraps them in
 *   `response`, the sandbox does not; both shapes are read.
 * - The merchant's own `reference` (the orderId) is the payment id. Amounts are sent in qəpik with a numeric
 *   currency code; `payment/status` reports the amount in manat (100 qəpik reads "1").
 * - The webhook (`callback`) is a GET carrying only `reference` and is not signed, so verifyCallback re-reads the
 *   payment with `payment/status`. There is no per-payment return URL: the page the customer goes back to is part of
 *   the merchant's payment-page template, set up by Yığım, so `returnUrl` is not sent.
 * - `cancel` reverses a payment before the day's settlement (or releases a DMS hold); after settlement `refund` is
 *   the way back. Both take an optional amount.
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

    /** Authorization status codes (section 8.1). */
    public static function mapStatus(?string $status): string
    {
        return match ($status) {
            'S0', 'S2' => PaymentStatus::PENDING, // created, waiting for card data / in progress
            'S1' => PaymentStatus::AUTHORIZED, // pre-authorised DMS payment: charge or cancel within 30 days
            '00' => PaymentStatus::PAID,
            'S4' => PaymentStatus::CANCELLED, // reversed
            // S3 unknown error, S7 system malfunction, 01–08 declines, 09 issuer timeout (Yığım asks the merchant to cancel)
            'S3', 'S7', '01', '02', '03', '04', '05', '06', '07', '08', '09' => PaymentStatus::FAILED,
            default => throw new ManatError(ManatError::PROVIDER_ERROR, 'yigim: unknown payment status "' . ($status ?? 'null') . '"', provider: 'yigim'),
        };
    }

    public function createPayment(array $request): CreatePaymentResult
    {
        self::assertReference($request['orderId']);
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
        if (!empty($request['callbackUrl'])) {
            $params['callback'] = $request['callbackUrl'];
        }
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

        // A refund keeps status 00 and is listed under `refund`. Its entries have not been seen yet; each one's
        // `amount` is read in manat like the payment's own amount, and an entry without one counts as a full refund.
        $refunds = is_array($reply['refund'] ?? null) ? $reply['refund'] : [];
        $refundedAmount = null;
        if ($status === PaymentStatus::PAID && $refunds !== []) {
            $amounts = array_map(static fn ($entry) => is_array($entry) ? ($entry['amount'] ?? null) : null, $refunds);
            $readable = array_filter($amounts, static fn ($value) => is_string($value) || is_int($value) || is_float($value));
            if ($amount !== null && count($readable) === count($amounts)) {
                $refunded = 0;
                foreach ($amounts as $value) {
                    $refunded += Money::fromDecimal(is_string($value) ? $value : Json::number($value), $amount->currency)->amount;
                }
                $refundedAmount = new Money($refunded, $amount->currency);
                $status = $refunded >= $amount->amount ? PaymentStatus::REFUNDED : PaymentStatus::PARTIALLY_REFUNDED;
            } else {
                $status = PaymentStatus::REFUNDED;
            }
        }
        return new PaymentState($paymentId, $status, $reply, $amount, $refundedAmount);
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
        // The specification nests replies under `response`; the sandbox does not.
        $reply = is_array($data) && is_array($data['response'] ?? null) ? $data['response'] : $data;
        if (!is_array($reply) || !is_int($reply['code'] ?? null)) {
            throw new ManatError(ManatError::PROVIDER_ERROR, "yigim: unexpected response (HTTP {$status})", provider: 'yigim', raw: $data);
        }
        if ($status >= 400 || $reply['code'] !== self::OK) {
            throw $this->error("HTTP {$status}", $reply);
        }
        return $reply;
    }

    private function error(string $what, array $reply): ManatError
    {
        $code = $reply['code'] ?? null;
        $message = isset($reply['message']) && $reply['message'] !== '' ? (string) $reply['message'] : null;
        $fromProvider = is_int($code) && $code !== self::OK;
        return new ManatError(
            ManatError::PROVIDER_ERROR,
            'yigim: ' . ($fromProvider && $message !== null ? $message : $what),
            provider: 'yigim',
            providerCode: $fromProvider ? (string) $code : null,
            providerMessage: $fromProvider ? $message : null,
            raw: $reply,
        );
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
