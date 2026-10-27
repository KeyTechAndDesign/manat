package com.keytd.manat;

import org.junit.jupiter.api.Test;

import java.math.BigDecimal;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;

class MoneyTest {
    @Test
    void formatsMinorUnitsWithoutFloatMaths() {
        assertEquals("25.50", Money.of(2550, Currency.AZN).toDecimalString());
        assertEquals("0.05", Money.of(5, Currency.AZN).toDecimalString());
        assertEquals("1.00", Money.of(100, Currency.USD).toDecimalString());
        assertEquals("1234567.89", Money.of(123456789, Currency.AZN).toDecimalString());
    }

    @Test
    void parsesDecimalsIntoMinorUnits() {
        assertEquals(Money.of(2550, Currency.AZN), Money.azn("25.5"));
        assertEquals(Money.of(2550, Currency.AZN), Money.fromDecimal(new BigDecimal("25.50"), Currency.AZN));
        assertEquals(Money.of(5, Currency.AZN), Money.azn("0.05"));
        assertEquals(Money.of(1000, Currency.EUR), Money.fromDecimal("10", Currency.EUR));
        assertEquals(Money.of(500, Currency.AZN), Money.fromDecimal(new BigDecimal("5.000"), Currency.AZN));
    }

    @Test
    void rejectsAmountsThatCannotBeRepresentedExactly() {
        for (String bad : new String[]{"25.555", "-1", "abc", "", "1e3", null}) {
            ManatError error = assertThrows(ManatError.class, () -> Money.azn(bad), String.valueOf(bad));
            assertEquals(ManatError.Code.INVALID_REQUEST, error.code());
        }
        assertThrows(ManatError.class, () -> Money.of(-5, Currency.AZN));
    }

    @Test
    void writesJsonNumbersWithoutTrailingZeros() {
        assertEquals(new BigDecimal("25.5"), Money.of(2550, Currency.AZN).toNumber());
        assertEquals("1", Money.of(100, Currency.AZN).toNumber().toPlainString());
        assertEquals("10", Money.of(1000, Currency.AZN).toNumber().toPlainString());
    }

    @Test
    void mapsIsoNumericCodesBothWays() {
        assertEquals("944", Currency.AZN.numericCode());
        assertEquals(Currency.AZN, Currency.fromNumeric("944"));
        assertNull(Currency.fromNumeric("000"));
    }
}
