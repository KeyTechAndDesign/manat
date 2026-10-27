package manat

import (
	"strings"
)

// pair is one urlencoded field; the order is kept.
type pair struct {
	k, v string
}

// formEncode encodes like JavaScript's URLSearchParams: alphanumerics and *-._ stay, space is +, the rest %XX of UTF-8.
func formEncode(fields []pair) string {
	var b strings.Builder
	for i, f := range fields {
		if i > 0 {
			b.WriteByte('&')
		}
		escape(&b, f.k)
		b.WriteByte('=')
		escape(&b, f.v)
	}
	return b.String()
}

func escape(b *strings.Builder, s string) {
	const hex = "0123456789ABCDEF"
	for i := 0; i < len(s); i++ {
		c := s[i]
		switch {
		case c >= 'a' && c <= 'z', c >= 'A' && c <= 'Z', c >= '0' && c <= '9', c == '*', c == '-', c == '.', c == '_':
			b.WriteByte(c)
		case c == ' ':
			b.WriteByte('+')
		default:
			b.WriteByte('%')
			b.WriteByte(hex[c>>4])
			b.WriteByte(hex[c&15])
		}
	}
}

// formDecode keeps the first value of a repeated key; malformed %-escapes stay as text.
func formDecode(body string) map[string]string {
	out := map[string]string{}
	for _, part := range strings.Split(body, "&") {
		if part == "" {
			continue
		}
		key, value, _ := strings.Cut(part, "=")
		key = unescape(key)
		if _, seen := out[key]; !seen {
			out[key] = unescape(value)
		}
	}
	return out
}

func unescape(s string) string {
	var b []byte
	for i := 0; i < len(s); i++ {
		c := s[i]
		switch {
		case c == '+':
			b = append(b, ' ')
		case c == '%' && i+2 < len(s) && isHex(s[i+1]) && isHex(s[i+2]):
			b = append(b, unhex(s[i+1])<<4|unhex(s[i+2]))
			i += 2
		default:
			b = append(b, c)
		}
	}
	return strings.ToValidUTF8(string(b), "�")
}

func isHex(c byte) bool {
	return c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F'
}

func unhex(c byte) byte {
	switch {
	case c >= 'a':
		return c - 'a' + 10
	case c >= 'A':
		return c - 'A' + 10
	default:
		return c - '0'
	}
}
