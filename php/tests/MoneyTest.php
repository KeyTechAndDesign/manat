<?php

// No strict_types on purpose: most application code runs without it, and that is where PHP would coerce 25.5 to 25.

namespace Manat\Tests;

use Manat\ManatError;
use Manat\Money;
use PHPUnit\Framework\TestCase;

final class MoneyTest extends TestCase
{
    public function testParsesDecimalStringsAndWholeManats(): void
    {
        self::assertSame(2550, Money::azn('25.50')->amount);
        self::assertSame(2550, Money::azn('25.5')->amount);
        self::assertSame(2500, Money::azn(25)->amount);
    }

    public function testRejectsFloatsInsteadOfTruncatingThem(): void
    {
        foreach ([25.5, 25.0, 0.1 + 0.2] as $float) {
            try {
                Money::azn($float);
                self::fail("Money::azn({$float}) was accepted");
            } catch (ManatError $error) {
                self::assertSame(ManatError::INVALID_REQUEST, $error->errorCode);
            }
        }
    }
}
