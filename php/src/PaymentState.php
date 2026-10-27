<?php

declare(strict_types=1);

namespace Manat;

/** The provider's current view of a payment (getStatus, capture, cancel). */
final class PaymentState
{
    public function __construct(
        public readonly string $paymentId,
        public readonly string $status,
        public readonly mixed $raw,
        public readonly ?Money $amount = null,
        public readonly ?Money $refundedAmount = null,
        public readonly ?string $providerRef = null,
    ) {
    }

    /** @return array<string, mixed> */
    public function toArray(): array
    {
        return array_filter([
            'paymentId' => $this->paymentId,
            'status' => $this->status,
            'amount' => $this->amount?->toArray(),
            'refundedAmount' => $this->refundedAmount?->toArray(),
            'providerRef' => $this->providerRef,
            'raw' => $this->raw,
        ], static fn ($value, $key) => $value !== null || $key === 'raw', ARRAY_FILTER_USE_BOTH);
    }
}
