package manat

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"math/big"
	"sort"
	"strconv"
	"strings"
	"unicode/utf8"
)

// jsonNumber is number text written as is.
type jsonNumber string

// kv is one entry of an ordered JSON object.
type kv struct {
	k string
	v any
}

// object is a JSON object that keeps its key order, so signed payloads match JSON.stringify in other languages.
type object []kv

// parseJSON decodes into map[string]any, []any, string, json.Number, bool and nil.
func parseJSON(text string) (any, error) {
	dec := json.NewDecoder(strings.NewReader(text))
	dec.UseNumber()
	var v any
	if err := dec.Decode(&v); err != nil {
		return nil, err
	}
	if _, err := dec.Token(); err != io.EOF {
		return nil, fmt.Errorf("unexpected text after JSON")
	}
	return v, nil
}

// stringify writes JSON like JavaScript's JSON.stringify: compact, no HTML escaping, "/" and non-ASCII as is.
func stringify(v any) string {
	var b bytes.Buffer
	writeJSON(&b, v)
	return b.String()
}

func writeJSON(b *bytes.Buffer, v any) {
	switch x := v.(type) {
	case nil:
		b.WriteString("null")
	case string:
		quote(b, x)
	case bool:
		b.WriteString(strconv.FormatBool(x))
	case int:
		b.WriteString(strconv.Itoa(x))
	case int64:
		b.WriteString(strconv.FormatInt(x, 10))
	case jsonNumber:
		b.WriteString(string(x))
	case json.Number:
		b.WriteString(trimNumber(string(x)))
	case object:
		b.WriteByte('{')
		for i, e := range x {
			if i > 0 {
				b.WriteByte(',')
			}
			quote(b, e.k)
			b.WriteByte(':')
			writeJSON(b, e.v)
		}
		b.WriteByte('}')
	case map[string]any:
		keys := make([]string, 0, len(x))
		for k := range x {
			keys = append(keys, k)
		}
		sort.Strings(keys)
		o := make(object, 0, len(keys))
		for _, k := range keys {
			o = append(o, kv{k, x[k]})
		}
		writeJSON(b, o)
	case map[string]string:
		m := make(map[string]any, len(x))
		for k, s := range x {
			m[k] = s
		}
		writeJSON(b, m)
	case []any:
		b.WriteByte('[')
		for i, e := range x {
			if i > 0 {
				b.WriteByte(',')
			}
			writeJSON(b, e)
		}
		b.WriteByte(']')
	default:
		panic(fmt.Sprintf("manat: cannot write %T as JSON", v))
	}
}

func quote(b *bytes.Buffer, s string) {
	b.WriteByte('"')
	for i := 0; i < len(s); {
		r, size := utf8.DecodeRuneInString(s[i:])
		switch {
		case r == '"':
			b.WriteString(`\"`)
		case r == '\\':
			b.WriteString(`\\`)
		case r == '\b':
			b.WriteString(`\b`)
		case r == '\f':
			b.WriteString(`\f`)
		case r == '\n':
			b.WriteString(`\n`)
		case r == '\r':
			b.WriteString(`\r`)
		case r == '\t':
			b.WriteString(`\t`)
		case r < 0x20:
			fmt.Fprintf(b, `\u%04x`, r)
		case r == utf8.RuneError && size == 1:
			// Invalid UTF-8 cannot come from JavaScript; write the byte as a lone code unit.
			fmt.Fprintf(b, `\u%04x`, s[i])
		default:
			b.WriteString(s[i : i+size])
		}
		i += size
	}
	b.WriteByte('"')
}

// trimNumber writes a JSON number the way JavaScript prints it for decimals: "1.50" → "1.5", "1.0" → "1", "1e2" → "100".
func trimNumber(text string) string {
	r, ok := new(big.Rat).SetString(text)
	if !ok {
		return text
	}
	if r.IsInt() {
		return r.Num().String()
	}
	s := r.FloatString(30)
	s = strings.TrimRight(s, "0")
	return strings.TrimSuffix(s, ".")
}
