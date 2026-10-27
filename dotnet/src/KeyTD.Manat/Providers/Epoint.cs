using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using KeyTD.Manat.Internal;

namespace KeyTD.Manat.Providers
{
    /// <summary>Settings for <see cref="EpointProvider"/>.</summary>
    public sealed class EpointOptions : ProviderOptions
    {
        /// <summary>The merchant's public key (i0000000000).</summary>
        public string PublicKey { get; init; } = "";

        /// <summary>The merchant's private key, used to sign requests and check callbacks.</summary>
        public string PrivateKey { get; init; } = "";

        /// <summary>Overrides the API URL.</summary>
        public string? BaseUrl { get; init; }
    }

    /// <summary>
    /// Epoint (API v1).
    /// Every message is data (base64 JSON) + signature = base64(sha1(privateKey + data + privateKey)).
    /// </summary>
    public sealed class EpointProvider : PaymentProvider
    {
        private const string ProviderName = "epoint";
        private const string DefaultBaseUrl = "https://epoint.az/api/1";
        private static readonly Currency[] Supported = { Currency.AZN, Currency.USD, Currency.EUR, Currency.RUB };
        private static readonly Regex Base64Text = new Regex("^[A-Za-z0-9+/]+={0,2}$", RegexOptions.CultureInvariant);
        private static readonly IReadOnlyCollection<Capability> All = new[]
        {
            Capability.CreatePayment, Capability.VerifyCallback, Capability.GetStatus, Capability.Refund,
        };

        private readonly Http http;
        private readonly string baseUrl;
        private readonly string publicKey;
        private readonly string privateKey;

        /// <summary>Creates the adapter.</summary>
        public EpointProvider(EpointOptions options)
        {
            if (string.IsNullOrEmpty(options?.PublicKey))
            {
                throw new ManatException(ManatErrorCode.InvalidRequest, "epoint: publicKey is required");
            }
            if (string.IsNullOrEmpty(options!.PrivateKey))
            {
                throw new ManatException(ManatErrorCode.InvalidRequest, "epoint: privateKey is required");
            }
            publicKey = options.PublicKey;
            privateKey = options.PrivateKey;
            baseUrl = (options.BaseUrl ?? DefaultBaseUrl).TrimEnd('/');
            http = new Http(ProviderName, options);
        }

        /// <inheritdoc />
        public override string Name => ProviderName;

        /// <inheritdoc />
        public override IReadOnlyCollection<Capability> Capabilities => All;

        /// <summary>base64(sha1(privateKey + data + privateKey)), the signature on every request and callback.</summary>
        public static string Signature(string privateKey, string data) =>
            Crypto.Base64(Crypto.Sha1(Encoding.UTF8.GetBytes(privateKey + data + privateKey)));

        /// <summary>Encodes a payload the way the adapter sends it: JSON text, then base64, then signed.</summary>
        public static (string Data, string Signature) Encode(string privateKey, string json)
        {
            var data = Crypto.Base64(Encoding.UTF8.GetBytes(json));
            return (data, Signature(privateKey, data));
        }

        /// <summary>Maps a payment status.</summary>
        public static PaymentStatus MapStatus(string? status) => status switch
        {
            "new" => PaymentStatus.Pending,
            "success" => PaymentStatus.Paid,
            "returned" => PaymentStatus.Refunded,
            "failed" or "error" => PaymentStatus.Failed,
            _ => throw new ManatException(ManatErrorCode.ProviderError, $"epoint: unknown status \"{status}\"", ProviderName),
        };

        /// <inheritdoc />
        public override async Task<CreatePaymentResult> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Intent == Intent.Authorize)
            {
                throw new ManatException(ManatErrorCode.UnsupportedOperation,
                    "epoint: pre-authorisation is not supported (Epoint statuses do not tell a hold from a capture)", ProviderName);
            }
            var amount = request.Amount!;
            if (Array.IndexOf(Supported, amount.Currency) < 0)
            {
                throw new ManatException(ManatErrorCode.InvalidRequest, $"epoint: currency {amount.Currency} is not supported (AZN, USD, EUR, RUB)");
            }
            if (request.OrderId.Length > 255)
            {
                throw new ManatException(ManatErrorCode.InvalidRequest, "epoint: orderId is longer than 255 characters");
            }
            var payload = new JsonObject
            {
                ["public_key"] = publicKey,
                ["amount"] = amount.ToDecimalString(),
                ["currency"] = amount.Currency.ToString(),
                ["language"] = (request.Language ?? Language.Az).ToCode(),
                ["order_id"] = request.OrderId,
            };
            if (!string.IsNullOrEmpty(request.Description))
            {
                payload["description"] = Values.Truncate(request.Description!, 1000);
            }
            payload["success_redirect_url"] = request.ReturnUrl;
            payload["error_redirect_url"] = request.ReturnUrl;

            var response = await CallAsync("request", payload, cancellationToken).ConfigureAwait(false);
            if (!Equals(Values.Get(response, "status"), "success") || !Values.Truthy(Values.Get(response, "redirect_url"))
                || !Values.Truthy(Values.Get(response, "transaction")))
            {
                throw Error("payment was not created", response);
            }
            return new CreatePaymentResult(Values.Text(Values.Get(response, "transaction"))!, PaymentStatus.Pending,
                NextAction.Redirect(Values.Text(Values.Get(response, "redirect_url"))!), response);
        }

        /// <inheritdoc />
        public override Task<PaymentEvent> VerifyCallbackAsync(IncomingCallback callback, CancellationToken cancellationToken = default)
        {
            var data = callback.BodyField("data");
            var signature = callback.BodyField("signature");
            var body = (object?)callback.BodyText ?? callback.BodyFields;
            // An empty data still has a valid signature (sha1(key + key)), so it is rejected outright.
            if (string.IsNullOrEmpty(data) || string.IsNullOrEmpty(signature))
            {
                throw new ManatException(ManatErrorCode.InvalidSignature,
                    "epoint: callback has no data/signature (the browser redirect is not signed; use getStatus)", ProviderName, raw: body);
            }
            if (!Crypto.SafeEquals(signature, Signature(privateKey, data!)))
            {
                throw new ManatException(ManatErrorCode.InvalidSignature, "epoint: callback signature does not match", ProviderName, raw: body);
            }
            var payload = DecodeData(data!);
            if (payload == null || !Values.Truthy(Values.Get(payload, "transaction")))
            {
                throw new ManatException(ManatErrorCode.ProviderError, "epoint: signed callback has no transaction", ProviderName, raw: payload);
            }
            var orderId = Values.Get(payload, "order_id");
            return Task.FromResult(new PaymentEvent(Values.Text(Values.Get(payload, "transaction"))!, MapStatus(Values.Get(payload, "status") as string),
                Values.Truthy(orderId) ? Values.Text(orderId) : null, raw: payload));
        }

        /// <inheritdoc />
        public override async Task<PaymentState> GetStatusAsync(string paymentId, CancellationToken cancellationToken = default)
        {
            var response = await CallAsync("get-status", new JsonObject { ["public_key"] = publicKey, ["transaction"] = paymentId }, cancellationToken)
                .ConfigureAwait(false);
            // server_error means Epoint could not check the payment with the bank; the outcome is unknown.
            if (Equals(Values.Get(response, "status"), "server_error") || !Values.Truthy(Values.Get(response, "transaction")))
            {
                throw Error("status is not available", response);
            }
            if (!Equals(Values.Get(response, "transaction"), paymentId))
            {
                throw Error($"status is for transaction {Values.Text(Values.Get(response, "transaction"))}, not {paymentId}", response);
            }
            return new PaymentState(paymentId, MapStatus(Values.Get(response, "status") as string), raw: response);
        }

        /// <inheritdoc />
        public override async Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken cancellationToken = default)
        {
            var payload = new JsonObject { ["public_key"] = publicKey, ["language"] = "az", ["transaction"] = request.PaymentId };
            if (request.Amount != null)
            {
                payload["amount"] = request.Amount.ToDecimalString();
            }
            payload["currency"] = request.Amount?.Currency.ToString() ?? "AZN";

            var response = await CallAsync("reverse", payload, cancellationToken).ConfigureAwait(false);
            if (!Equals(Values.Get(response, "status"), "success"))
            {
                throw Error("refund failed", response);
            }
            var state = await GetStatusAsync(request.PaymentId, cancellationToken).ConfigureAwait(false);
            // Epoint has no partial-refund status: a reversed payment still reported as "success" was partly refunded.
            var status = state.Status == PaymentStatus.Paid ? PaymentStatus.PartiallyRefunded : state.Status;
            return new RefundResult(request.PaymentId, status, raw: new JsonObject { ["refund"] = response, ["payment"] = state.Raw });
        }

        private async Task<IReadOnlyDictionary<string, object?>> CallAsync(string method, JsonObject payload, CancellationToken cancellationToken)
        {
            var (data, signature) = Encode(privateKey, Json.Stringify(payload));
            var form = new[] { new KeyValuePair<string, string>("data", data), new KeyValuePair<string, string>("signature", signature) };
            var (status, raw) = await http.JsonAsync("POST", $"{baseUrl}/{method}", null, null, form, cancellationToken).ConfigureAwait(false);
            var body = Values.Map(raw) ?? throw new ManatException(ManatErrorCode.ProviderError, $"epoint: unexpected response (HTTP {status})", ProviderName, raw: raw);
            if (status >= 400)
            {
                throw Error($"HTTP {status}", body);
            }
            return body;
        }

        private static ManatException Error(string what, IReadOnlyDictionary<string, object?> response)
        {
            var message = Values.Get(response, "message");
            var code = Values.Get(response, "code");
            return new ManatException(ManatErrorCode.ProviderError, "epoint: " + (message != null ? Values.Text(message) : what), ProviderName,
                Values.Truthy(code) ? Values.Text(code) : null, Values.Truthy(message) ? Values.Text(message) : null, response);
        }

        private static IReadOnlyDictionary<string, object?>? DecodeData(string data)
        {
            if (!Base64Text.IsMatch(data))
            {
                throw new ManatException(ManatErrorCode.ProviderError, "epoint: callback data is not base64", ProviderName, raw: data);
            }
            try
            {
                return Values.Map(Json.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(data))));
            }
            catch (FormatException error)
            {
                throw new ManatException(ManatErrorCode.ProviderError, "epoint: callback data is not JSON", ProviderName, raw: data, inner: error);
            }
        }
    }
}
