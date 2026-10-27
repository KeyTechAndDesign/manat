using System.Collections.Generic;

namespace KeyTD.Manat
{
    /// <summary>Where to send the customer after CreatePayment: a redirect (GET), an auto-submitted form, or nothing.</summary>
    public sealed class NextAction
    {
        /// <summary>The kinds of next step.</summary>
        public enum ActionType
        {
            /// <summary>Redirect the browser to <see cref="Url"/>.</summary>
            Redirect,
            /// <summary>Render <see cref="Fields"/> as hidden inputs and auto-submit them to <see cref="Url"/> with POST.</summary>
            Form,
            /// <summary>Nothing to do.</summary>
            None,
        }

        private static readonly IReadOnlyDictionary<string, string> NoFields = new Dictionary<string, string>();

        private NextAction(ActionType type, string? url, IReadOnlyList<KeyValuePair<string, string>> fields)
        {
            Type = type;
            Url = url;
            FieldList = fields;
            var map = new Dictionary<string, string>();
            foreach (var field in fields)
            {
                map[field.Key] = field.Value;
            }
            Fields = fields.Count == 0 ? NoFields : map;
        }

        /// <summary>Send the browser to <paramref name="url"/>.</summary>
        public static NextAction Redirect(string url) => new NextAction(ActionType.Redirect, url, new List<KeyValuePair<string, string>>());

        /// <summary>Auto-submit these fields to <paramref name="url"/>; their order is kept.</summary>
        public static NextAction PostForm(string url, IEnumerable<KeyValuePair<string, string>> fields) =>
            new NextAction(ActionType.Form, url, new List<KeyValuePair<string, string>>(fields));

        /// <summary>Nothing to do.</summary>
        public static NextAction Nothing() => new NextAction(ActionType.None, null, new List<KeyValuePair<string, string>>());

        /// <summary>Which kind of step.</summary>
        public ActionType Type { get; }

        /// <summary>Null for <see cref="ActionType.None"/>.</summary>
        public string? Url { get; }

        /// <summary>"POST" for a form, otherwise null.</summary>
        public string? Method => Type == ActionType.Form ? "POST" : null;

        /// <summary>Form fields; empty unless <see cref="ActionType.Form"/>.</summary>
        public IReadOnlyDictionary<string, string> Fields { get; }

        /// <summary>Form fields in the order they were signed.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> FieldList { get; }

        /// <summary>The spec's shape.</summary>
        public IReadOnlyDictionary<string, object?> ToDictionary()
        {
            var output = new Dictionary<string, object?> { ["type"] = Type.ToString().ToLowerInvariant() };
            if (Type != ActionType.None)
            {
                output["url"] = Url;
            }
            if (Type == ActionType.Form)
            {
                output["method"] = "POST";
                var fields = new Dictionary<string, object?>();
                foreach (var field in FieldList)
                {
                    fields[field.Key] = field.Value;
                }
                output["fields"] = fields;
            }
            return output;
        }
    }

    /// <summary>What CreatePayment returns: the provider's payment id and where to send the customer.</summary>
    public sealed class CreatePaymentResult
    {
        /// <summary>Creates a result.</summary>
        public CreatePaymentResult(string paymentId, PaymentStatus status, NextAction next, object? raw)
        {
            PaymentId = paymentId;
            Status = status;
            Next = next;
            Raw = raw;
        }

        /// <summary>The provider's id for this payment (or the orderId when the provider has none). Store it with the order.</summary>
        public string PaymentId { get; }

        /// <summary>Usually <see cref="PaymentStatus.Pending"/>.</summary>
        public PaymentStatus Status { get; }

        /// <summary>Where to send the customer.</summary>
        public NextAction Next { get; }

        /// <summary>The provider's response, untouched (dictionaries, lists, strings, numbers as parsed from JSON).</summary>
        public object? Raw { get; }

        /// <summary>The spec's shape, for logging or JSON.</summary>
        public IReadOnlyDictionary<string, object?> ToDictionary() => new Dictionary<string, object?>
        {
            ["paymentId"] = PaymentId,
            ["status"] = Status.ToValue(),
            ["next"] = Next.ToDictionary(),
            ["raw"] = Raw,
        };
    }

    /// <summary>The provider's current view of a payment (GetStatus, Capture, Cancel).</summary>
    public sealed class PaymentState
    {
        /// <summary>Creates a state.</summary>
        public PaymentState(string paymentId, PaymentStatus status, Money? amount = null, Money? refundedAmount = null, string? providerRef = null,
            object? raw = null)
        {
            PaymentId = paymentId;
            Status = status;
            Amount = amount;
            RefundedAmount = refundedAmount;
            ProviderRef = providerRef;
            Raw = raw;
        }

        /// <summary>The payment's id.</summary>
        public string PaymentId { get; }

        /// <summary>Its status.</summary>
        public PaymentStatus Status { get; }

        /// <summary>Or null when the provider does not report it.</summary>
        public Money? Amount { get; }

        /// <summary>Or null when nothing was refunded or the provider does not report it.</summary>
        public Money? RefundedAmount { get; }

        /// <summary>Opaque data some providers need for later operations (Azericard); or null.</summary>
        public string? ProviderRef { get; }

        /// <summary>The provider's response, untouched.</summary>
        public object? Raw { get; }

        /// <summary>The spec's shape, for logging or JSON. Absent values are left out.</summary>
        public IReadOnlyDictionary<string, object?> ToDictionary()
        {
            var output = new Dictionary<string, object?> { ["paymentId"] = PaymentId, ["status"] = Status.ToValue() };
            if (Amount != null)
            {
                output["amount"] = Amount.ToDictionary();
            }
            if (RefundedAmount != null)
            {
                output["refundedAmount"] = RefundedAmount.ToDictionary();
            }
            if (ProviderRef != null)
            {
                output["providerRef"] = ProviderRef;
            }
            output["raw"] = Raw;
            return output;
        }
    }

    /// <summary>An authenticated provider callback or redirect, from <see cref="ManatClient.VerifyCallbackAsync"/>.</summary>
    public sealed class PaymentEvent
    {
        /// <summary>Creates an event.</summary>
        public PaymentEvent(string paymentId, PaymentStatus status, string? orderId = null, Money? amount = null, string? providerRef = null, object? raw = null)
        {
            PaymentId = paymentId;
            Status = status;
            OrderId = orderId;
            Amount = amount;
            ProviderRef = providerRef;
            Raw = raw;
        }

        /// <summary>The event for a payment re-read from the provider's API, which is how unsigned redirects are handled.</summary>
        public static PaymentEvent FromState(PaymentState state) => new PaymentEvent(state.PaymentId, state.Status, amount: state.Amount, raw: state.Raw);

        /// <summary>The payment's id.</summary>
        public string PaymentId { get; }

        /// <summary>Its status.</summary>
        public PaymentStatus Status { get; }

        /// <summary>The merchant's orderId when the provider echoes it; or null.</summary>
        public string? OrderId { get; }

        /// <summary>Or null when the provider does not report it.</summary>
        public Money? Amount { get; }

        /// <summary>
        /// Opaque provider data needed for later operations (Azericard's RRN and INT_REF), or null.
        /// Store it with the order and pass it back to Refund/Capture/Cancel unchanged.
        /// </summary>
        public string? ProviderRef { get; }

        /// <summary>What the provider sent or answered, untouched.</summary>
        public object? Raw { get; }

        /// <summary>The spec's shape, for logging or JSON. Absent values are left out.</summary>
        public IReadOnlyDictionary<string, object?> ToDictionary()
        {
            var output = new Dictionary<string, object?> { ["paymentId"] = PaymentId };
            if (OrderId != null)
            {
                output["orderId"] = OrderId;
            }
            output["status"] = Status.ToValue();
            if (Amount != null)
            {
                output["amount"] = Amount.ToDictionary();
            }
            if (ProviderRef != null)
            {
                output["providerRef"] = ProviderRef;
            }
            output["raw"] = Raw;
            return output;
        }
    }

    /// <summary>What Refund returns.</summary>
    public sealed class RefundResult
    {
        /// <summary>Creates a result.</summary>
        public RefundResult(string paymentId, PaymentStatus status, string? refundId = null, object? raw = null)
        {
            PaymentId = paymentId;
            Status = status;
            RefundId = refundId;
            Raw = raw;
        }

        /// <summary>The payment's id.</summary>
        public string PaymentId { get; }

        /// <summary><see cref="PaymentStatus.Refunded"/> or <see cref="PaymentStatus.PartiallyRefunded"/> on success.</summary>
        public PaymentStatus Status { get; }

        /// <summary>The provider's id for the refund, when it gives one; or null.</summary>
        public string? RefundId { get; }

        /// <summary>The provider's responses, untouched.</summary>
        public object? Raw { get; }

        /// <summary>The spec's shape, for logging or JSON. Absent values are left out.</summary>
        public IReadOnlyDictionary<string, object?> ToDictionary()
        {
            var output = new Dictionary<string, object?> { ["paymentId"] = PaymentId };
            if (RefundId != null)
            {
                output["refundId"] = RefundId;
            }
            output["status"] = Status.ToValue();
            output["raw"] = Raw;
            return output;
        }
    }
}
