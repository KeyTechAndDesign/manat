using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using KeyTD.Manat.Internal;

namespace KeyTD.Manat.Providers
{
    /// <summary>Settings for <see cref="PayriffProvider"/>.</summary>
    public sealed class PayriffOptions : ProviderOptions
    {
        /// <summary>The application's secret key; a "Development" application's key is test mode.</summary>
        public string SecretKey { get; init; } = "";

        /// <summary>Overrides the API host.</summary>
        public string? BaseUrl { get; init; }
    }

    /// <summary>
    /// Payriff (API v3).
    /// The webhook is unsigned, so verifyCallback re-reads the order. There is no void for a pre-auth.
    /// </summary>
    public sealed class PayriffProvider : PaymentProvider
    {
        private const string ProviderName = "payriff";
        private const string DefaultBaseUrl = "https://api.payriff.com";
        private const string Success = "00000";
        private static readonly Regex OrderId = new Regex("^[A-Za-z0-9-]{1,64}$", RegexOptions.CultureInvariant);
        private static readonly IReadOnlyCollection<Capability> All = new[]
        {
            Capability.CreatePayment, Capability.VerifyCallback, Capability.GetStatus, Capability.Refund, Capability.Capture,
        };

        private readonly Http http;
        private readonly string baseUrl;
        private readonly string secretKey;

        /// <summary>Creates the adapter.</summary>
        public PayriffProvider(PayriffOptions options)
        {
            if (string.IsNullOrEmpty(options?.SecretKey))
            {
                throw new ManatException(ManatErrorCode.InvalidRequest, "payriff: secretKey is required");
            }
            secretKey = options!.SecretKey;
            baseUrl = (options.BaseUrl ?? DefaultBaseUrl).TrimEnd('/');
            http = new Http(ProviderName, options);
        }

        /// <inheritdoc />
        public override string Name => ProviderName;

        /// <inheritdoc />
        public override IReadOnlyCollection<Capability> Capabilities => All;

        /// <summary>Maps payload.paymentStatus.</summary>
        public static PaymentStatus MapStatus(string? status) => status switch
        {
            "CREATED" or "PENDING" or "ACCEPTED" or "IN_REVIEW" => PaymentStatus.Pending,
            "APPROVED" or "PAID" or "PARTIAL" or "CASH" => PaymentStatus.Paid, // PARTIAL: pre-auth partially captured
            "PREAUTH_APPROVED" => PaymentStatus.Authorized,
            "REFUNDED" or "REFUND_IN_PROGRESS" => PaymentStatus.Refunded,
            "PARTIAL_REFUND" => PaymentStatus.PartiallyRefunded,
            "CANCELED" or "EXPIRED" or "REVERSE" or "PREAUTH_EXPIRED" => PaymentStatus.Cancelled,
            "DECLINED" => PaymentStatus.Failed,
            _ => throw new ManatException(ManatErrorCode.ProviderError, $"payriff: unknown paymentStatus \"{status}\"", ProviderName),
        };

        /// <inheritdoc />
        public override async Task<CreatePaymentResult> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken = default)
        {
            if (request.CallbackUrl != null && !request.CallbackUrl.StartsWith("https://", System.StringComparison.Ordinal))
            {
                throw new ManatException(ManatErrorCode.InvalidRequest, "payriff: callbackUrl must be an HTTPS URL");
            }
            var amount = request.Amount!;
            var body = new JsonObject
            {
                ["amount"] = amount.ToNumber(),
                ["currency"] = amount.Currency.ToString(),
                ["language"] = (request.Language ?? Language.Az).ToCode().ToUpperInvariant(),
                ["operation"] = request.Intent == Intent.Authorize ? "PRE_AUTH" : "PURCHASE",
                ["description"] = request.Description ?? $"Order {request.OrderId}",
                ["redirectUrl"] = request.ReturnUrl,
            };
            if (!string.IsNullOrEmpty(request.CallbackUrl))
            {
                body["callbackUrl"] = request.CallbackUrl;
            }
            if (request.Metadata != null && request.Metadata.Count > 0)
            {
                var metadata = new JsonObject();
                foreach (var pair in request.Metadata)
                {
                    metadata[pair.Key] = pair.Value;
                }
                body["metadata"] = metadata;
            }
            var payload = Values.Map(Values.Get(await CallAsync("POST", "/api/v3/orders", body, cancellationToken).ConfigureAwait(false), "payload"));
            if (payload == null || !Values.Truthy(Values.Get(payload, "orderId")) || !Values.Truthy(Values.Get(payload, "paymentUrl")))
            {
                throw new ManatException(ManatErrorCode.ProviderError, "payriff: order response has no orderId or paymentUrl", ProviderName, raw: payload);
            }
            return new CreatePaymentResult(Values.Text(Values.Get(payload, "orderId"))!, PaymentStatus.Pending,
                NextAction.Redirect(Values.Text(Values.Get(payload, "paymentUrl"))!), payload);
        }

        /// <inheritdoc />
        public override async Task<PaymentEvent> VerifyCallbackAsync(IncomingCallback callback, CancellationToken cancellationToken = default)
        {
            var orderId = CallbackOrderId(callback);
            if (string.IsNullOrEmpty(orderId))
            {
                throw new ManatException(ManatErrorCode.InvalidSignature, "payriff: callback has no payload.orderId", ProviderName,
                    raw: (object?)callback.BodyText ?? callback.BodyFields);
            }
            // The webhook is unsigned; trust only what the API says about this order.
            return PaymentEvent.FromState(await GetStatusAsync(orderId!, cancellationToken).ConfigureAwait(false));
        }

        /// <inheritdoc />
        public override async Task<PaymentState> GetStatusAsync(string paymentId, CancellationToken cancellationToken = default)
        {
            if (!OrderId.IsMatch(paymentId))
            {
                throw new ManatException(ManatErrorCode.InvalidRequest, $"payriff: invalid paymentId \"{paymentId}\"");
            }
            var order = Values.Map(Values.Get(await CallAsync("GET", "/api/v3/orders/" + paymentId, null, cancellationToken).ConfigureAwait(false), "payload"));
            if (order == null || !Values.Truthy(Values.Get(order, "orderId")))
            {
                throw new ManatException(ManatErrorCode.ProviderError, $"payriff: no order in response for {paymentId}", ProviderName, raw: order);
            }
            Money? amount = null;
            if (Values.Get(order, "amount") != null && Values.Truthy(Values.Get(order, "currencyType")))
            {
                var currency = Currencies.Find(Values.Get(order, "currencyType") as string)
                    ?? throw new ManatException(ManatErrorCode.ProviderError, $"payriff: unsupported currency \"{Values.Get(order, "currencyType")}\"", ProviderName);
                amount = Values.Money(Values.Get(order, "amount"), currency);
            }
            return new PaymentState(Values.Text(Values.Get(order, "orderId"))!, OrderStatus(order), amount, raw: order);
        }

        /// <inheritdoc />
        public override async Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken cancellationToken = default)
        {
            var paymentId = request.PaymentId;
            // Payriff rejects a refund without an amount ("Request refund amount is null"), so a full refund sends the order amount.
            var amount = request.Amount ?? (await GetStatusAsync(paymentId, cancellationToken).ConfigureAwait(false)).Amount
                ?? throw new ManatException(ManatErrorCode.ProviderError, $"payriff: order {paymentId} has no amount to refund", ProviderName);
            var envelope = await CallAsync("POST", "/api/v3/refund",
                new JsonObject { ["orderId"] = paymentId, ["refundReason"] = "Refund", ["amount"] = amount.ToNumber() }, cancellationToken).ConfigureAwait(false);
            var state = await GetStatusAsync(paymentId, cancellationToken).ConfigureAwait(false);
            var responseId = Values.Get(envelope, "responseId");
            return new RefundResult(paymentId, state.Status, Values.Truthy(responseId) ? Values.Text(responseId) : null,
                new JsonObject { ["refund"] = envelope, ["order"] = state.Raw });
        }

        /// <inheritdoc />
        public override async Task<PaymentState> CaptureAsync(PaymentAction action, CancellationToken cancellationToken = default)
        {
            var body = new JsonObject { ["orderId"] = action.PaymentId };
            if (action.Amount != null)
            {
                body["amount"] = action.Amount.ToNumber();
            }
            await CallAsync("POST", "/api/v3/complete", body, cancellationToken).ConfigureAwait(false);
            return await GetStatusAsync(action.PaymentId, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>The whole envelope.</summary>
        private async Task<IReadOnlyDictionary<string, object?>> CallAsync(string method, string path, object? body, CancellationToken cancellationToken)
        {
            var headers = new[] { new KeyValuePair<string, string>("authorization", secretKey) };
            var (status, raw) = await http.JsonAsync(method, baseUrl + path, headers, body, null, cancellationToken).ConfigureAwait(false);
            var data = Values.Map(raw);
            if (data == null || !data.ContainsKey("code"))
            {
                throw new ManatException(ManatErrorCode.ProviderError, $"payriff: unexpected response (HTTP {status})", ProviderName, raw: raw);
            }
            if (status >= 400 || !Equals(Values.Get(data, "code"), Success))
            {
                var code = Values.Get(data, "code");
                var message = Values.Get(data, "message");
                throw new ManatException(ManatErrorCode.ProviderError, "payriff: " + (message != null ? Values.Text(message) : $"code {code}"), ProviderName,
                    Values.Truthy(code) ? Values.Text(code) : null, Values.Truthy(message) ? Values.Text(message) : null, data);
            }
            return data;
        }

        /// <summary>After a partial refund Payriff reports the order as REFUNDED; only its transaction says PARTIAL_REFUND.</summary>
        private static PaymentStatus OrderStatus(IReadOnlyDictionary<string, object?> order)
        {
            var status = MapStatus(Values.Get(order, "paymentStatus") as string);
            if (status == PaymentStatus.Refunded && Values.List(Values.Get(order, "transactions")) is { } transactions)
            {
                foreach (var transaction in transactions)
                {
                    if (Equals(Values.Get(transaction, "status"), "PARTIAL_REFUND"))
                    {
                        return PaymentStatus.PartiallyRefunded;
                    }
                }
            }
            return status;
        }

        private static string? CallbackOrderId(IncomingCallback callback)
        {
            object? body = callback.BodyFields;
            if (callback.BodyText != null)
            {
                try
                {
                    body = Json.Parse(callback.BodyText);
                }
                catch (JsonParseException)
                {
                    body = null;
                }
            }
            return Values.Get(body, "payload", "orderId") as string ?? callback.QueryValue("orderId");
        }
    }
}
