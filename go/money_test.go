package manat

import "testing"

func TestMoneyFormatsMinorUnitsWithoutFloats(t *testing.T) {
	for amount, want := range map[int64]string{2550: "25.50", 5: "0.05", 100: "1.00", 123456789: "1234567.89"} {
		if got := (Money{Amount: amount, Currency: "AZN"}).DecimalString(); got != want {
			t.Errorf("%d → %s, want %s", amount, got, want)
		}
	}
}

func TestMoneyParsesDecimals(t *testing.T) {
	for text, want := range map[string]int64{"25.5": 2550, "25.50": 2550, "0.05": 5, "10": 1000} {
		got, err := AZN(text)
		if err != nil || got.Amount != want || got.Currency != "AZN" {
			t.Errorf("%q → %v %v, want %d", text, got, err, want)
		}
	}
}

func TestMoneyRejectsInexactAmounts(t *testing.T) {
	for _, bad := range []string{"25.555", "-1", "abc", "", "1e3"} {
		if _, err := AZN(bad); !IsError(err, InvalidRequest) {
			t.Errorf("%q: got %v, want invalid_request", bad, err)
		}
	}
	if _, err := ParseMoney("1", "XYZ"); !IsError(err, InvalidRequest) {
		t.Errorf("unknown currency accepted")
	}
}

func TestMoneyJSONNumber(t *testing.T) {
	for amount, want := range map[int64]string{2550: "25.5", 100: "1", 1000: "10"} {
		if got := string((Money{Amount: amount, Currency: "AZN"}).number()); got != want {
			t.Errorf("%d → %s, want %s", amount, got, want)
		}
	}
}

func TestNumericCurrency(t *testing.T) {
	if (Money{Currency: "AZN"}).NumericCurrency() != "944" || currencyFromNumeric("944") != "AZN" || currencyFromNumeric("000") != "" {
		t.Error("ISO 4217 numeric codes do not map both ways")
	}
}
