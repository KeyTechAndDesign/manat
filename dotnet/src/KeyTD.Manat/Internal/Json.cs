using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KeyTD.Manat.Internal
{
    /// <summary>A JSON object that keeps its keys in order.</summary>
    internal sealed class JsonObject : IDictionary<string, object?>, IReadOnlyDictionary<string, object?>
    {
        private readonly Dictionary<string, object?> values = new Dictionary<string, object?>();
        private readonly List<string> order = new List<string>();

        public object? this[string key]
        {
            get => values[key];
            set
            {
                if (!values.ContainsKey(key))
                {
                    order.Add(key);
                }
                values[key] = value;
            }
        }

        public ICollection<string> Keys => order.ToArray();

        public ICollection<object?> Values => order.ConvertAll(key => values[key]);

        IEnumerable<string> IReadOnlyDictionary<string, object?>.Keys => order;

        IEnumerable<object?> IReadOnlyDictionary<string, object?>.Values => Values;

        public int Count => order.Count;

        public bool IsReadOnly => false;

        public void Add(string key, object? value)
        {
            values.Add(key, value);
            order.Add(key);
        }

        public void Add(KeyValuePair<string, object?> item) => Add(item.Key, item.Value);

        public void Clear()
        {
            values.Clear();
            order.Clear();
        }

        public bool Contains(KeyValuePair<string, object?> item) => values.TryGetValue(item.Key, out var value) && Equals(value, item.Value);

        public bool ContainsKey(string key) => values.ContainsKey(key);

        public void CopyTo(KeyValuePair<string, object?>[] array, int arrayIndex)
        {
            foreach (var pair in this)
            {
                array[arrayIndex++] = pair;
            }
        }

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            foreach (var key in order)
            {
                yield return new KeyValuePair<string, object?>(key, values[key]);
            }
        }

        public bool Remove(string key) => values.Remove(key) && order.Remove(key);

        public bool Remove(KeyValuePair<string, object?> item) => Contains(item) && Remove(item.Key);

        public bool TryGetValue(string key, out object? value) => values.TryGetValue(key, out value);

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Thrown for text that is not JSON.</summary>
    internal sealed class JsonParseException : FormatException
    {
        public JsonParseException(string message, int position)
            : base($"{message} at position {position}")
        {
        }
    }

    /// <summary>
    /// Minimal JSON reader and writer, so the library needs no package. Objects become <see cref="JsonObject"/>, arrays
    /// <c>List&lt;object?&gt;</c>, integers <c>long</c> (or <c>decimal</c> when too large), other numbers <c>decimal</c>.
    /// Output matches JavaScript's JSON.stringify: compact, <c>/</c> and non-ASCII unescaped, so signed payloads are
    /// byte-for-byte the same in every language.
    /// </summary>
    internal static class Json
    {
        private const int MaxDepth = 512;

        public static object? Parse(string text)
        {
            var reader = new Reader(text ?? "");
            reader.SkipWhitespace();
            var value = reader.Value(0);
            reader.SkipWhitespace();
            if (!reader.AtEnd)
            {
                throw reader.Error("unexpected text after JSON");
            }
            return value;
        }

        public static string Stringify(object? value)
        {
            var output = new StringBuilder();
            Write(output, value);
            return output.ToString();
        }

        private static void Write(StringBuilder output, object? value)
        {
            switch (value)
            {
                case null:
                    output.Append("null");
                    break;
                case string text:
                    Quote(output, text);
                    break;
                case bool flag:
                    output.Append(flag ? "true" : "false");
                    break;
                case decimal number:
                    output.Append(number.ToString("0.############################", CultureInfo.InvariantCulture));
                    break;
                case double number:
                    if (double.IsNaN(number) || double.IsInfinity(number))
                    {
                        output.Append("null");
                    }
                    else
                    {
                        output.Append(number.ToString("R", CultureInfo.InvariantCulture));
                    }
                    break;
                case float number:
                    Write(output, (double)number);
                    break;
                case long or int or short or byte or ulong or uint or ushort or sbyte:
                    output.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                    break;
                case IEnumerable<KeyValuePair<string, object?>> map:
                    WriteObject(output, map);
                    break;
                case IEnumerable<KeyValuePair<string, string>> strings:
                    WriteObject(output, Pairs(strings));
                    break;
                case IEnumerable items:
                    output.Append('[');
                    var first = true;
                    foreach (var item in items)
                    {
                        if (!first)
                        {
                            output.Append(',');
                        }
                        first = false;
                        Write(output, item);
                    }
                    output.Append(']');
                    break;
                default:
                    throw new ArgumentException($"cannot write {value.GetType().FullName} as JSON");
            }
        }

        private static IEnumerable<KeyValuePair<string, object?>> Pairs(IEnumerable<KeyValuePair<string, string>> strings)
        {
            foreach (var pair in strings)
            {
                yield return new KeyValuePair<string, object?>(pair.Key, pair.Value);
            }
        }

        private static void WriteObject(StringBuilder output, IEnumerable<KeyValuePair<string, object?>> map)
        {
            output.Append('{');
            var first = true;
            foreach (var pair in map)
            {
                if (!first)
                {
                    output.Append(',');
                }
                first = false;
                Quote(output, pair.Key);
                output.Append(':');
                Write(output, pair.Value);
            }
            output.Append('}');
        }

        private static void Quote(StringBuilder output, string text)
        {
            output.Append('"');
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                switch (c)
                {
                    case '"':
                        output.Append("\\\"");
                        break;
                    case '\\':
                        output.Append("\\\\");
                        break;
                    case '\b':
                        output.Append("\\b");
                        break;
                    case '\f':
                        output.Append("\\f");
                        break;
                    case '\n':
                        output.Append("\\n");
                        break;
                    case '\r':
                        output.Append("\\r");
                        break;
                    case '\t':
                        output.Append("\\t");
                        break;
                    default:
                        var loneSurrogate = char.IsHighSurrogate(c)
                            ? i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])
                            : char.IsLowSurrogate(c) && (i == 0 || !char.IsHighSurrogate(text[i - 1]));
                        if (c < 0x20 || loneSurrogate)
                        {
                            output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            output.Append(c);
                        }
                        break;
                }
            }
            output.Append('"');
        }

        private sealed class Reader
        {
            private readonly string text;
            private int pos;

            public Reader(string text)
            {
                this.text = text;
            }

            public bool AtEnd => pos >= text.Length;

            public JsonParseException Error(string message) => new JsonParseException(message, pos);

            public void SkipWhitespace()
            {
                while (pos < text.Length && (text[pos] == ' ' || text[pos] == '\t' || text[pos] == '\n' || text[pos] == '\r'))
                {
                    pos++;
                }
            }

            public object? Value(int depth)
            {
                if (depth > MaxDepth)
                {
                    throw Error("JSON nested too deeply");
                }
                if (AtEnd)
                {
                    throw Error("unexpected end of JSON");
                }
                var c = text[pos];
                switch (c)
                {
                    case '{':
                        return ParseObject(depth);
                    case '[':
                        return ParseArray(depth);
                    case '"':
                        return ParseString();
                    case 't':
                        return Literal("true", true);
                    case 'f':
                        return Literal("false", false);
                    case 'n':
                        return Literal("null", null);
                    default:
                        if (c == '-' || (c >= '0' && c <= '9'))
                        {
                            return ParseNumber();
                        }
                        throw Error($"unexpected character '{c}'");
                }
            }

            private object? Literal(string word, object? value)
            {
                if (string.CompareOrdinal(text, pos, word, 0, word.Length) != 0)
                {
                    throw Error("unexpected text");
                }
                pos += word.Length;
                return value;
            }

            private JsonObject ParseObject(int depth)
            {
                var output = new JsonObject();
                pos++;
                SkipWhitespace();
                if (!AtEnd && text[pos] == '}')
                {
                    pos++;
                    return output;
                }
                while (true)
                {
                    SkipWhitespace();
                    if (AtEnd || text[pos] != '"')
                    {
                        throw Error("expected a property name");
                    }
                    var key = ParseString();
                    SkipWhitespace();
                    Expect(':');
                    SkipWhitespace();
                    output[key] = Value(depth + 1);
                    SkipWhitespace();
                    if (AtEnd)
                    {
                        throw Error("unexpected end of JSON");
                    }
                    var c = text[pos++];
                    if (c == '}')
                    {
                        return output;
                    }
                    if (c != ',')
                    {
                        throw Error("expected ',' or '}'");
                    }
                }
            }

            private List<object?> ParseArray(int depth)
            {
                var output = new List<object?>();
                pos++;
                SkipWhitespace();
                if (!AtEnd && text[pos] == ']')
                {
                    pos++;
                    return output;
                }
                while (true)
                {
                    SkipWhitespace();
                    output.Add(Value(depth + 1));
                    SkipWhitespace();
                    if (AtEnd)
                    {
                        throw Error("unexpected end of JSON");
                    }
                    var c = text[pos++];
                    if (c == ']')
                    {
                        return output;
                    }
                    if (c != ',')
                    {
                        throw Error("expected ',' or ']'");
                    }
                }
            }

            private void Expect(char c)
            {
                if (AtEnd || text[pos] != c)
                {
                    throw Error($"expected '{c}'");
                }
                pos++;
            }

            private string ParseString()
            {
                pos++;
                var output = new StringBuilder();
                while (true)
                {
                    if (AtEnd)
                    {
                        throw Error("unterminated string");
                    }
                    var c = text[pos++];
                    if (c == '"')
                    {
                        return output.ToString();
                    }
                    if (c < 0x20)
                    {
                        throw Error("control character in string");
                    }
                    if (c != '\\')
                    {
                        output.Append(c);
                        continue;
                    }
                    if (AtEnd)
                    {
                        throw Error("unterminated escape");
                    }
                    var e = text[pos++];
                    switch (e)
                    {
                        case '"':
                        case '\\':
                        case '/':
                            output.Append(e);
                            break;
                        case 'b':
                            output.Append('\b');
                            break;
                        case 'f':
                            output.Append('\f');
                            break;
                        case 'n':
                            output.Append('\n');
                            break;
                        case 'r':
                            output.Append('\r');
                            break;
                        case 't':
                            output.Append('\t');
                            break;
                        case 'u':
                            if (pos + 4 > text.Length
                                || !int.TryParse(text.Substring(pos, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                            {
                                throw Error("bad unicode escape");
                            }
                            output.Append((char)code);
                            pos += 4;
                            break;
                        default:
                            throw Error($"bad escape '\\{e}'");
                    }
                }
            }

            private object ParseNumber()
            {
                var start = pos;
                if (text[pos] == '-')
                {
                    pos++;
                }
                if (AtEnd)
                {
                    throw Error("bad number");
                }
                if (text[pos] == '0')
                {
                    pos++;
                }
                else if (!Digits())
                {
                    throw Error("bad number");
                }
                var integer = true;
                if (!AtEnd && text[pos] == '.')
                {
                    integer = false;
                    pos++;
                    if (!Digits())
                    {
                        throw Error("bad number");
                    }
                }
                if (!AtEnd && (text[pos] == 'e' || text[pos] == 'E'))
                {
                    integer = false;
                    pos++;
                    if (!AtEnd && (text[pos] == '+' || text[pos] == '-'))
                    {
                        pos++;
                    }
                    if (!Digits())
                    {
                        throw Error("bad number");
                    }
                }
                var literal = text.Substring(start, pos - start);
                if (integer && long.TryParse(literal, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole))
                {
                    return whole;
                }
                if (decimal.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    return number;
                }
                return double.Parse(literal, NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            private bool Digits()
            {
                var start = pos;
                while (!AtEnd && text[pos] >= '0' && text[pos] <= '9')
                {
                    pos++;
                }
                return pos > start;
            }
        }
    }
}
