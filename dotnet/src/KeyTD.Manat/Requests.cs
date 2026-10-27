using System.Collections.Generic;

namespace KeyTD.Manat
{
    /// <summary>
    /// Input for <see cref="ManatClient.CreatePaymentAsync"/>.
    /// <code>
    /// new CreatePaymentRequest { OrderId = "A-1001", Amount = Money.Azn("25.50"), ReturnUrl = "https://shop.az/payment/return" }
    /// </code>
    /// </summary>
    public sealed class CreatePaymentRequest
    {
        /// <summary>The merchant's own id, unique per payment attempt.</summary>
        public string OrderId { get; init; } = "";

        /// <summary>What to charge.</summary>
        public Money? Amount { get; init; }

        /// <summary>Where the customer lands after paying.</summary>
        public string ReturnUrl { get; init; } = "";

        /// <summary>Server-to-server notification URL, for providers that take one per payment.</summary>
        public string? CallbackUrl { get; init; }

        /// <summary>Shown to the customer where the provider supports it.</summary>
        public string? Description { get; init; }

        /// <summary>Payment page language; adapters default to Azerbaijani.</summary>
        public Language? Language { get; init; }

        /// <summary><see cref="KeyTD.Manat.Intent.Authorize"/> holds the amount for a later capture.</summary>
        public Intent Intent { get; init; } = Intent.Purchase;

        /// <summary>Customer e-mail, where the provider takes it.</summary>
        public string? CustomerEmail { get; init; }

        /// <summary>Customer phone, where the provider takes it.</summary>
        public string? CustomerPhone { get; init; }

        /// <summary>Passed through to providers that keep metadata with the order.</summary>
        public IReadOnlyDictionary<string, string>? Metadata { get; init; }
    }

    /// <summary>Input for <see cref="ManatClient.RefundAsync"/>. Without an amount the whole payment is refunded.</summary>
    public sealed class RefundRequest
    {
        /// <summary>The payment to refund.</summary>
        public string PaymentId { get; init; } = "";

        /// <summary>Null for a full refund.</summary>
        public Money? Amount { get; init; }

        /// <summary>From <see cref="PaymentEvent.ProviderRef"/>, for providers that need it (Azericard).</summary>
        public string? ProviderRef { get; init; }
    }

    /// <summary>Input for <see cref="ManatClient.CaptureAsync"/> and <see cref="ManatClient.CancelAsync"/>.</summary>
    public sealed class PaymentAction
    {
        /// <summary>The payment to act on.</summary>
        public string PaymentId { get; init; } = "";

        /// <summary>Capture less than was held; null for the whole amount.</summary>
        public Money? Amount { get; init; }

        /// <summary>From <see cref="PaymentEvent.ProviderRef"/>, for providers that need it (Azericard).</summary>
        public string? ProviderRef { get; init; }
    }
}
