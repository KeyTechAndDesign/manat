using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace KeyTD.Manat.Internal
{
    /// <summary>Reading parsed JSON (dictionaries, lists, strings, long, decimal) without casts all over the adapters.</summary>
    internal static class Values
    {
        /// <summary><paramref name="value"/> as an object, or null when it is something else.</summary>
        public static IReadOnlyDictionary<string, object?>? Map(object? value) => value as IReadOnlyDictionary<string, object?>;

        /// <summary><paramref name="value"/> as a list, or null when it is something else.</summary>
        public static IReadOnlyList<object?>? List(object? value) => value as IReadOnlyList<object?>;

        /// <summary>Walks object keys: <c>Get(data, "order", "id")</c>. Null when any step is missing.</summary>
        public static object? Get(object? value, params string[] path)
        {
            var current = value;
            foreach (var key in path)
            {
                var map = Map(current);
                if (map == null || !map.TryGetValue(key, out current))
                {
                    return null;
                }
            }
            return current;
        }

        /// <summary>The value when it is a string, otherwise null.</summary>
        public static string? String(object? value) => value as string;

        /// <summary>A string, or the first string of a list or array; otherwise null.</summary>
        public static string? FirstString(object? value)
        {
            if (value is string text)
            {
                return text;
            }
            if (value is IEnumerable items and not IDictionary)
            {
                foreach (var item in items)
                {
                    return item as string;
                }
            }
            return null;
        }

        /// <summary>JavaScript's <c>String(value)</c> for strings, numbers and booleans; null for anything else.</summary>
        public static string? Text(object? value) => value switch
        {
            string text => text,
            decimal number => number.ToString("0.############################", CultureInfo.InvariantCulture),
            long or int => System.Convert.ToString(value, CultureInfo.InvariantCulture),
            double number => number.ToString("R", CultureInfo.InvariantCulture),
            bool flag => flag ? "true" : "false",
            _ => null,
        };

        /// <summary>JavaScript truthiness: not null, false, "", or 0.</summary>
        public static bool Truthy(object? value) => value switch
        {
            null => false,
            bool flag => flag,
            string text => text.Length > 0,
            long number => number != 0,
            int number => number != 0,
            decimal number => number != 0,
            double number => number != 0,
            _ => true,
        };

        /// <summary>A JSON integer as a long, or null for anything else (strings, decimals with a fraction).</summary>
        public static long? Integer(object? value) => value switch
        {
            long number => number,
            int number => number,
            decimal number when decimal.Truncate(number) == number && number >= long.MinValue && number <= long.MaxValue => (long)number,
            _ => null,
        };

        /// <summary>JavaScript's <c>Number(value)</c> for numbers and numeric strings; null when not a number.</summary>
        public static decimal? Decimal(object? value)
        {
            switch (value)
            {
                case decimal number:
                    return number;
                case long number:
                    return number;
                case int number:
                    return number;
                case string text:
                    var trimmed = text.Trim();
                    if (trimmed.Length == 0)
                    {
                        return 0;
                    }
                    return decimal.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : (decimal?)null;
                default:
                    return null;
            }
        }

        /// <summary>A decimal amount from JSON (number or string, "25.5" or 25.5) as Money.</summary>
        public static Money Money(object? value, Currency currency) => value switch
        {
            decimal number => KeyTD.Manat.Money.FromDecimal(number, currency),
            long number => KeyTD.Manat.Money.FromDecimal(number, currency),
            int number => KeyTD.Manat.Money.FromDecimal(number, currency),
            _ => KeyTD.Manat.Money.FromDecimal(Text(value) ?? "", currency),
        };

        /// <summary>Code points, so a cut never splits a character.</summary>
        public static string Truncate(string value, int maxCodePoints)
        {
            var count = 0;
            for (var i = 0; i < value.Length; i++)
            {
                if (count == maxCodePoints)
                {
                    return value.Substring(0, i);
                }
                if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    i++;
                }
                count++;
            }
            return value;
        }
    }
}
