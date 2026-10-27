<?php

declare(strict_types=1);

namespace Manat;

/**
 * Every error the library throws. `$errorCode` is one of the constants below
 * (PHP's own Exception::getCode() is an integer, so the spec's `code` lives here).
 */
final class ManatError extends \RuntimeException
{
    public const INVALID_REQUEST = 'invalid_request';
    public const INVALID_SIGNATURE = 'invalid_signature';
    public const PROVIDER_ERROR = 'provider_error';
    public const NETWORK_ERROR = 'network_error';
    public const UNSUPPORTED_OPERATION = 'unsupported_operation';

    public function __construct(
        public readonly string $errorCode,
        string $message,
        public readonly ?string $provider = null,
        public readonly ?string $providerCode = null,
        public readonly ?string $providerMessage = null,
        public readonly mixed $raw = null,
        ?\Throwable $previous = null,
    ) {
        parent::__construct($message, 0, $previous);
    }

    public static function is(mixed $error, ?string $code = null): bool
    {
        return $error instanceof self && ($code === null || $error->errorCode === $code);
    }
}
