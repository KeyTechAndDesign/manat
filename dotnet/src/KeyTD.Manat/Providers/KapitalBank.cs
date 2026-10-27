using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using KeyTD.Manat.Internal;

namespace KeyTD.Manat.Providers
{
    /// <summary>Settings for <see cref="KapitalBankProvider"/>.</summary>
    public sealed class KapitalBankOptions : ProviderOptions
    {
        /// <summary>The merchant's API user, such as <c>TerminalSys/xxxx</c>.</summary>
        public string Username { get; init; } = "";

        /// <summary>The API user's password.</summary>
        public string Password { get; init; } = "";

        /// <summary>Default <see cref="ProviderEnvironment.Production"/>.</summary>
        public ProviderEnvironment Environment { get; init; }

        /// <summary>Overrides the environment's API URL.</summary>
        public string? BaseUrl { get; init; }
    }

    /// <summary>
    /// Kapital Bank (TXPG REST API).
    /// The redirect back (?ID=…&amp;STATUS=…) is unsigned, so verifyCallback re-reads the order.
    /// </summary>
    public sealed class KapitalBankProvider : PaymentProvider
    {
        private const string ProviderName = "kapitalbank";
        private const string TestUrl = "https://txpgtst.kapitalbank.az/api";
        private const string ProductionUrl = "https://e-commerce.kapitalbank.az/api";
        private static readonly Regex OrderId = new Regex(@"^\d+$", RegexOptions.CultureInvariant);
        private static readonly Regex BareOrigin = new Regex(@"^[a-zA-Z][a-zA-Z0-9+.-]*://[^/]+$", RegexOptions.CultureInvariant);
        private static readonly IReadOnlyCollection<Capability> All = new[]
        {
            Capability.CreatePayment, Capability.VerifyCallback, Capability.GetStatus, Capability.Refund, Capability.Capture, Capability.Cancel,
        };

        private readonly Http http;
        private readonly string baseUrl;
        private readonly string authHeader;

        /// <summary>Creates the adapter.</summary>
        public KapitalBankProvider(KapitalBankOptions options)
        {
            if (string.IsNullOrEmpty(options?.Username) || string.IsNullOrEmpty(options!.Password))
            {
                throw new ManatException(ManatErrorCode.InvalidRequest, "kapitalbank: username and password are required");
            }
            baseUrl = (options.BaseUrl ?? (options.Environment == ProviderEnvironment.Test ? TestUrl : ProductionUrl)).TrimEnd('/');
            authHeader = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(options.Username + ":" + options.Password));
            http = new Http(ProviderName, options);
        }

        /// <inheritdoc />
        public override string Name => ProviderName;

        /// <inheritdoc />
        public override IReadOnlyCollection<Capability> Capabilities => All;

        /// <summary>Maps an order's status.</summary>
        public static PaymentStatus MapStatus(IReadOnlyDictionary<string, object?> order)
        {
            order.TryGetValue("status", out var status);
            switch (status as string)
            {
                case "Preparing":
                    return PaymentStatus.Pending;
                case "Authorized":
                    return PaymentStatus.Authorized;
                case "PartPaid":
                case "FullyPaid":
                case "Funded":
                case "Closed":
                {
                    var refundRaw = Values.Get(order, "clearedRefundAmount");
                    var refunded = refundRaw == null ? 0m : Values.Decimal(refundRaw);
                    if (refunded > 0)
                    {
                        var chargeRaw = Values.Get(order, "clearedChargeAmount");
                        var charged = chargeRaw == null ? null : Values.Decimal(chargeRaw);
                        var full = chargeRaw == null || (charged != null && refunded >= charged);
                        return full ? PaymentStatus.Refunded : PaymentStatus.PartiallyRefunded;
                    }
                    return PaymentStatus.Paid;
                }
                case "Refunded":
                    return PaymentStatus.Refunded;
                case "Cancelled":
                case "Voided":
                case "Expired":
                    return PaymentStatus.Cancelled;
                case "Rejected":
                case "Refused":
                case "Declined":
                    return PaymentStatus.Failed;
                default:
                    throw new ManatException(ManatErrorCode.ProviderError, $"kapitalbank: unknown order status \"{status}\"", ProviderName, raw: order);
            }
        }

        /// <inheritdoc />
        public override async Task<CreatePaymentResult> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken = default)
        {
            var amount = request.Amount!;
            var order = new JsonObject
            {
                ["typeRid"] = request.Intent == Intent.Authorize ? "Order_DMS" : "Order_SMS",
                ["amount"] = amount.ToDecimalString(),
                ["currency"] = amount.Currency.ToString(),
                ["language"] = (request.Language ?? Language.Az).ToCode(),
                ["description"] = request.Description ?? request.OrderId,
                ["hppRedirectUrl"] = request.ReturnUrl,
            };
            var data = await CallAsync("POST", "/order", new JsonObject { ["order"] = order }, cancellationToken).ConfigureAwait(false);
            var created = Values.Map(Values.Get(data, "order"));
            if (created == null || !Values.Truthy(Values.Get(created, "id")) || !Values.Truthy(Values.Get(created, "hppUrl"))
                || !Values.Truthy(Values.Get(created, "password")))
            {
                throw new ManatException(ManatErrorCode.ProviderError, "kapitalbank: order response is missing id, hppUrl or password", ProviderName, raw: data);
            }
            var id = Values.Text(Values.Get(created, "id"))!;
            var url = WithQuery(Values.Text(Values.Get(created, "hppUrl"))!, new[]
            {
                new KeyValuePair<string, string>("id", id),
                new KeyValuePair<string, string>("password", Values.Text(Values.Get(created, "password"))!),
            });
            var status = Values.Get(created, "status") != null ? MapStatus(created) : PaymentStatus.Pending;
            return new CreatePaymentResult(id, status, NextAction.Redirect(url), data);
        }

        /// <inheritdoc />
        public override async Task<PaymentEvent> VerifyCallbackAsync(IncomingCallback callback, CancellationToken cancellationToken = default)
        {
            var id = callback.QueryValue("ID") ?? callback.QueryValue("id") ?? callback.BodyField("ID");
            if (id == null || !OrderId.IsMatch(id))
            {
                throw new ManatException(ManatErrorCode.InvalidSignature, "kapitalbank: callback has no numeric ID", ProviderName, raw: callback.ToDictionary());
            }
            // The redirect is unsigned; the API is the source of truth.
            return PaymentEvent.FromState(await GetStatusAsync(id, cancellationToken).ConfigureAwait(false));
        }

        /// <inheritdoc />
        public override async Task<PaymentState> GetStatusAsync(string paymentId, CancellationToken cancellationToken = default) =>
            ToState(await FetchOrderAsync(paymentId, cancellationToken).ConfigureAwait(false));

        /// <inheritdoc />
        public override async Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken cancellationToken = default)
        {
            var paymentId = request.PaymentId;
            var amount = request.Amount ?? await RemainingRefundableAsync(paymentId, cancellationToken).ConfigureAwait(false);
            var data = await ExecAsync(paymentId, new JsonObject { ["phase"] = "Single", ["amount"] = amount.ToDecimalString(), ["type"] = "Refund" },
                cancellationToken).ConfigureAwait(false);
            var state = await GetStatusAsync(paymentId, cancellationToken).ConfigureAwait(false);
            var actionId = Values.Get(data, "tran", "match", "tranActionId");
            return new RefundResult(paymentId, state.Status, Values.Truthy(actionId) ? Values.Text(actionId) : null,
                new JsonObject { ["exec"] = data, ["order"] = state.Raw });
        }

        /// <inheritdoc />
        public override async Task<PaymentState> CaptureAsync(PaymentAction action, CancellationToken cancellationToken = default)
        {
            var paymentId = action.PaymentId;
            var amount = action.Amount ?? (await GetStatusAsync(paymentId, cancellationToken).ConfigureAwait(false)).Amount
                ?? throw new ManatException(ManatErrorCode.ProviderError, $"kapitalbank: order {paymentId} has no amount to capture", ProviderName);
            await ExecAsync(paymentId, new JsonObject { ["phase"] = "Clearing", ["amount"] = amount.ToDecimalString() }, cancellationToken).ConfigureAwait(false);
            return await GetStatusAsync(paymentId, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public override async Task<PaymentState> CancelAsync(PaymentAction action, CancellationToken cancellationToken = default)
        {
            var paymentId = action.PaymentId;
            var current = await GetStatusAsync(paymentId, cancellationToken).ConfigureAwait(false);
            // Reversal of a pre-auth hold uses phase Auth; a same-day purchase uses phase Single.
            var phase = current.Status == PaymentStatus.Authorized ? "Auth" : "Single";
            await ExecAsync(paymentId, new JsonObject { ["phase"] = phase, ["voidKind"] = "Full" }, cancellationToken).ConfigureAwait(false);
            return await GetStatusAsync(paymentId, cancellationToken).ConfigureAwait(false);
        }

        private async Task<Money> RemainingRefundableAsync(string paymentId, CancellationToken cancellationToken)
        {
            var order = await FetchOrderAsync(paymentId, cancellationToken).ConfigureAwait(false);
            var currency = CurrencyOf(Values.Get(order, "currency"), paymentId);
            var charged = Values.Get(order, "clearedChargeAmount") ?? Values.Get(order, "amount") ?? "0";
            var remaining = Values.Money(charged, currency).Amount;
            if (Values.Truthy(Values.Get(order, "clearedRefundAmount")))
            {
                remaining -= Values.Money(Values.Get(order, "clearedRefundAmount"), currency).Amount;
            }
            if (remaining <= 0)
            {
                throw new ManatException(ManatErrorCode.InvalidRequest, $"kapitalbank: order {paymentId} has nothing left to refund", ProviderName, raw: order);
            }
            return Money.Of(remaining, currency);
        }

        private async Task<JsonObject> FetchOrderAsync(string paymentId, CancellationToken cancellationToken)
        {
            RequireOrderId(paymentId);
            var data = await CallAsync("GET", $"/order/{paymentId}?tranDetailLevel=2&tokenDetailLevel=2&orderDetailLevel=2", null, cancellationToken)
                .ConfigureAwait(false);
            if (!(Values.Get(data, "order") is JsonObject order))
            {
                throw new ManatException(ManatErrorCode.ProviderError, $"kapitalbank: no order in response for {paymentId}", ProviderName, raw: data);
            }
            if (Values.Get(order, "id") == null)
            {
                order["id"] = paymentId;
            }
            return order;
        }

        private static PaymentState ToState(JsonObject order)
        {
            var id = Values.Text(Values.Get(order, "id"))!;
            Money? amount = null;
            Money? refunded = null;
            if (Values.Get(order, "amount") != null && Values.Get(order, "currency") != null)
            {
                amount = Values.Money(Values.Get(order, "amount"), CurrencyOf(Values.Get(order, "currency"), id));
            }
            if (Values.Truthy(Values.Get(order, "clearedRefundAmount")) && Values.Get(order, "currency") != null)
            {
                refunded = Values.Money(Values.Get(order, "clearedRefundAmount"), CurrencyOf(Values.Get(order, "currency"), id));
            }
            return new PaymentState(id, MapStatus(order), amount, refunded, null, order);
        }

        private Task<IReadOnlyDictionary<string, object?>> ExecAsync(string paymentId, JsonObject tran, CancellationToken cancellationToken)
        {
            RequireOrderId(paymentId);
            return CallAsync("POST", $"/order/{paymentId}/exec-tran", new JsonObject { ["tran"] = tran }, cancellationToken);
        }

        private async Task<IReadOnlyDictionary<string, object?>> CallAsync(string method, string path, object? body, CancellationToken cancellationToken)
        {
            var headers = new[] { new KeyValuePair<string, string>("authorization", authHeader) };
            var (status, raw) = await http.JsonAsync(method, baseUrl + path, headers, body, null, cancellationToken).ConfigureAwait(false);
            var data = Values.Map(raw);
            if (status >= 400 || (data != null && Values.Truthy(Values.Get(data, "errorCode"))))
            {
                var code = Values.Text(Values.Get(data, "errorCode"));
                var message = Values.Text(Values.Get(data, "errorDescription"));
                throw new ManatException(ManatErrorCode.ProviderError, "kapitalbank: " + (message ?? $"HTTP {status}"), ProviderName, code, message, raw);
            }
            return data ?? new JsonObject();
        }

        private static void RequireOrderId(string paymentId)
        {
            if (!OrderId.IsMatch(paymentId))
            {
                throw new ManatException(ManatErrorCode.InvalidRequest, $"kapitalbank: paymentId must be the numeric order id, got \"{paymentId}\"");
            }
        }

        private static Currency CurrencyOf(object? code, string paymentId) =>
            Currencies.Find(code as string)
            ?? throw new ManatException(ManatErrorCode.ProviderError, $"kapitalbank: unexpected currency \"{code}\" on order {paymentId}", ProviderName);

        /// <summary>Sets query parameters on a URL, replacing existing ones with the same name (like URL.searchParams.set).</summary>
        private static string WithQuery(string url, IEnumerable<KeyValuePair<string, string>> parameters)
        {
            var fragment = "";
            var hash = url.IndexOf('#');
            if (hash >= 0)
            {
                fragment = url.Substring(hash);
                url = url.Substring(0, hash);
            }
            var question = url.IndexOf('?');
            var baseUrl = question >= 0 ? url.Substring(0, question) : url;
            var query = question >= 0 ? url.Substring(question + 1) : "";
            if (BareOrigin.IsMatch(baseUrl))
            {
                baseUrl += "/";
            }
            var merged = new JsonObject();
            foreach (var pair in Form.Decode(query))
            {
                merged[pair.Key] = pair.Value;
            }
            foreach (var pair in parameters)
            {
                merged[pair.Key] = pair.Value;
            }
            var fields = new List<KeyValuePair<string, string>>();
            foreach (var pair in merged)
            {
                fields.Add(new KeyValuePair<string, string>(pair.Key, (string)pair.Value!));
            }
            return baseUrl + "?" + Form.Encode(fields) + fragment;
        }
    }
}
