<?php

declare(strict_types=1);

namespace Manat;

/** The statuses every adapter maps its provider's codes onto. */
final class PaymentStatus
{
    public const PENDING = 'pending';
    public const AUTHORIZED = 'authorized';
    public const PAID = 'paid';
    public const FAILED = 'failed';
    public const CANCELLED = 'cancelled';
    public const REFUNDED = 'refunded';
    public const PARTIALLY_REFUNDED = 'partially_refunded';

    private function __construct()
    {
    }
}
