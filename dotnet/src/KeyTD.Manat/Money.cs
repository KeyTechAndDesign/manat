using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace KeyTD.Manat
{
    /// <summary>An amount in minor units: 2550 AZN is 25.50 manat. There is no floating point anywhere.</summary>
    public sealed class Money : IEquatable<Money>
    {
        private static readonly Regex Decimal = new Regex(@"^(\d+)(?:\.(\d+))?$", RegexOptions.CultureInvariant);
        private const int Digits = 2;

        private Money(long amount, Currency currency)
        {
            if (amount < 0)
            {
                throw new ManatException(ManatErrorCode.InvalidRequest, $"amount must not be negative, got {amount}");
            }
            Amount = amount;
            Currency = currency;
        }

        /// <summary>Minor units: 2550 for 25.50.</summary>
        public long Amount { get; }

        /// <summary>The currency.</summary>
        public Currency Currency { get; }

        /// <summary><c>Money.Of(2550, Currency.AZN)</c> is 25.50 AZN.</summary>
        public static Money Of(long minorUnits, Currency currency) => new Money(minorUnits, currency);

        /// <summary><c>Money.Azn("25.50")</c> is 2550 AZN.</summary>
        public static Money Azn(string value) => FromDecimal(value, Currency.AZN);

        /// <summary>"25.5" / "25.50" / "25" → 2550. Rejects more decimals than the currency has.</summary>
        public static Money FromDecimal(string? value, Currency currency)
        {
            var text = value?.Trim() ?? "";
            var match = Decimal.Match(text);
            if (!match.Success || match.Groups[2].Value.Length > Digits)
            {
                throw new ManatException(ManatErrorCode.InvalidRequest, $"\"{text}\" is not a valid {currency} amount");
            }
            var units = match.Groups[1].Value + match.Groups[2].Value.PadRight(Digits, '0');
            if (!long.TryParse(units, NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
            {
                throw new ManatException(ManatErrorCode.InvalidRequest, $"\"{text}\" is too large");
            }
            return new Money(amount, currency);
        }

        /// <summary>25.5m → 2550, as a JSON number would read (trailing zeros do not count as decimals).</summary>
        public static Money FromDecimal(decimal value, Currency currency) =>
            FromDecimal(value.ToString("0.############################", CultureInfo.InvariantCulture), currency);

        /// <summary>2550 → "25.50".</summary>
        public string ToDecimalString()
        {
            var units = Amount.ToString(CultureInfo.InvariantCulture).PadLeft(Digits + 1, '0');
            return units.Substring(0, units.Length - Digits) + "." + units.Substring(units.Length - Digits);
        }

        /// <summary>2550 → 25.5 and 100 → 1, for JSON APIs that take amounts as numbers.</summary>
        public decimal ToNumber() => Amount / 100m;

        /// <summary><c>{amount: 2550, currency: "AZN"}</c>, the spec's shape.</summary>
        public IReadOnlyDictionary<string, object?> ToDictionary() =>
            new Dictionary<string, object?> { ["amount"] = Amount, ["currency"] = Currency.ToString() };

        /// <inheritdoc />
        public bool Equals(Money? other) => other is not null && other.Amount == Amount && other.Currency == Currency;

        /// <inheritdoc />
        public override bool Equals(object? obj) => Equals(obj as Money);

        /// <inheritdoc />
        public override int GetHashCode() => Amount.GetHashCode() * 31 + (int)Currency;

        /// <summary>"25.50 AZN".</summary>
        public override string ToString() => ToDecimalString() + " " + Currency;
    }
}
