<?php

declare(strict_types=1);

namespace Manat;

/**
 * What every provider adapter implements. getStatus, refund, capture and cancel are optional:
 * an adapter has them as methods when its capabilities() lists them.
 *
 * Requests reach adapters through Manat, which has already validated them and turned
 * every amount into Money.
 */
interface PaymentProvider
{
    public function name(): string;

    /** @return list<string> e.g. ['createPayment', 'verifyCallback', 'getStatus', 'refund'] */
    public function capabilities(): array;

    /** @param array<string, mixed> $request */
    public function createPayment(array $request): CreatePaymentResult;

    /** @param array{method: string, query?: array<string, mixed>, body?: array<string, mixed>|string, headers?: array<string, mixed>} $callback */
    public function verifyCallback(array $callback): PaymentEvent;
}
