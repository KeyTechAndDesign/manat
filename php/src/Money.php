<?php

declare(strict_types=1);

namespace Manat;

/** Integer amount in minor units: 2550 = 25.50 AZN. Floats are never accepted. */
final class Money
{
    public const CURRENCIES = ['AZN', 'USD', 'EUR', 'RUB', 'TRY', 'GBP'];

    /** ISO 4217 numeric codes, which several bank APIs use instead of letters. */
    private const NUMERIC_CODES = ['AZN' => '944', 'USD' => '840', 'EUR' => '978', 'RUB' => '643', 'TRY' => '949', 'GBP' => '826'];

    public function __construct(
        public readonly int $amount,
        public readonly string $currency,
    ) {
        if (!in_array($currency, self::CURRENCIES, true)) {
            throw new ManatError(ManatError::INVALID_REQUEST, "currency \"{$currency}\" is not supported");
        }
        if ($amount < 0) {
            throw new ManatError(ManatError::INVALID_REQUEST, "amount must not be negative, got {$amount}");
        }
    }

    /** Money::azn('25.50') → 2550 AZN. */
    public static function azn(string|int $value): self
    {
        return self::fromDecimal($value, 'AZN');
    }

    /** "25.5" / "25.50" / 25 → 2550. Rejects more decimals than the currency has. */
    public static function fromDecimal(string|int|float $value, string $currency): self
    {
        $text = is_float($value) ? Json::number($value) : trim((string) $value);
        if (!preg_match('/^(\d+)(?:\.(\d+))?$/', $text, $m) || strlen($m[2] ?? '') > 2) {
            throw new ManatError(ManatError::INVALID_REQUEST, "\"{$text}\" is not a valid {$currency} amount");
        }
        return new self((int) ($m[1] . str_pad($m[2] ?? '', 2, '0')), $currency);
    }

    /**
     * Accepts a Money or ['amount' => int, 'currency' => string] and checks it is a positive amount.
     *
     * @param Money|array{amount?: mixed, currency?: mixed}|mixed $value
     */
    public static function from(mixed $value, string $field = 'amount'): self
    {
        if (is_array($value)) {
            $amount = $value['amount'] ?? null;
            $currency = $value['currency'] ?? null;
            if (!is_int($amount)) {
                $shown = is_scalar($amount) ? var_export($amount, true) : gettype($amount);
                throw new ManatError(ManatError::INVALID_REQUEST, "{$field}.amount must be a positive integer in minor units (2550 = 25.50), got {$shown}");
            }
            if (!is_string($currency) || !in_array($currency, self::CURRENCIES, true)) {
                throw new ManatError(ManatError::INVALID_REQUEST, "{$field}.currency is not supported");
            }
            $value = new self($amount, $currency);
        }
        if (!$value instanceof self) {
            throw new ManatError(ManatError::INVALID_REQUEST, "{$field} is required");
        }
        if ($value->amount < 1) {
            throw new ManatError(ManatError::INVALID_REQUEST, "{$field}.amount must be a positive integer in minor units (2550 = 25.50), got {$value->amount}");
        }
        return $value;
    }

    /** 2550 AZN → "25.50" (no floats involved). */
    public function toDecimalString(): string
    {
        $units = str_pad((string) $this->amount, 3, '0', STR_PAD_LEFT);
        return substr($units, 0, -2) . '.' . substr($units, -2);
    }

    /** 2550 → 25.5 and 100 → 1, for JSON APIs that take amounts as numbers. */
    public function toNumber(): int|float
    {
        return $this->amount % 100 === 0 ? intdiv($this->amount, 100) : (float) $this->toDecimalString();
    }

    public function numericCurrency(): string
    {
        return self::NUMERIC_CODES[$this->currency];
    }

    public static function currencyFromNumeric(string $code): ?string
    {
        $found = array_search($code, self::NUMERIC_CODES, true);
        return $found === false ? null : $found;
    }

    /** @return array{amount: int, currency: string} */
    public function toArray(): array
    {
        return ['amount' => $this->amount, 'currency' => $this->currency];
    }
}
