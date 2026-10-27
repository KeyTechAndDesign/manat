using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using KeyTD.Manat.Internal;

namespace KeyTD.Manat.Providers
{
    /// <summary>Settings for <see cref="GoldenPayProvider"/>.</summary>
    public sealed class GoldenPayOptions : ProviderOptions
    {
        /// <summary>Auth key from the GoldenPay merchant panel; signs every request.</summary>
        public string AuthKey { get; init; } = "";

        /// <summary>Merchant name issued by GoldenPay.</summary>
        public string MerchantName { get; init; } = "";

        /// <summary>Default <see cref="ProviderEnvironment.Production"/>.</summary>
        public ProviderEnvironment Environment { get; init; }

        /// <summary>Overrides the API host (also used for the payment page).</summary>
        public string? BaseUrl { get; init; }
    }

    /// <summary>
    /// GoldenPay. AZN only.
    /// The redirect back is unsigned, so verifyCallback re-reads the payment.
    /// </summary>
    public sealed class GoldenPayProvider : PaymentProvider
    {
        private const string ProviderName = "goldenpay";
        private const string TestUrl = "https://rest-newpgtest.goldenpay.az";
        private const string ProductionUrl = "https://rest-pg.goldenpay.az";
        private const long Success = 1;
        private const long Reversed = 817;
        /// <summary>getPaymentResult codes that describe a payment; others are request errors.</summary>
        private static readonly HashSet<long> PaymentCodes = new HashSet<long> { Success, 813, 814, 815, 816, Reversed, 818, 819 };
        /// <summary>Description limit in bytes.</summary>
        private const int MaxDescriptionBytes = 500;
        private static readonly Regex PaymentKey = new Regex("^[A-Za-z0-9-]{1,64}$", RegexOptions.CultureInvariant);
        private static readonly IReadOnlyCollection<Capability> All = new[]
        {
            Capability.CreatePayment, Capability.VerifyCallback, Capability.GetStatus, Capability.Refund,
        };

        private readonly Http http;
        private readonly string baseUrl;
        private readonly string authKey;
        private readonly string merchantName;

        /// <summary>Creates the adapter.</summary>
        public GoldenPayProvider(GoldenPayOptions options)
        {
            if (string.IsNullOrEmpty(options?.AuthKey))
            {
                throw new ManatException(ManatErrorCode.InvalidRequest, "goldenpay: authKey is required");
            }
            if (string.IsNullOrEmpty(options!.MerchantName))
            {
                throw new ManatException(ManatErrorCode.InvalidRequest, "goldenpay: merchantName is required");
            }
            authKey = options.AuthKey;
            merchantName = options.MerchantName;
            baseUrl = (options.BaseUrl ?? (options.Environment == ProviderEnvironment.Test ? TestUrl : ProductionUrl)).TrimEnd('/');
            http = new Http(ProviderName, options);
        }

        /// <inheritdoc />
        public override string Name => ProviderName;

        /// <inheritdoc />
        public override IReadOnlyCollection<Capability> Capabilities => All;

        /// <summary>Maps <c>status.code</c> of a getPaymentResult reply.</summary>
        public static PaymentStatus MapStatus(long? code) => code switch
        {
            Success => PaymentStatus.Paid,
            813 or 818 => PaymentStatus.Pending, // not finished / in progress
            814 or 819 => PaymentStatus.Cancelled, // cancelled by the customer / payment page timed out
            815 or 816 => PaymentStatus.Failed,
            Reversed => PaymentStatus.Refunded,
            _ => throw new ManatException(ManatErrorCode.ProviderError, $"goldenpay: status code {(code?.ToString() ?? "null")} does not describe a payment", ProviderName),
        };

        /// <inheritdoc />
        public override async Task<CreatePaymentResult> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Intent == Intent.Authorize)
            {
                throw new ManatException(ManatErrorCode.UnsupportedOperation, "goldenpay: pre-authorisation is not supported", ProviderName);
            }
            RequireAzn(request.Amount!);
            // GoldenPay has no order id field; the description is the only text that reaches its panel.
            var description = TruncateBytes(request.Description ?? request.OrderId, MaxDescriptionBytes);
            var amount = request.Amount!.Amount;
            var body = new JsonObject
            {
                ["merchantName"] = merchantName,
                ["amount"] = amount,
                ["lang"] = (request.Language ?? Language.Az).ToCode(),
                ["description"] = description,
                ["redirectUrl"] = request.ReturnUrl,
                ["hashCode"] = Crypto.Md5Hex(authKey + merchantName + amount + description),
            };
            var response = await CallAsync("POST", "getPaymentKey", body, cancellationToken).ConfigureAwait(false);
            var key = Values.Get(response, "paymentKey");
            if (StatusCode(response) != Success || !Values.Truthy(key))
            {
                throw Error("payment was not created", response);
            }
            var paymentKey = Values.Text(key)!;
            return new CreatePaymentResult(paymentKey, PaymentStatus.Pending, NextAction.Redirect($"{baseUrl}/pay/{Uri.EscapeDataString(paymentKey)}"), response);
        }

        /// <inheritdoc />
        public override async Task<PaymentEvent> VerifyCallbackAsync(IncomingCallback callback, CancellationToken cancellationToken = default)
        {
            var key = callback.QueryValue("payment_key") ?? callback.QueryValue("paymentKey") ?? callback.BodyField("payment_key") ?? callback.BodyField("paymentKey");
            if (key == null || !PaymentKey.IsMatch(key))
            {
                throw new ManatException(ManatErrorCode.InvalidSignature, "goldenpay: redirect has no payment_key", ProviderName, raw: callback.ToDictionary());
            }
            // The redirect is unsigned; trust only what getPaymentResult says about this key.
            return PaymentEvent.FromState(await GetStatusAsync(key, cancellationToken).ConfigureAwait(false));
        }

        /// <inheritdoc />
        public override async Task<PaymentState> GetStatusAsync(string paymentId, CancellationToken cancellationToken = default)
        {
            if (!PaymentKey.IsMatch(paymentId))
            {
                throw new ManatException(ManatErrorCode.InvalidRequest, $"goldenpay: invalid paymentId \"{paymentId}\"");
            }
            var query = Form.Encode(new[]
            {
                new KeyValuePair<string, string>("payment_key", paymentId),
                new KeyValuePair<string, string>("hash_code", Crypto.Md5Hex(authKey + paymentId)),
            });
            var response = await CallAsync("GET", "getPaymentResult?" + query, null, cancellationToken).ConfigureAwait(false);
            var code = StatusCode(response);
            if (code == null || !PaymentCodes.Contains(code.Value))
            {
                throw Error("status is not available", response);
            }
            var key = Values.Get(response, "paymentKey");
            if (Values.Truthy(key) && !Equals(key, paymentId))
            {
                throw Error($"result is for payment {Values.Text(key)}, not {paymentId}", response);
            }
            var amount = Values.Integer(Values.Get(response, "amount"));
            return new PaymentState(paymentId, MapStatus(code), amount > 0 ? Money.Of(amount.Value, Currency.AZN) : null, raw: response);
        }

        /// <inheritdoc />
        public override async Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken cancellationToken = default)
        {
            var paymentId = request.PaymentId;
            if (request.Amount != null)
            {
                RequireAzn(request.Amount);
            }
            // reversePaymentKey needs an amount, and GoldenPay has no partial-refund status, so the payment is read first.
            var payment = await GetStatusAsync(paymentId, cancellationToken).ConfigureAwait(false);
            var amount = request.Amount ?? payment.Amount
                ?? throw new ManatException(ManatErrorCode.ProviderError, $"goldenpay: payment {paymentId} has no amount to refund", ProviderName, raw: payment.Raw);
            var hashCode = Crypto.Md5Hex(authKey + paymentId);
            var response = await CallAsync("POST", "reversePaymentKey", new JsonObject
            {
                ["paymentKey"] = paymentId,
                ["hashCode"] = hashCode,
                ["amount"] = amount.Amount,
                ["secretKey"] = Crypto.Md5Hex(hashCode + amount.Amount),
            }, cancellationToken).ConfigureAwait(false);
            // 817 (reversed) and 1 both mean success.
            var code = StatusCode(response);
            if (code != Reversed && code != Success)
            {
                throw Error("refund failed", response);
            }
            var partial = payment.Amount != null && amount.Amount < payment.Amount.Amount;
            return new RefundResult(paymentId, partial ? PaymentStatus.PartiallyRefunded : PaymentStatus.Refunded,
                raw: new JsonObject { ["refund"] = response, ["payment"] = payment.Raw });
        }

        private async Task<IReadOnlyDictionary<string, object?>> CallAsync(string method, string path, object? body, CancellationToken cancellationToken)
        {
            var (status, raw) = await http.JsonAsync(method, $"{baseUrl}/{path}", null, body, null, cancellationToken).ConfigureAwait(false);
            var data = Values.Map(raw);
            if (data == null || !Values.Truthy(Values.Get(data, "status")))
            {
                throw new ManatException(ManatErrorCode.ProviderError, $"goldenpay: unexpected response (HTTP {status})", ProviderName, raw: raw);
            }
            if (status >= 400)
            {
                throw Error($"HTTP {status}", data);
            }
            return data;
        }

        private static long? StatusCode(IReadOnlyDictionary<string, object?> response) => Values.Integer(Values.Get(response, "status", "code"));

        private static ManatException Error(string what, IReadOnlyDictionary<string, object?> response)
        {
            var code = Values.Get(response, "status", "code");
            var message = Values.Text(Values.Get(response, "status", "message"));
            // Code 1 is GoldenPay saying "success" (e.g. a result for another key), so only `what` describes the problem.
            var fromProvider = code != null && Values.Integer(code) != Success;
            var hasMessage = fromProvider && !string.IsNullOrEmpty(message);
            return new ManatException(ManatErrorCode.ProviderError, "goldenpay: " + (hasMessage ? message : what), ProviderName,
                fromProvider ? Values.Text(code) : null, hasMessage ? message : null, response);
        }

        private static void RequireAzn(Money money)
        {
            if (money.Currency != Currency.AZN)
            {
                throw new ManatException(ManatErrorCode.InvalidRequest, $"goldenpay: only AZN is supported, got {money.Currency}");
            }
        }

        /// <summary>Cuts to at most <paramref name="max"/> UTF-8 bytes without splitting a character.</summary>
        private static string TruncateBytes(string value, int max)
        {
            if (Encoding.UTF8.GetByteCount(value) <= max)
            {
                return value;
            }
            var output = new StringBuilder();
            var bytes = 0;
            for (var i = 0; i < value.Length; i++)
            {
                var length = char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]) ? 2 : 1;
                var character = value.Substring(i, length);
                var size = Encoding.UTF8.GetByteCount(character);
                if (bytes + size > max)
                {
                    break;
                }
                output.Append(character);
                bytes += size;
                i += length - 1;
            }
            return output.ToString();
        }
    }
}
