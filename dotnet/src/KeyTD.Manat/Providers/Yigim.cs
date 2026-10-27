using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using KeyTD.Manat.Internal;

namespace KeyTD.Manat.Providers
{
    /// <summary>Settings for <see cref="YigimProvider"/>.</summary>
    public sealed class YigimOptions : ProviderOptions
    {
        /// <summary>Merchant name issued by Yığım (<c>X-Merchant</c>).</summary>
        public string Merchant { get; init; } = "";

        /// <summary>API key issued by Yığım (<c>X-API-Key</c>).</summary>
        public string ApiKey { get; init; } = "";

        /// <summary>Biller name provided by the acquirer.</summary>
        public string Biller { get; init; } = "";

        /// <summary>Payment page template; default <c>TPL0001</c>.</summary>
        public string? Template { get; init; }

        /// <summary>Default <see cref="ProviderEnvironment.Production"/>.</summary>
        public ProviderEnvironment Environment { get; init; }

        /// <summary>Overrides the API host.</summary>
        public string? BaseUrl { get; init; }
    }

    /// <summary>
    /// Yığım (MAGNET).
    /// The webhook carries only `reference` and is unsigned, so verifyCallback re-reads the payment.
    /// 09 (issuer timeout) and S7 are not final: the money may have moved, so they stay pending until cancelled.
    /// </summary>
    public sealed class YigimProvider : PaymentProvider
    {
        private const string ProviderName = "yigim";
        private const string TestUrl = "https://sandbox.api.pay.yigim.az";
        private const string ProductionUrl = "https://api.pay.yigim.az";
        private const long Ok = 0;
        private const string DefaultTemplate = "TPL0001";
        private static readonly Regex Reference = new Regex(@"^[^\s\x00-\x1f]{1,255}$", RegexOptions.CultureInvariant);
        private static readonly IReadOnlyCollection<Capability> All = new[]
        {
            Capability.CreatePayment, Capability.VerifyCallback, Capability.GetStatus, Capability.Refund, Capability.Capture, Capability.Cancel,
        };

        private readonly Http http;
        private readonly string baseUrl;
        private readonly YigimOptions options;
        private readonly string template;

        /// <summary>Creates the adapter.</summary>
        public YigimProvider(YigimOptions options)
        {
            this.options = options ?? throw new ManatException(ManatErrorCode.InvalidRequest, "yigim: options are required");
            if (string.IsNullOrEmpty(options.Merchant))
            {
                throw new ManatException(ManatErrorCode.InvalidRequest, "yigim: merchant is required");
            }
            if (string.IsNullOrEmpty(options.ApiKey))
            {
                throw new ManatException(ManatErrorCode.InvalidRequest, "yigim: apiKey is required");
            }
            if (string.IsNullOrEmpty(options.Biller))
            {
                throw new ManatException(ManatErrorCode.InvalidRequest, "yigim: biller is required");
            }
            template = string.IsNullOrEmpty(options.Template) ? DefaultTemplate : options.Template!;
            baseUrl = (options.BaseUrl ?? (options.Environment == ProviderEnvironment.Test ? TestUrl : ProductionUrl)).TrimEnd('/');
            http = new Http(ProviderName, options);
        }

        /// <inheritdoc />
        public override string Name => ProviderName;

        /// <inheritdoc />
        public override IReadOnlyCollection<Capability> Capabilities => All;

        /// <summary>Maps a payment status code.</summary>
        public static PaymentStatus MapStatus(string? status) => status switch
        {
            "S0" or "S2" or "S7" or "09" => PaymentStatus.Pending, // waiting for card data, in progress, malfunction, issuer timeout
            "S1" => PaymentStatus.Authorized, // pre-authorised DMS payment: charge or cancel within 30 days
            "00" => PaymentStatus.Paid,
            "S4" => PaymentStatus.Cancelled, // reversed
            "S3" or "01" or "02" or "03" or "04" or "05" or "06" or "07" or "08" => PaymentStatus.Failed, // unknown error, declines
            _ => throw new ManatException(ManatErrorCode.ProviderError, $"yigim: unknown payment status \"{status}\"", ProviderName),
        };

        /// <inheritdoc />
        public override async Task<CreatePaymentResult> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken = default)
        {
            RequireReference(request.OrderId);
            // The return URL travels in `extra` (name=value;name=value), which the gateway also cuts at `&`.
            if (request.ReturnUrl.IndexOf('&') >= 0 || request.ReturnUrl.IndexOf(';') >= 0)
            {
                throw new ManatException(ManatErrorCode.InvalidRequest, "yigim: returnUrl may carry at most one query parameter and no \";\"");
            }
            var amount = request.Amount!;
            var parameters = new List<KeyValuePair<string, string>>
            {
                Pair("reference", request.OrderId),
                Pair("type", request.Intent == Intent.Authorize ? "DMS" : "SMS"),
                Pair("amount", amount.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Pair("currency", amount.Currency.NumericCode()),
                Pair("biller", options.Biller),
            };
            if (!string.IsNullOrEmpty(request.Description))
            {
                parameters.Add(Pair("description", request.Description!));
            }
            parameters.Add(Pair("template", template));
            parameters.Add(Pair("language", (request.Language ?? Language.Az).ToCode()));
            // The gateway appends `&reference=…` to the callback URL as is, so it needs a query string.
            if (!string.IsNullOrEmpty(request.CallbackUrl))
            {
                parameters.Add(Pair("callback", request.CallbackUrl!.IndexOf('?') >= 0 ? request.CallbackUrl : request.CallbackUrl + "?"));
            }
            parameters.Add(Pair("extra", $"back-url={request.ReturnUrl};fail-url={request.ReturnUrl}"));
            var reply = await CallAsync("payment/create", parameters, cancellationToken).ConfigureAwait(false);
            if (!(Values.Get(reply, "url") is string url) || url.Length == 0)
            {
                throw Error("payment page URL is missing", reply);
            }
            return new CreatePaymentResult(request.OrderId, PaymentStatus.Pending, NextAction.Redirect(url), reply);
        }

        /// <inheritdoc />
        public override async Task<PaymentEvent> VerifyCallbackAsync(IncomingCallback callback, CancellationToken cancellationToken = default)
        {
            var reference = callback.QueryValue("reference") ?? callback.BodyField("reference");
            if (reference == null || !Reference.IsMatch(reference))
            {
                throw new ManatException(ManatErrorCode.InvalidSignature, "yigim: callback has no reference", ProviderName, raw: callback.ToDictionary());
            }
            // The webhook is unsigned; trust only what payment/status says about this reference.
            var state = await GetStatusAsync(reference, cancellationToken).ConfigureAwait(false);
            return new PaymentEvent(state.PaymentId, state.Status, state.PaymentId, state.Amount, raw: state.Raw);
        }

        /// <inheritdoc />
        public override async Task<PaymentState> GetStatusAsync(string paymentId, CancellationToken cancellationToken = default)
        {
            RequireReference(paymentId);
            var reply = await CallAsync("payment/status", new[] { Pair("reference", paymentId) }, cancellationToken).ConfigureAwait(false);
            var reference = Values.Get(reply, "reference");
            if (Values.Truthy(reference) && !Equals(reference, paymentId))
            {
                throw Error($"status is for payment {Values.Text(reference)}, not {paymentId}", reply);
            }
            var status = MapStatus(Values.Text(Values.Get(reply, "status")));
            var currency = Currencies.FromNumeric(Values.Get(reply, "currency") as string);
            Money? amount = null;
            var amountValue = Values.Get(reply, "amount");
            if (amountValue != null && !Equals(amountValue, "") && currency != null)
            {
                amount = Values.Money(amountValue, currency.Value);
            }
            return new PaymentState(paymentId, status, amount, null, null, reply);
        }

        /// <inheritdoc />
        public override async Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken cancellationToken = default)
        {
            var reply = await CallAsync("payment/refund", WithAmount(request.PaymentId, request.Amount), cancellationToken).ConfigureAwait(false);
            var state = await GetStatusAsync(request.PaymentId, cancellationToken).ConfigureAwait(false);
            // While payment/status still reads 00 the requested amount decides.
            var status = state.Status;
            if (status == PaymentStatus.Paid)
            {
                var partial = request.Amount != null && state.Amount != null && request.Amount.Amount < state.Amount.Amount;
                status = partial ? PaymentStatus.PartiallyRefunded : PaymentStatus.Refunded;
            }
            return new RefundResult(request.PaymentId, status, raw: new JsonObject { ["refund"] = reply, ["payment"] = state.Raw });
        }

        /// <inheritdoc />
        public override async Task<PaymentState> CaptureAsync(PaymentAction action, CancellationToken cancellationToken = default)
        {
            await CallAsync("payment/charge", WithAmount(action.PaymentId, action.Amount), cancellationToken).ConfigureAwait(false);
            return await GetStatusAsync(action.PaymentId, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public override async Task<PaymentState> CancelAsync(PaymentAction action, CancellationToken cancellationToken = default)
        {
            await CallAsync("payment/cancel", new[] { Pair("reference", action.PaymentId) }, cancellationToken).ConfigureAwait(false);
            return await GetStatusAsync(action.PaymentId, cancellationToken).ConfigureAwait(false);
        }

        private async Task<IReadOnlyDictionary<string, object?>> CallAsync(string command, IReadOnlyList<KeyValuePair<string, string>> parameters,
            CancellationToken cancellationToken)
        {
            RequireReference(parameters[0].Value);
            var headers = new[] { Pair("x-merchant", options.Merchant), Pair("x-api-key", options.ApiKey), Pair("x-type", "JSON") };
            var (status, raw) = await http.JsonAsync("GET", $"{baseUrl}/{command}?{Form.Encode(parameters)}", headers, null, null, cancellationToken)
                .ConfigureAwait(false);
            // Replies may be nested under `response`.
            var data = Values.Map(raw);
            var reply = Values.Map(Values.Get(data, "response")) ?? data;
            if (reply == null || !(Code(reply) is long code))
            {
                throw new ManatException(ManatErrorCode.ProviderError, $"yigim: unexpected response (HTTP {status})", ProviderName, raw: raw);
            }
            if (status >= 400 || code != Ok)
            {
                throw Error($"HTTP {status}", reply);
            }
            return reply;
        }

        private static ManatException Error(string what, IReadOnlyDictionary<string, object?> reply)
        {
            var fromProvider = Code(reply) is long code && code != Ok;
            var message = Values.Truthy(Values.Get(reply, "message")) ? Values.Text(Values.Get(reply, "message")) : null;
            return new ManatException(ManatErrorCode.ProviderError, "yigim: " + (fromProvider && message != null ? message : what), ProviderName,
                fromProvider ? Code(reply)!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : null, fromProvider ? message : null, reply);
        }

        /// <summary><c>code</c> comes as 0 or "0".</summary>
        private static long? Code(IReadOnlyDictionary<string, object?> reply) => Values.Get(reply, "code") switch
        {
            long number => number,
            string text when long.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };

        private static List<KeyValuePair<string, string>> WithAmount(string reference, Money? amount)
        {
            var parameters = new List<KeyValuePair<string, string>> { Pair("reference", reference) };
            if (amount != null)
            {
                parameters.Add(Pair("amount", amount.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
            return parameters;
        }

        private static KeyValuePair<string, string> Pair(string key, string value) => new KeyValuePair<string, string>(key, value);

        private static void RequireReference(string? reference)
        {
            if (reference == null || !Reference.IsMatch(reference))
            {
                throw new ManatException(ManatErrorCode.InvalidRequest, $"yigim: invalid reference \"{reference}\"");
            }
        }
    }
}
