namespace KeyTD.Manat
{
    /// <summary>Currencies the adapters understand. Each has two minor-unit digits.</summary>
    public enum Currency
    {
#pragma warning disable CS1591 // ISO 4217 codes need no comment
        AZN,
        USD,
        EUR,
        RUB,
        TRY,
        GBP,
#pragma warning restore CS1591
    }

    /// <summary>The statuses every adapter maps its provider's codes onto.</summary>
    public enum PaymentStatus
    {
        /// <summary>Not finished yet.</summary>
        Pending,
        /// <summary>Funds are held (a pre-authorisation) and wait for capture or cancel.</summary>
        Authorized,
        /// <summary>Paid.</summary>
        Paid,
        /// <summary>Declined or failed.</summary>
        Failed,
        /// <summary>Cancelled, expired or reversed.</summary>
        Cancelled,
        /// <summary>Fully refunded.</summary>
        Refunded,
        /// <summary>Partly refunded.</summary>
        PartiallyRefunded,
    }

    /// <summary>The operations an adapter can offer; <see cref="ManatClient.Supports"/> tells which ones it does.</summary>
    public enum Capability
    {
        /// <summary>createPayment</summary>
        CreatePayment,
        /// <summary>verifyCallback</summary>
        VerifyCallback,
        /// <summary>getStatus</summary>
        GetStatus,
        /// <summary>refund</summary>
        Refund,
        /// <summary>capture</summary>
        Capture,
        /// <summary>cancel</summary>
        Cancel,
    }

    /// <summary>Payment page language.</summary>
    public enum Language
    {
        /// <summary>Azerbaijani.</summary>
        Az,
        /// <summary>English.</summary>
        En,
        /// <summary>Russian.</summary>
        Ru,
    }

    /// <summary>What CreatePayment asks for.</summary>
    public enum Intent
    {
        /// <summary>Charge the card now.</summary>
        Purchase,
        /// <summary>Hold the amount (pre-authorisation); finish with capture or release with cancel.</summary>
        Authorize,
    }

    /// <summary>Which of a provider's hosts to use, for providers with separate test and production hosts.</summary>
    public enum ProviderEnvironment
    {
        /// <summary>The live host (default).</summary>
        Production,
        /// <summary>The provider's test host.</summary>
        Test,
    }

    /// <summary>Spec names and codes for the enums.</summary>
    public static class EnumExtensions
    {
        /// <summary>The spec's name, such as <c>"partially_refunded"</c>.</summary>
        public static string ToValue(this PaymentStatus status) => status switch
        {
            PaymentStatus.Pending => "pending",
            PaymentStatus.Authorized => "authorized",
            PaymentStatus.Paid => "paid",
            PaymentStatus.Failed => "failed",
            PaymentStatus.Cancelled => "cancelled",
            PaymentStatus.Refunded => "refunded",
            _ => "partially_refunded",
        };

        /// <summary>The spec's name, such as <c>"getStatus"</c>.</summary>
        public static string ToValue(this Capability capability) => capability switch
        {
            Capability.CreatePayment => "createPayment",
            Capability.VerifyCallback => "verifyCallback",
            Capability.GetStatus => "getStatus",
            Capability.Refund => "refund",
            Capability.Capture => "capture",
            _ => "cancel",
        };

        /// <summary>"az", "en" or "ru".</summary>
        public static string ToCode(this Language language) => language switch
        {
            Language.En => "en",
            Language.Ru => "ru",
            _ => "az",
        };

        /// <summary>ISO 4217 numeric code ("944" for AZN), which several bank APIs use instead of letters.</summary>
        public static string NumericCode(this Currency currency) => currency switch
        {
            Currency.AZN => "944",
            Currency.USD => "840",
            Currency.EUR => "978",
            Currency.RUB => "643",
            Currency.TRY => "949",
            _ => "826",
        };
    }

    /// <summary>Looking currencies up by code.</summary>
    public static class Currencies
    {
        private static readonly Currency[] All = { Currency.AZN, Currency.USD, Currency.EUR, Currency.RUB, Currency.TRY, Currency.GBP };

        /// <summary>The currency with this letter code ("AZN"), or null.</summary>
        public static Currency? Find(string? code)
        {
            foreach (var currency in All)
            {
                if (currency.ToString() == code)
                {
                    return currency;
                }
            }
            return null;
        }

        /// <summary>The currency with this ISO 4217 numeric code ("944"), or null.</summary>
        public static Currency? FromNumeric(string? code)
        {
            foreach (var currency in All)
            {
                if (currency.NumericCode() == code)
                {
                    return currency;
                }
            }
            return null;
        }
    }
}
