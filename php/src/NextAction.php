<?php

declare(strict_types=1);

namespace Manat;

/** Where to send the customer after createPayment: a redirect (GET), an auto-submitted form, or nothing. */
final class NextAction
{
    /** @param array<string, string> $fields */
    private function __construct(
        public readonly string $type,
        public readonly ?string $url = null,
        public readonly ?string $method = null,
        public readonly array $fields = [],
    ) {
    }

    public static function redirect(string $url): self
    {
        return new self('redirect', $url);
    }

    /** @param array<string, string> $fields */
    public static function form(string $url, array $fields): self
    {
        return new self('form', $url, 'POST', $fields);
    }

    public static function none(): self
    {
        return new self('none');
    }

    /** @return array<string, mixed> */
    public function toArray(): array
    {
        return match ($this->type) {
            'redirect' => ['type' => 'redirect', 'url' => $this->url],
            'form' => ['type' => 'form', 'url' => $this->url, 'method' => 'POST', 'fields' => $this->fields],
            default => ['type' => $this->type],
        };
    }
}
