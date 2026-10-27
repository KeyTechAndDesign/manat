<?php

declare(strict_types=1);

namespace Manat;

/** An authenticated provider callback or redirect. */
final class PaymentEvent
{
    public function __construct(
        public readonly string $paymentId,
        public readonly string $status,
        public readonly mixed $raw,
        public readonly ?string $orderId = null,
        public readonly ?Money $amount = null,
        /**
         * Opaque provider data needed for later operations (e.g. Azericard's RRN and INT_REF).
         * Store it with the order and pass it back to refund/capture/cancel unchanged.
         */
        public readonly ?string $providerRef = null,
    ) {
    }

    public static function fromState(PaymentState $state): self
    {
        return new self($state->paymentId, $state->status, $state->raw, amount: $state->amount);
    }

    /** @return array<string, mixed> */
    public function toArray(): array
    {
        return array_filter([
            'paymentId' => $this->paymentId,
            'orderId' => $this->orderId,
            'status' => $this->status,
            'amount' => $this->amount?->toArray(),
            'providerRef' => $this->providerRef,
            'raw' => $this->raw,
        ], static fn ($value, $key) => $value !== null || $key === 'raw', ARRAY_FILTER_USE_BOTH);
    }
}
