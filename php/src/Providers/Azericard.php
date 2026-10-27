<?php

declare(strict_types=1);

namespace Manat\Providers;

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
 * Azericard e-commerce gateway (CGI form-post interface, RSA-signed).
 * Docs: https://developer.azericard.com/en
 *
 * - The customer's browser POSTs a signed form to the gateway (`next->type === 'form'`).
 * - Every message carries P_SIGN: RSA-SHA256 (PKCS#1 v1.5, hex) over a "MAC source"
 *   where each field value is prefixed with its byte length and empty values become "-".
 *   Field order differs per message type and the docs note it can differ per terminal,
 *   so every order can be overridden with `macFields`.
 * - The result is POSTed back to BACKREF and signed with the gateway (MPI) key over
 *   AMOUNT, TERMINAL, APPROVAL, RRN, INT_REF. ORDER and ACTION are NOT covered by that
 *   signature, so a payment only counts as approved when the signed APPROVAL code is
 *   present, and merchants should reject a providerRef (RRN + INT_REF) they have already seen.
 * - Completion (21) and reversals (22 online, 24 offline) are server-to-server form POSTs
 *   that answer with a bare ACTION code ("0" = approved).
 * - The gateway delivers the result to BACKREF server-to-server (checked on the test gateway:
 *   nothing is posted through the browser), so BACKREF must be reachable from the internet.
 * - Status (TRTYPE 90) answers in JSON. Its `signature` does not match any documented field
 *   order with the published test key, so getStatus relies on TLS to the gateway instead.
 */
final class Azericard implements PaymentProvider
{
    private const GATEWAY_URLS = [
        'test' => 'https://testmpi.3dsecure.az/cgi-bin/cgi_link',
        // Not in the public docs (they only publish the test URL); used by existing integrations.
        'production' => 'https://mpi.3dsecure.az/cgi-bin/cgi_link',
    ];

    /** MAC field orders from the docs (sections 2.2.1–2.2.4 and 4.3). */
    public const MAC_FIELDS = [
        'purchase' => ['AMOUNT', 'CURRENCY', 'TERMINAL', 'TRTYPE', 'TIMESTAMP', 'NONCE', 'MERCH_URL'],
        'completion' => ['AMOUNT', 'CURRENCY', 'TERMINAL', 'TRTYPE', 'ORDER', 'RRN', 'INT_REF'],
        'reversal' => ['AMOUNT', 'CURRENCY', 'TERMINAL', 'TRTYPE', 'ORDER', 'RRN', 'INT_REF'],
        'callback' => ['AMOUNT', 'TERMINAL', 'APPROVAL', 'RRN', 'INT_REF'],
        'status' => ['ORDER', 'TERMINAL', 'TRTYPE', 'TIMESTAMP', 'NONCE'],
    ];

    private const ACTION_MESSAGES = [
        '0' => 'Approved',
        '1' => 'Duplicate transaction',
        '2' => 'Transaction declined',
        '3' => 'Transaction processing error',
        '6' => 'Repeat of a declined transaction',
        '7' => 'Repeat of a transaction with an authentication error',
        '8' => 'Repeat of a transaction that terminated without a response',
    ];

    private const DAY_SECONDS = 86400;

    private readonly HttpClient $http;
    private readonly string $url;
    private readonly \OpenSSLAsymmetricKey $privateKey;
    private readonly ?\OpenSSLAsymmetricKey $gatewayKey;
    /** @var array<string, list<string>> */
    private readonly array $macFields;
    /** @var \Closure(): \DateTimeInterface */
    private readonly \Closure $clock;
    /** @var \Closure(): string */
    private readonly \Closure $nonce;

    /**
     * @param array{terminal: string, privateKey: string, gatewayPublicKey?: string, merchantName: string, merchantUrl: string,
     *     environment?: 'test'|'production', gatewayUrl?: string, email?: string, country?: string, merchantGmt?: string,
     *     macFields?: array<string, list<string>>, clock?: callable(): \DateTimeInterface, nonce?: callable(): string} $config
     *     privateKey and gatewayPublicKey are PEM text; clock and nonce are testing hooks.
     * @param array{transport?: \Manat\Http\Transport, timeout?: float} $options
     */
    public function __construct(private readonly array $config, array $options = [])
    {
        if (empty($config['terminal'])) {
            throw new ManatError(ManatError::INVALID_REQUEST, 'azericard: terminal is required');
        }
        if (empty($config['merchantName']) || empty($config['merchantUrl'])) {
            throw new ManatError(ManatError::INVALID_REQUEST, 'azericard: merchantName and merchantUrl are required');
        }
        if (empty($config['privateKey'])) {
            throw new ManatError(ManatError::INVALID_REQUEST, 'azericard: privateKey (PEM) is required');
        }
        $privateKey = openssl_pkey_get_private($config['privateKey']);
        if ($privateKey === false) {
            throw new ManatError(ManatError::INVALID_REQUEST, 'azericard: privateKey is not a valid PEM key');
        }
        $details = openssl_pkey_get_details($privateKey);
        if (($details['type'] ?? null) !== OPENSSL_KEYTYPE_RSA || ($details['bits'] ?? 0) < 2048) {
            throw new ManatError(ManatError::INVALID_REQUEST, 'azericard: privateKey must be an RSA key of at least 2048 bits');
        }
        $this->privateKey = $privateKey;
        $gatewayKey = null;
        if (!empty($config['gatewayPublicKey'])) {
            $gatewayKey = openssl_pkey_get_public($config['gatewayPublicKey']);
            if ($gatewayKey === false) {
                throw new ManatError(ManatError::INVALID_REQUEST, 'azericard: gatewayPublicKey is not a valid PEM key');
            }
        }
        $this->gatewayKey = $gatewayKey;
        $this->url = $config['gatewayUrl'] ?? self::GATEWAY_URLS[$config['environment'] ?? 'production'];
        $this->macFields = ($config['macFields'] ?? []) + self::MAC_FIELDS;
        $this->clock = \Closure::fromCallable($config['clock'] ?? static fn (): \DateTimeInterface => new \DateTimeImmutable('now', new \DateTimeZone('UTC')));
        $this->nonce = \Closure::fromCallable($config['nonce'] ?? static fn (): string => bin2hex(random_bytes(16)));
        $this->http = new HttpClient('azericard', $options);
    }

    public function name(): string
    {
        return 'azericard';
    }

    public function capabilities(): array
    {
        return ['createPayment', 'verifyCallback', 'getStatus', 'refund', 'capture', 'cancel'];
    }

    /**
     * Length-prefixed MAC source: "11.48" → "511.48", empty → "-". Lengths are in bytes.
     *
     * @param array<string, string|null> $fields
     * @param list<string> $order
     */
    public static function macSource(array $fields, array $order): string
    {
        $out = '';
        foreach ($order as $name) {
            $value = (string) ($fields[$name] ?? '');
            $out .= $value === '' ? '-' : strlen($value) . $value;
        }
        return $out;
    }

    /** GMT timestamp YYYYMMDDHHMMSS. */
    public static function timestamp(\DateTimeInterface $date): string
    {
        return gmdate('YmdHis', $date->getTimestamp());
    }

    public function createPayment(array $request): CreatePaymentResult
    {
        $orderId = $request['orderId'];
        if (!preg_match('/^\d{6,32}$/', $orderId)) {
            throw new ManatError(ManatError::INVALID_REQUEST, "azericard: orderId must be 6–32 digits (got \"{$orderId}\"); its last 6 digits must be unique per terminal per day");
        }
        /** @var Money $amount */
        $amount = $request['amount'];
        $fields = self::compact([
            'AMOUNT' => $amount->toDecimalString(),
            'CURRENCY' => $amount->currency,
            'ORDER' => $orderId,
            'DESC' => self::truncate($request['description'] ?? "Order {$orderId}", 50),
            'MERCH_NAME' => self::truncate($this->config['merchantName'], 50),
            'MERCH_URL' => $this->config['merchantUrl'],
            'TERMINAL' => $this->config['terminal'],
            'EMAIL' => $this->config['email'] ?? null,
            'TRTYPE' => ($request['intent'] ?? 'purchase') === 'authorize' ? '0' : '1',
            'COUNTRY' => $this->config['country'] ?? null,
            'MERCH_GMT' => $this->config['merchantGmt'] ?? null,
            'TIMESTAMP' => self::timestamp(($this->clock)()),
            'NONCE' => ($this->nonce)(),
            'BACKREF' => $request['callbackUrl'] ?? $request['returnUrl'],
            'LANG' => $request['language'] ?? 'az',
        ]);
        $fields['P_SIGN'] = $this->sign($fields, 'purchase');
        return new CreatePaymentResult($orderId, PaymentStatus::PENDING, NextAction::form($this->url, $fields), ['fields' => $fields]);
    }

    public function verifyCallback(array $callback): PaymentEvent
    {
        if ($this->gatewayKey === null) {
            throw new ManatError(ManatError::INVALID_REQUEST, 'azericard: gatewayPublicKey is required to verify callbacks; never accept them unverified');
        }
        $fields = self::readFields($callback);
        $signature = $fields['P_SIGN'] ?? '';
        if ($signature === '' || !preg_match('/^[0-9a-fA-F]+$/', $signature) || strlen($signature) % 2 !== 0) {
            throw new ManatError(ManatError::INVALID_SIGNATURE, 'azericard: callback has no valid P_SIGN', provider: 'azericard', raw: $fields);
        }
        $source = self::macSource($fields, $this->macFields['callback']);
        if (openssl_verify($source, (string) hex2bin($signature), $this->gatewayKey, OPENSSL_ALGO_SHA256) !== 1) {
            throw new ManatError(ManatError::INVALID_SIGNATURE, 'azericard: callback P_SIGN does not match', provider: 'azericard', raw: $fields);
        }
        $terminal = $fields['TERMINAL'] ?? '';
        if ($terminal !== $this->config['terminal']) {
            throw new ManatError(ManatError::INVALID_SIGNATURE, "azericard: callback is for terminal {$terminal}, not {$this->config['terminal']}", provider: 'azericard', raw: $fields);
        }
        if (empty($fields['ORDER'])) {
            throw new ManatError(ManatError::INVALID_SIGNATURE, 'azericard: callback has no ORDER', provider: 'azericard', raw: $fields);
        }

        $amount = self::parseAmount($fields['AMOUNT'] ?? null, $fields['CURRENCY'] ?? null);
        // ACTION is unsigned; the signed APPROVAL code is what proves an approval.
        $approved = ($fields['ACTION'] ?? null) === '0' && !empty($fields['APPROVAL']);
        $providerRef = null;
        if ($approved && !empty($fields['RRN']) && !empty($fields['INT_REF']) && $amount !== null && !empty($fields['TIMESTAMP'])) {
            $providerRef = self::encodeRef($fields['RRN'], $fields['INT_REF'], $amount, $fields['TIMESTAMP'], $fields['TRTYPE'] ?? '1');
        }
        $status = $approved ? self::statusForTrtype($fields['TRTYPE'] ?? null) : PaymentStatus::FAILED;
        return new PaymentEvent($fields['ORDER'], $status, $fields, $fields['ORDER'], $amount, $providerRef);
    }

    /**
     * TRTYPE 90. Works for 24 hours after the payment. The original TRTYPE is not known from the
     * paymentId alone, so a purchase (1) is asked for first and a pre-auth (0) on "context mismatch".
     * The answer only describes the transaction type asked about, so completion (21) and the
     * reversals (22 online, 24 offline) are queried too. Only the latest offline reversal is
     * visible this way, so refundedAmount can be lower than the truth after several 24s.
     */
    public function getStatus(array $input): PaymentState
    {
        $paymentId = $input['paymentId'];
        if (!preg_match('/^\d{6,32}$/', $paymentId)) {
            throw new ManatError(ManatError::INVALID_REQUEST, "azericard: paymentId must be the numeric ORDER, got \"{$paymentId}\"");
        }
        $trtype = '1';
        $data = $this->queryStatus($paymentId, $trtype);
        if (($data['responseCode'] ?? null) === '-24') {
            $trtype = '0';
            $data = $this->queryStatus($paymentId, $trtype);
        }
        $status = self::mapStatusResponse($data, $trtype);
        $amount = self::parseAmount($data['amount'] ?? null, $data['currency'] ?? null);
        $raw = [$trtype => $data];
        if (($status !== PaymentStatus::PAID && $status !== PaymentStatus::AUTHORIZED) || $amount === null) {
            return new PaymentState($paymentId, $status, $raw, $amount);
        }

        $related = [];
        $approved = [];
        foreach ($trtype === '0' ? ['21', '22', '24'] : ['22', '24'] as $type) {
            $related[$type] = $this->queryStatus($paymentId, $type);
            if (($related[$type]['actionCode'] ?? null) === '0' && ($related[$type]['responseCode'] ?? null) === '00') {
                $approved[$type] = $related[$type];
            }
        }
        $raw += $related;
        if ($status === PaymentStatus::AUTHORIZED && isset($approved['21'])) {
            $status = PaymentStatus::PAID;
        }
        $refunded = self::approvedAmount($approved, '22', $amount->currency) + self::approvedAmount($approved, '24', $amount->currency);
        $refundedAmount = null;
        if ($refunded > 0) {
            $refundedAmount = new Money($refunded, $amount->currency);
            $status = $status === PaymentStatus::AUTHORIZED
                ? PaymentStatus::CANCELLED
                : ($refunded >= $amount->amount ? PaymentStatus::REFUNDED : PaymentStatus::PARTIALLY_REFUNDED);
        }
        $providerRef = null;
        $paidAt = isset($data['tranDate']) ? self::bakuTimeToGmt($data['tranDate']) : ($data['timestamp'] ?? null);
        if (!empty($data['rrn']) && !empty($data['intRef']) && !empty($paidAt)) {
            $providerRef = self::encodeRef($data['rrn'], $data['intRef'], $amount, $paidAt, isset($approved['21']) ? '21' : $trtype);
        }
        return new PaymentState($paymentId, $status, $raw, $amount, $refundedAmount, $providerRef);
    }

    public function capture(array $input): PaymentState
    {
        $ref = self::requireRef($input['providerRef'] ?? null);
        $amount = $input['amount'] ?? $ref['amount'];
        $this->send('completion', '21', $input['paymentId'], $ref, $amount);
        $providerRef = self::encodeRef($ref['rrn'], $ref['intRef'], $amount, $ref['timestamp'], '21');
        return new PaymentState($input['paymentId'], PaymentStatus::PAID, null, $amount, null, $providerRef);
    }

    public function cancel(array $input): PaymentState
    {
        $ref = self::requireRef($input['providerRef'] ?? null);
        $this->send('reversal', '22', $input['paymentId'], $ref, $ref['amount']);
        return new PaymentState($input['paymentId'], PaymentStatus::CANCELLED, null, $ref['amount']);
    }

    public function refund(array $request): RefundResult
    {
        $ref = self::requireRef($request['providerRef'] ?? null);
        /** @var Money $amount */
        $amount = $request['amount'] ?? $ref['amount'];
        if ($amount->currency !== $ref['amount']->currency || $amount->amount > $ref['amount']->amount) {
            throw new ManatError(ManatError::INVALID_REQUEST, 'azericard: refund amount exceeds the paid amount or uses another currency');
        }
        // Online reversal (22) is only possible before settlement and only once per transaction
        // (a second 22 fails with ACTION 3 on the gateway); everything else is an offline reversal (24).
        $paidAt = self::parseTimestamp($ref['timestamp']);
        $trtype = '24';
        if ($paidAt !== null && ($this->clock)()->getTimestamp() - $paidAt < self::DAY_SECONDS) {
            $online = $this->queryStatus($request['paymentId'], '22');
            $trtype = ($online['actionCode'] ?? null) === '0' && ($online['responseCode'] ?? null) === '00' ? '24' : '22';
        }
        $action = $this->send('reversal', $trtype, $request['paymentId'], $ref, $amount);
        $status = $amount->amount < $ref['amount']->amount ? PaymentStatus::PARTIALLY_REFUNDED : PaymentStatus::REFUNDED;
        return new RefundResult($request['paymentId'], $status, ['trtype' => $trtype, 'action' => $action]);
    }

    /** @return array<string, mixed> */
    private function queryStatus(string $order, string $tranTrtype): array
    {
        $fields = [
            'TRAN_TRTYPE' => $tranTrtype,
            'ORDER' => $order,
            'TERMINAL' => $this->config['terminal'],
            'TRTYPE' => '90',
            'TIMESTAMP' => self::timestamp(($this->clock)()),
            'NONCE' => ($this->nonce)(),
        ];
        $fields['P_SIGN'] = $this->sign($fields, 'status');
        [$status, $data] = $this->http->json('POST', $this->url, [], null, $fields);
        if ($status >= 400 || !is_array($data)) {
            throw new ManatError(ManatError::PROVIDER_ERROR, "azericard: status request failed (HTTP {$status})", provider: 'azericard', raw: $data);
        }
        return $data;
    }

    /** @param array{rrn: string, intRef: string, amount: Money, timestamp: string, trtype: string} $ref */
    private function send(string $message, string $trtype, string $order, array $ref, Money $amount): string
    {
        $fields = [
            'AMOUNT' => $amount->toDecimalString(),
            'CURRENCY' => $amount->currency,
            'ORDER' => $order,
            'RRN' => $ref['rrn'],
            'INT_REF' => $ref['intRef'],
            'TERMINAL' => $this->config['terminal'],
            'TRTYPE' => $trtype,
            'TIMESTAMP' => self::timestamp(($this->clock)()),
            'NONCE' => ($this->nonce)(),
        ];
        $fields['P_SIGN'] = $this->sign($fields, $message);
        $response = $this->http->send('POST', $this->url, [], null, $fields);
        $action = trim($response->body);
        if ($response->status >= 400) {
            throw new ManatError(ManatError::PROVIDER_ERROR, "azericard: HTTP {$response->status}", provider: 'azericard', raw: $response->body);
        }
        if ($action !== '0') {
            $text = self::ACTION_MESSAGES[$action] ?? 'Unexpected response "' . substr($action, 0, 100) . '"';
            throw new ManatError(ManatError::PROVIDER_ERROR, "azericard: {$text}", provider: 'azericard', providerCode: substr($action, 0, 20), providerMessage: $text, raw: $response->body);
        }
        return $action;
    }

    /** @param array<string, string> $fields */
    private function sign(array $fields, string $message): string
    {
        $source = self::macSource($fields, $this->macFields[$message]);
        if (!openssl_sign($source, $signature, $this->privateKey, OPENSSL_ALGO_SHA256)) {
            throw new ManatError(ManatError::INVALID_REQUEST, 'azericard: could not sign with privateKey');
        }
        return bin2hex($signature);
    }

    /** @param array<string, mixed> $data TRTYPE 90 JSON, field names as returned by the test gateway */
    public static function mapStatusResponse(array $data, string $tranTrtype): string
    {
        if (($data['actionCode'] ?? null) === '0' && ($data['responseCode'] ?? null) === '00') {
            return $tranTrtype === '0' ? PaymentStatus::AUTHORIZED : PaymentStatus::PAID;
        }
        if (($data['responseCode'] ?? null) === '-40') {
            return PaymentStatus::PENDING; // "Client side transaction form in progress"
        }
        return PaymentStatus::FAILED;
    }

    private static function approvedAmount(array $approved, string $type, string $currency): int
    {
        if (!isset($approved[$type])) {
            return 0;
        }
        $money = self::parseAmount($approved[$type]['amount'] ?? null, $approved[$type]['currency'] ?? $currency);
        return $money !== null && $money->currency === $currency ? $money->amount : 0;
    }

    /** "2026.10.26 19:06:40" in Baku time (UTC+4, no DST) → "20261026150640" GMT. */
    private static function bakuTimeToGmt(string $value): ?string
    {
        if (!preg_match('/^(\d{4})\.(\d{2})\.(\d{2}) (\d{2}):(\d{2}):(\d{2})$/', $value, $m)) {
            return null;
        }
        return gmdate('YmdHis', gmmktime((int) $m[4] - 4, (int) $m[5], (int) $m[6], (int) $m[2], (int) $m[3], (int) $m[1]));
    }

    private static function statusForTrtype(?string $trtype): string
    {
        return match ($trtype) {
            '0' => PaymentStatus::AUTHORIZED,
            '22', '24' => PaymentStatus::REFUNDED,
            default => PaymentStatus::PAID,
        };
    }

    private static function encodeRef(string $rrn, string $intRef, Money $amount, string $timestamp, string $trtype): string
    {
        return implode('|', ['az1', $rrn, $intRef, $amount->toDecimalString(), $amount->currency, $timestamp, $trtype]);
    }

    /** @return array{rrn: string, intRef: string, amount: Money, timestamp: string, trtype: string} */
    private static function requireRef(?string $value): array
    {
        $parts = $value === null ? [] : explode('|', $value);
        if (count($parts) !== 7 || $parts[0] !== 'az1' || $parts[1] === '' || $parts[2] === '') {
            throw new ManatError(ManatError::INVALID_REQUEST, 'azericard: providerRef from the payment callback is required (store event.providerRef with the order)');
        }
        [, $rrn, $intRef, $amount, $currency, $timestamp, $trtype] = $parts;
        return ['rrn' => $rrn, 'intRef' => $intRef, 'amount' => Money::fromDecimal($amount, self::currency($currency)), 'timestamp' => $timestamp, 'trtype' => $trtype];
    }

    private static function parseAmount(?string $amount, ?string $currency): ?Money
    {
        if ($amount === null || $amount === '' || $currency === null || $currency === '') {
            return null;
        }
        $alpha = preg_match('/^\d{3}$/', $currency) ? Money::currencyFromNumeric($currency) : $currency;
        return $alpha === null ? null : Money::fromDecimal($amount, self::currency($alpha));
    }

    private static function currency(string $code): string
    {
        if (in_array($code, Money::CURRENCIES, true)) {
            return $code;
        }
        throw new ManatError(ManatError::PROVIDER_ERROR, "azericard: unsupported currency \"{$code}\"", provider: 'azericard');
    }

    private static function parseTimestamp(string $value): ?int
    {
        if (!preg_match('/^(\d{4})(\d{2})(\d{2})(\d{2})(\d{2})(\d{2})$/', $value, $m)) {
            return null;
        }
        return gmmktime((int) $m[4], (int) $m[5], (int) $m[6], (int) $m[2], (int) $m[3], (int) $m[1]);
    }

    /** @return array<string, string> upper-case keys, trimmed values; query first, body after */
    private static function readFields(array $callback): array
    {
        $out = [];
        $add = static function (string $key, mixed $value) use (&$out): void {
            if (is_array($value)) {
                $value = $value[0] ?? null;
            }
            if (is_string($value)) {
                $out[strtoupper($key)] = trim($value);
            }
        };
        foreach ($callback['query'] ?? [] as $key => $value) {
            $add((string) $key, $value);
        }
        $body = $callback['body'] ?? [];
        foreach (is_string($body) ? Form::decode($body) : $body as $key => $value) {
            $add((string) $key, $value);
        }
        return $out;
    }

    /**
     * @param array<string, string|null> $fields
     * @return array<string, string>
     */
    private static function compact(array $fields): array
    {
        return array_filter($fields, static fn ($value) => $value !== null && $value !== '');
    }

    private static function truncate(string $value, int $max): string
    {
        preg_match_all('/./us', $value, $chars);
        return count($chars[0]) > $max ? implode('', array_slice($chars[0], 0, $max)) : $value;
    }
}
