package manat

import (
	"fmt"
	"regexp"
	"strconv"
	"strings"
)

// Money is an amount in minor units: 2550 AZN is 25.50 manat. There is no floating point anywhere.
type Money struct {
	Amount   int64
	Currency string
}

var numericCodes = map[string]string{"AZN": "944", "USD": "840", "EUR": "978", "RUB": "643", "TRY": "949", "GBP": "826"}

var decimalPattern = regexp.MustCompile(`^(\d+)(?:\.(\d+))?$`)

// AZN parses "25.50" into 2550 AZN.
func AZN(value string) (Money, error) { return ParseMoney(value, "AZN") }

// ParseMoney parses "25.5" / "25.50" / "25" into minor units. It rejects more decimals than the currency has.
func ParseMoney(value, currency string) (Money, error) {
	if _, ok := numericCodes[currency]; !ok {
		return Money{}, invalid(fmt.Sprintf("currency %q is not supported", currency))
	}
	text := strings.TrimSpace(value)
	m := decimalPattern.FindStringSubmatch(text)
	if m == nil || len(m[2]) > 2 {
		return Money{}, invalid(fmt.Sprintf("%q is not a valid %s amount", text, currency))
	}
	amount, err := strconv.ParseInt(m[1]+(m[2] + "00")[:2], 10, 64)
	if err != nil {
		return Money{}, invalid(fmt.Sprintf("%q is too large", text))
	}
	return Money{Amount: amount, Currency: currency}, nil
}

// DecimalString formats 2550 as "25.50".
func (m Money) DecimalString() string {
	units := strconv.FormatInt(m.Amount, 10)
	for len(units) < 3 {
		units = "0" + units
	}
	return units[:len(units)-2] + "." + units[len(units)-2:]
}

// String is "25.50 AZN".
func (m Money) String() string { return m.DecimalString() + " " + m.Currency }

// NumericCurrency is the ISO 4217 numeric code ("944" for AZN), which several bank APIs use instead of letters.
func (m Money) NumericCurrency() string { return numericCodes[m.Currency] }

// ToMap is the spec's shape: {amount: 2550, currency: "AZN"}.
func (m Money) ToMap() map[string]any {
	return map[string]any{"amount": m.Amount, "currency": m.Currency}
}

// number is 2550 → 25.5 and 100 → 1, for JSON APIs that take amounts as numbers.
func (m Money) number() jsonNumber {
	return jsonNumber(trimNumber(m.DecimalString()))
}

func currencyFromNumeric(code string) string {
	for letters, numeric := range numericCodes {
		if numeric == code {
			return letters
		}
	}
	return ""
}

func supportedCurrency(code string) bool {
	_, ok := numericCodes[code]
	return ok
}

func (m Money) valid(field string) error {
	if !supportedCurrency(m.Currency) {
		return invalid(fmt.Sprintf("%s.currency %q is not supported", field, m.Currency))
	}
	if m.Amount < 1 {
		return invalid(fmt.Sprintf("%s.amount must be a positive integer in minor units (2550 = 25.50), got %d", field, m.Amount))
	}
	return nil
}
