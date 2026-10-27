<?php

declare(strict_types=1);

namespace Manat;

final class RefundResult
{
    public function __construct(
        public readonly string $paymentId,
        public readonly string $status,
        public readonly mixed $raw,
        public readonly ?string $refundId = null,
    ) {
    }

    /** @return array<string, mixed> */
    public function toArray(): array
    {
        return array_filter([
            'paymentId' => $this->paymentId,
            'refundId' => $this->refundId,
            'status' => $this->status,
            'raw' => $this->raw,
        ], static fn ($value, $key) => $value !== null || $key === 'raw', ARRAY_FILTER_USE_BOTH);
    }
}
