<?php

declare(strict_types=1);

namespace Manat;

final class CreatePaymentResult
{
    public function __construct(
        /** Provider's id for this payment (or the orderId when the provider has none). */
        public readonly string $paymentId,
        public readonly string $status,
        public readonly NextAction $next,
        /** Provider response, untouched. */
        public readonly mixed $raw,
    ) {
    }

    /** @return array<string, mixed> */
    public function toArray(): array
    {
        return ['paymentId' => $this->paymentId, 'status' => $this->status, 'next' => $this->next->toArray(), 'raw' => $this->raw];
    }
}
