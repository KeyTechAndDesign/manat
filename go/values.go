package manat

import (
	"encoding/json"
	"math/big"
	"strconv"
	"strings"
	"unicode/utf8"
)

// Helpers for reading parsed JSON (map[string]any, []any, string, json.Number, bool).

func asMap(v any) map[string]any {
	m, _ := v.(map[string]any)
	return m
}

func asList(v any) []any {
	l, _ := v.([]any)
	return l
}

// get walks object keys; nil when a step is missing.
func get(v any, path ...string) any {
	for _, key := range path {
		m := asMap(v)
		if m == nil {
			return nil
		}
		v = m[key]
	}
	return v
}

// str is the value when it is a string.
func str(v any) (string, bool) {
	s, ok := v.(string)
	return s, ok
}

// text is JavaScript's String(value) for strings, numbers and booleans; "" for anything else.
func text(v any) string {
	switch x := v.(type) {
	case string:
		return x
	case json.Number:
		return trimNumber(string(x))
	case int64:
		return strconv.FormatInt(x, 10)
	case int:
		return strconv.Itoa(x)
	case bool:
		return strconv.FormatBool(x)
	}
	return ""
}

// truthy is JavaScript truthiness: not nil, false, "" or 0.
func truthy(v any) bool {
	switch x := v.(type) {
	case nil:
		return false
	case bool:
		return x
	case string:
		return x != ""
	case json.Number:
		r, ok := new(big.Rat).SetString(string(x))
		return !ok || r.Sign() != 0
	case int64:
		return x != 0
	case int:
		return x != 0
	}
	return true
}

// integer is a JSON number with no fraction.
func integer(v any) (int64, bool) {
	switch x := v.(type) {
	case json.Number:
		r, ok := new(big.Rat).SetString(string(x))
		if !ok || !r.IsInt() || !r.Num().IsInt64() {
			return 0, false
		}
		return r.Num().Int64(), true
	case int64:
		return x, true
	case int:
		return int64(x), true
	}
	return 0, false
}

// decimal is JavaScript's Number(value) for numbers and numeric strings ("" is 0).
func decimal(v any) (*big.Rat, bool) {
	switch x := v.(type) {
	case json.Number:
		return new(big.Rat).SetString(string(x))
	case int64:
		return new(big.Rat).SetInt64(x), true
	case string:
		t := strings.TrimSpace(x)
		if t == "" {
			return new(big.Rat), true
		}
		return new(big.Rat).SetString(t)
	}
	return nil, false
}

// moneyOf reads a decimal amount from JSON (25.5 or "25.50").
func moneyOf(v any, currency string) (Money, error) {
	return ParseMoney(text(v), currency)
}

// firstString is a string, or the first string of a list.
func firstString(v any) string {
	switch x := v.(type) {
	case string:
		return x
	case []string:
		if len(x) > 0 {
			return x[0]
		}
	case []any:
		if len(x) > 0 {
			s, _ := x[0].(string)
			return s
		}
	}
	return ""
}

// truncate cuts to max code points.
func truncate(s string, max int) string {
	if utf8.RuneCountInString(s) <= max {
		return s
	}
	n := 0
	for i := range s {
		if n == max {
			return s[:i]
		}
		n++
	}
	return s
}
