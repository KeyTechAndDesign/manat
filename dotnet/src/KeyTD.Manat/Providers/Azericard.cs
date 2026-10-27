using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using KeyTD.Manat.Internal;

namespace KeyTD.Manat.Providers
{
    /// <summary>Settings for <see cref="AzericardProvider"/>.</summary>
    public sealed class AzericardOptions : ProviderOptions
    {
        /// <summary>The terminal id.</summary>
        public string Terminal { get; init; } = "";

        /// <summary>The merchant's RSA private key as PEM text (PKCS#1 or PKCS#8), at least 2048 bits.</summary>
        public string PrivateKey { get; init; } = "";

        /// <summary>Azericard's public key as PEM text; required to verify callbacks.</summary>
        public string? GatewayPublicKey { get; init; }

        /// <summary>Shown on the payment page.</summary>
        public string MerchantName { get; init; } = "";

        /// <summary>The shop's URL.</summary>
        public string MerchantUrl { get; init; } = "";

        /// <summary>Default <see cref="ProviderEnvironment.Production"/>.</summary>
        public ProviderEnvironment Environment { get; init; }

        /// <summary>Overrides the environment's gateway URL.</summary>
        public string? GatewayUrl { get; init; }

        /// <summary>Optional EMAIL field.</summary>
        public string? Email { get; init; }

        /// <summary>Optional COUNTRY field.</summary>
        public string? Country { get; init; }

        /// <summary>Optional MERCH_GMT field.</summary>
        public string? MerchantGmt { get; init; }

        /// <summary>Field orders per message type ("purchase", "completion", "reversal", "callback", "status") that differ on your terminal.</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<string>>? MacFields { get; init; }

        /// <summary>For tests: the current time, used in TIMESTAMP and the 24-hour refund rule.</summary>
        public Func<DateTimeOffset>? Clock { get; init; }

        /// <summary>For tests: the NONCE generator.</summary>
        public Func<string>? Nonce { get; init; }
    }

    /// <summary>
    /// Azericard (form POST, RSA-signed).
    /// Each message is signed over the length-prefixed values of a fixed field list (macFields).
    /// The callback signature does not cover ORDER or ACTION, so only a signed APPROVAL counts as approved.
    /// </summary>
    public sealed class AzericardProvider : PaymentProvider
    {
        private const string ProviderName = "azericard";
        private const string TestUrl = "https://testmpi.3dsecure.az/cgi-bin/cgi_link";
        private const string ProductionUrl = "https://mpi.3dsecure.az/cgi-bin/cgi_link";
        private const long DaySeconds = 86400;

        /// <summary>MAC field orders per message type.</summary>
        public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> DefaultMacFields = new Dictionary<string, IReadOnlyList<string>>
        {
            ["purchase"] = new[] { "AMOUNT", "CURRENCY", "TERMINAL", "TRTYPE", "TIMESTAMP", "NONCE", "MERCH_URL" },
            ["completion"] = new[] { "AMOUNT", "CURRENCY", "TERMINAL", "TRTYPE", "ORDER", "RRN", "INT_REF" },
            ["reversal"] = new[] { "AMOUNT", "CURRENCY", "TERMINAL", "TRTYPE", "ORDER", "RRN", "INT_REF" },
            ["callback"] = new[] { "AMOUNT", "TERMINAL", "APPROVAL", "RRN", "INT_REF" },
            ["status"] = new[] { "ORDER", "TERMINAL", "TRTYPE", "TIMESTAMP", "NONCE" },
        };

        private static readonly Dictionary<string, string> ActionMessages = new Dictionary<string, string>
        {
            ["0"] = "Approved",
            ["1"] = "Duplicate transaction",
            ["2"] = "Transaction declined",
            ["3"] = "Transaction processing error",
            ["6"] = "Repeat of a declined transaction",
            ["7"] = "Repeat of a transaction with an authentication error",
            ["8"] = "Repeat of a transaction that terminated without a response",
        };

        private static readonly Regex Order = new Regex(@"^\d{6,32}$", RegexOptions.CultureInvariant);
        private static readonly Regex HexText = new Regex("^[0-9a-fA-F]+$", RegexOptions.CultureInvariant);
        private static readonly Regex ThreeDigits = new Regex(@"^\d{3}$", RegexOptions.CultureInvariant);
        private static readonly IReadOnlyCollection<Capability> All = new[]
        {
            Capability.CreatePayment, Capability.VerifyCallback, Capability.GetStatus, Capability.Refund, Capability.Capture, Capability.Cancel,
        };

        private readonly Http http;
        private readonly string url;
        private readonly AzericardOptions options;
        private readonly RSA privateKey;
        private readonly RSA? gatewayKey;
        private readonly Dictionary<string, IReadOnlyList<string>> macFields;
        private readonly Func<DateTimeOffset> clock;
        private readonly Func<string> nonce;

        /// <summary>Creates the adapter.</summary>
        public AzericardProvider(AzericardOptions options)
        {
            this.options = options ?? throw new ManatException(ManatErrorCode.InvalidRequest, "azericard: options are required");
            if (string.IsNullOrEmpty(options.Terminal))
            {
                throw new ManatException(ManatErrorCode.InvalidRequest, "azericard: terminal is required");
            }
            if (string.IsNullOrEmpty(options.MerchantName) || string.IsNullOrEmpty(options.MerchantUrl))
            {
                throw new ManatException(ManatErrorCode.InvalidRequest, "azericard: merchantName and merchantUrl are required");
            }
            if (string.IsNullOrEmpty(options.PrivateKey))
            {
                throw new ManatException(ManatErrorCode.InvalidRequest, "azericard: privateKey (PEM) is required");
            }
            try
            {
                privateKey = Crypto.RsaPrivateKey(options.PrivateKey);
            }
            catch (CryptographicException error)
            {
                throw new ManatException(ManatErrorCode.InvalidRequest, "azericard: privateKey is not a valid PEM key", ProviderName, inner: error);
            }
            if (Crypto.KeyBits(privateKey) < 2048)
            {
                throw new ManatException(ManatErrorCode.InvalidRequest, "azericard: privateKey must be an RSA key of at least 2048 bits");
            }
            if (!string.IsNullOrEmpty(options.GatewayPublicKey))
            {
                try
                {
                    gatewayKey = Crypto.RsaPublicKey(options.GatewayPublicKey!);
                }
                catch (CryptographicException error)
                {
                    throw new ManatException(ManatErrorCode.InvalidRequest, "azericard: gatewayPublicKey is not a valid PEM key", ProviderName, inner: error);
                }
            }
            url = options.GatewayUrl ?? (options.Environment == ProviderEnvironment.Test ? TestUrl : ProductionUrl);
            macFields = new Dictionary<string, IReadOnlyList<string>>();
            foreach (var pair in DefaultMacFields)
            {
                macFields[pair.Key] = pair.Value;
            }
            foreach (var pair in options.MacFields ?? new Dictionary<string, IReadOnlyList<string>>())
            {
                macFields[pair.Key] = pair.Value;
            }
            clock = options.Clock ?? (Func<DateTimeOffset>)(() => DateTimeOffset.UtcNow);
            nonce = options.Nonce ?? (Func<string>)DefaultNonce;
            http = new Http(ProviderName, options);
        }

        /// <inheritdoc />
        public override string Name => ProviderName;

        /// <inheritdoc />
        public override IReadOnlyCollection<Capability> Capabilities => All;

        /// <summary>Length-prefixed MAC source: "11.48" → "511.48", empty or missing → "-". Lengths are in bytes.</summary>
        public static string MacSource(IReadOnlyDictionary<string, string> fields, IEnumerable<string> order)
        {
            var output = new StringBuilder();
            foreach (var name in order)
            {
                fields.TryGetValue(name, out var value);
                if (string.IsNullOrEmpty(value))
                {
                    output.Append('-');
                }
                else
                {
                    output.Append(Encoding.UTF8.GetByteCount(value)).Append(value);
                }
            }
            return output.ToString();
        }

        /// <summary>GMT timestamp YYYYMMDDHHMMSS.</summary>
        public static string Timestamp(DateTimeOffset time) => time.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

        /// <summary>Status of one transaction type from a TRTYPE 90 reply.</summary>
        public static PaymentStatus MapStatusResponse(IReadOnlyDictionary<string, object?> data, string tranTrtype)
        {
            if (Approved(data))
            {
                return tranTrtype == "0" ? PaymentStatus.Authorized : PaymentStatus.Paid;
            }
            return Equals(Values.Get(data, "responseCode"), "-40") ? PaymentStatus.Pending : PaymentStatus.Failed; // -40: form in progress
        }

        /// <inheritdoc />
        public override Task<CreatePaymentResult> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken = default)
        {
            var orderId = request.OrderId;
            if (!Order.IsMatch(orderId))
            {
                throw new ManatException(ManatErrorCode.InvalidRequest,
                    $"azericard: orderId must be 6–32 digits (got \"{orderId}\"); its last 6 digits must be unique per terminal per day");
            }
            var amount = request.Amount!;
            var fields = new List<KeyValuePair<string, string>>();
            void Put(string name, string? value)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    fields.Add(new KeyValuePair<string, string>(name, value!));
                }
            }
            Put("AMOUNT", amount.ToDecimalString());
            Put("CURRENCY", amount.Currency.ToString());
            Put("ORDER", orderId);
            Put("DESC", Values.Truncate(request.Description ?? $"Order {orderId}", 50));
            Put("MERCH_NAME", Values.Truncate(options.MerchantName, 50));
            Put("MERCH_URL", options.MerchantUrl);
            Put("TERMINAL", options.Terminal);
            Put("EMAIL", options.Email);
            Put("TRTYPE", request.Intent == Intent.Authorize ? "0" : "1");
            Put("COUNTRY", options.Country);
            Put("MERCH_GMT", options.MerchantGmt);
            Put("TIMESTAMP", Timestamp(clock()));
            Put("NONCE", nonce());
            Put("BACKREF", request.CallbackUrl ?? request.ReturnUrl);
            Put("LANG", (request.Language ?? Language.Az).ToCode());
            fields.Add(new KeyValuePair<string, string>("P_SIGN", Sign(fields, "purchase")));
            var raw = new JsonObject { ["fields"] = ToObject(fields) };
            return Task.FromResult(new CreatePaymentResult(orderId, PaymentStatus.Pending, NextAction.PostForm(url, fields), raw));
        }

        /// <inheritdoc />
        public override Task<PaymentEvent> VerifyCallbackAsync(IncomingCallback callback, CancellationToken cancellationToken = default)
        {
            if (gatewayKey == null)
            {
                throw new ManatException(ManatErrorCode.InvalidRequest, "azericard: gatewayPublicKey is required to verify callbacks; never accept them unverified");
            }
            var fields = ReadFields(callback);
            fields.TryGetValue("P_SIGN", out var signature);
            var signatureBytes = signature != null && HexText.IsMatch(signature) ? Crypto.Unhex(signature) : null;
            if (signatureBytes == null)
            {
                throw new ManatException(ManatErrorCode.InvalidSignature, "azericard: callback has no valid P_SIGN", ProviderName, raw: fields);
            }
            if (!Crypto.VerifySha256(gatewayKey, MacSource(fields, macFields["callback"]), signatureBytes))
            {
                throw new ManatException(ManatErrorCode.InvalidSignature, "azericard: callback P_SIGN does not match", ProviderName, raw: fields);
            }
            fields.TryGetValue("TERMINAL", out var terminal);
            if (terminal != options.Terminal)
            {
                throw new ManatException(ManatErrorCode.InvalidSignature, $"azericard: callback is for terminal {terminal}, not {options.Terminal}", ProviderName, raw: fields);
            }
            if (!fields.TryGetValue("ORDER", out var order) || string.IsNullOrEmpty(order))
            {
                throw new ManatException(ManatErrorCode.InvalidSignature, "azericard: callback has no ORDER", ProviderName, raw: fields);
            }

            var amount = ParseAmount(Field(fields, "AMOUNT"), Field(fields, "CURRENCY"));
            // ACTION is unsigned; the signed APPROVAL code is what proves an approval.
            var approved = Field(fields, "ACTION") == "0" && !string.IsNullOrEmpty(Field(fields, "APPROVAL"));
            string? providerRef = null;
            if (approved && !string.IsNullOrEmpty(Field(fields, "RRN")) && !string.IsNullOrEmpty(Field(fields, "INT_REF")) && amount != null
                && !string.IsNullOrEmpty(Field(fields, "TIMESTAMP")))
            {
                providerRef = EncodeRef(fields["RRN"], fields["INT_REF"], amount, fields["TIMESTAMP"], Field(fields, "TRTYPE") ?? "1");
            }
            var status = approved ? StatusForTrtype(Field(fields, "TRTYPE")) : PaymentStatus.Failed;
            return Task.FromResult(new PaymentEvent(order, status, order, amount, providerRef, ToObject(fields)));
        }

        /// <summary>TRTYPE 90 answers for one transaction type, so the purchase (or pre-auth), completion and reversals are each queried.</summary>
        public override async Task<PaymentState> GetStatusAsync(string paymentId, CancellationToken cancellationToken = default)
        {
            if (!Order.IsMatch(paymentId))
            {
                throw new ManatException(ManatErrorCode.InvalidRequest, $"azericard: paymentId must be the numeric ORDER, got \"{paymentId}\"");
            }
            var trtype = "1";
            var data = await QueryStatusAsync(paymentId, trtype, cancellationToken).ConfigureAwait(false);
            if (Equals(Values.Get(data, "responseCode"), "-24"))
            {
                trtype = "0";
                data = await QueryStatusAsync(paymentId, trtype, cancellationToken).ConfigureAwait(false);
            }
            var status = MapStatusResponse(data, trtype);
            var amount = ParseAmount(Values.Text(Values.Get(data, "amount")), Values.Text(Values.Get(data, "currency")));
            var raw = new JsonObject { [trtype] = data };
            if ((status != PaymentStatus.Paid && status != PaymentStatus.Authorized) || amount == null)
            {
                return new PaymentState(paymentId, status, amount, raw: raw);
            }

            var approvedRelated = new Dictionary<string, IReadOnlyDictionary<string, object?>>();
            foreach (var type in trtype == "0" ? new[] { "21", "22", "24" } : new[] { "22", "24" })
            {
                var related = await QueryStatusAsync(paymentId, type, cancellationToken).ConfigureAwait(false);
                raw[type] = related;
                if (Approved(related))
                {
                    approvedRelated[type] = related;
                }
            }
            if (status == PaymentStatus.Authorized && approvedRelated.ContainsKey("21"))
            {
                status = PaymentStatus.Paid;
            }
            var refunded = ApprovedAmount(approvedRelated, "22", amount.Currency) + ApprovedAmount(approvedRelated, "24", amount.Currency);
            Money? refundedAmount = null;
            if (refunded > 0)
            {
                refundedAmount = Money.Of(refunded, amount.Currency);
                status = status == PaymentStatus.Authorized
                    ? PaymentStatus.Cancelled
                    : refunded >= amount.Amount ? PaymentStatus.Refunded : PaymentStatus.PartiallyRefunded;
            }
            string? providerRef = null;
            var paidAt = Values.Get(data, "tranDate") != null ? BakuTimeToGmt(Values.Text(Values.Get(data, "tranDate"))) : Values.Text(Values.Get(data, "timestamp"));
            var rrn = Values.Text(Values.Get(data, "rrn"));
            var intRef = Values.Text(Values.Get(data, "intRef"));
            if (!string.IsNullOrEmpty(rrn) && !string.IsNullOrEmpty(intRef) && !string.IsNullOrEmpty(paidAt))
            {
                providerRef = EncodeRef(rrn!, intRef!, amount, paidAt!, approvedRelated.ContainsKey("21") ? "21" : trtype);
            }
            return new PaymentState(paymentId, status, amount, refundedAmount, providerRef, raw);
        }

        /// <inheritdoc />
        public override async Task<PaymentState> CaptureAsync(PaymentAction action, CancellationToken cancellationToken = default)
        {
            var reference = RequireRef(action.ProviderRef);
            var amount = action.Amount ?? reference.Amount;
            await SendAsync("completion", "21", action.PaymentId, reference, amount, cancellationToken).ConfigureAwait(false);
            return new PaymentState(action.PaymentId, PaymentStatus.Paid, amount, null, EncodeRef(reference.Rrn, reference.IntRef, amount, reference.Timestamp, "21"));
        }

        /// <inheritdoc />
        public override async Task<PaymentState> CancelAsync(PaymentAction action, CancellationToken cancellationToken = default)
        {
            var reference = RequireRef(action.ProviderRef);
            await SendAsync("reversal", "22", action.PaymentId, reference, reference.Amount, cancellationToken).ConfigureAwait(false);
            return new PaymentState(action.PaymentId, PaymentStatus.Cancelled, reference.Amount);
        }

        /// <inheritdoc />
        public override async Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken cancellationToken = default)
        {
            var reference = RequireRef(request.ProviderRef);
            var amount = request.Amount ?? reference.Amount;
            if (amount.Currency != reference.Amount.Currency || amount.Amount > reference.Amount.Amount)
            {
                throw new ManatException(ManatErrorCode.InvalidRequest, "azericard: refund amount exceeds the paid amount or uses another currency");
            }
            // Online reversal (22) works once, before settlement; otherwise it is offline (24).
            var paidAt = ParseTimestamp(reference.Timestamp);
            var trtype = "24";
            if (paidAt != null && clock().ToUnixTimeSeconds() - paidAt.Value.ToUnixTimeSeconds() < DaySeconds)
            {
                trtype = Approved(await QueryStatusAsync(request.PaymentId, "22", cancellationToken).ConfigureAwait(false)) ? "24" : "22";
            }
            var action = await SendAsync("reversal", trtype, request.PaymentId, reference, amount, cancellationToken).ConfigureAwait(false);
            var status = amount.Amount < reference.Amount.Amount ? PaymentStatus.PartiallyRefunded : PaymentStatus.Refunded;
            return new RefundResult(request.PaymentId, status, raw: new JsonObject { ["trtype"] = trtype, ["action"] = action });
        }

        private async Task<IReadOnlyDictionary<string, object?>> QueryStatusAsync(string order, string tranTrtype, CancellationToken cancellationToken)
        {
            var fields = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("TRAN_TRTYPE", tranTrtype),
                new KeyValuePair<string, string>("ORDER", order),
                new KeyValuePair<string, string>("TERMINAL", options.Terminal),
                new KeyValuePair<string, string>("TRTYPE", "90"),
                new KeyValuePair<string, string>("TIMESTAMP", Timestamp(clock())),
                new KeyValuePair<string, string>("NONCE", nonce()),
            };
            fields.Add(new KeyValuePair<string, string>("P_SIGN", Sign(fields, "status")));
            var (status, raw) = await http.JsonAsync("POST", url, null, null, fields, cancellationToken).ConfigureAwait(false);
            var data = Values.Map(raw);
            if (status >= 400 || data == null)
            {
                throw new ManatException(ManatErrorCode.ProviderError, $"azericard: status request failed (HTTP {status})", ProviderName, raw: raw);
            }
            return data;
        }

        private async Task<string> SendAsync(string message, string trtype, string order, Ref reference, Money amount, CancellationToken cancellationToken)
        {
            var fields = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("AMOUNT", amount.ToDecimalString()),
                new KeyValuePair<string, string>("CURRENCY", amount.Currency.ToString()),
                new KeyValuePair<string, string>("ORDER", order),
                new KeyValuePair<string, string>("RRN", reference.Rrn),
                new KeyValuePair<string, string>("INT_REF", reference.IntRef),
                new KeyValuePair<string, string>("TERMINAL", options.Terminal),
                new KeyValuePair<string, string>("TRTYPE", trtype),
                new KeyValuePair<string, string>("TIMESTAMP", Timestamp(clock())),
                new KeyValuePair<string, string>("NONCE", nonce()),
            };
            fields.Add(new KeyValuePair<string, string>("P_SIGN", Sign(fields, message)));
            var response = await http.SendAsync("POST", url, null, null, fields, cancellationToken).ConfigureAwait(false);
            var action = response.Body.Trim();
            if (response.Status >= 400)
            {
                throw new ManatException(ManatErrorCode.ProviderError, $"azericard: HTTP {response.Status}", ProviderName, raw: response.Body);
            }
            if (action != "0")
            {
                var text = ActionMessages.TryGetValue(action, out var known) ? known : $"Unexpected response \"{Cut(action, 100)}\"";
                throw new ManatException(ManatErrorCode.ProviderError, $"azericard: {text}", ProviderName, Cut(action, 20), text, response.Body);
            }
            return action;
        }

        private string Sign(IEnumerable<KeyValuePair<string, string>> fields, string message)
        {
            var map = new Dictionary<string, string>();
            foreach (var field in fields)
            {
                map[field.Key] = field.Value;
            }
            return Crypto.Hex(Crypto.SignSha256(privateKey, MacSource(map, macFields[message])));
        }

        private static bool Approved(IReadOnlyDictionary<string, object?> data) =>
            Equals(Values.Get(data, "actionCode"), "0") && Equals(Values.Get(data, "responseCode"), "00");

        private static long ApprovedAmount(Dictionary<string, IReadOnlyDictionary<string, object?>> approved, string type, Currency currency)
        {
            if (!approved.TryGetValue(type, out var data))
            {
                return 0;
            }
            var code = Values.Get(data, "currency") != null ? Values.Text(Values.Get(data, "currency")) : currency.ToString();
            var money = ParseAmount(Values.Text(Values.Get(data, "amount")), code);
            return money != null && money.Currency == currency ? money.Amount : 0;
        }

        /// <summary>"2026.10.26 19:06:40" in Baku time (UTC+4, no DST) → "20261026150640" GMT.</summary>
        private static string? BakuTimeToGmt(string? value) =>
            DateTime.TryParseExact(value, "yyyy.MM.dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
                ? local.AddHours(-4).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)
                : null;

        private static PaymentStatus StatusForTrtype(string? trtype) => trtype switch
        {
            "0" => PaymentStatus.Authorized,
            "22" or "24" => PaymentStatus.Refunded,
            _ => PaymentStatus.Paid,
        };

        private static string EncodeRef(string rrn, string intRef, Money amount, string timestamp, string trtype) =>
            string.Join("|", "az1", rrn, intRef, amount.ToDecimalString(), amount.Currency.ToString(), timestamp, trtype);

        /// <summary>What providerRef carries: the payment's RRN and INT_REF, its amount and its GMT timestamp.</summary>
        private sealed class Ref
        {
            public Ref(string rrn, string intRef, Money amount, string timestamp)
            {
                Rrn = rrn;
                IntRef = intRef;
                Amount = amount;
                Timestamp = timestamp;
            }

            public string Rrn { get; }

            public string IntRef { get; }

            public Money Amount { get; }

            public string Timestamp { get; }
        }

        private static Ref RequireRef(string? value)
        {
            var parts = value?.Split('|') ?? Array.Empty<string>();
            if (parts.Length != 7 || parts[0] != "az1" || parts[1].Length == 0 || parts[2].Length == 0)
            {
                throw new ManatException(ManatErrorCode.InvalidRequest,
                    "azericard: providerRef from the payment callback is required (store event.providerRef with the order)");
            }
            return new Ref(parts[1], parts[2], Money.FromDecimal(parts[3], CurrencyOf(parts[4])), parts[5]);
        }

        private static Money? ParseAmount(string? amount, string? code)
        {
            if (string.IsNullOrEmpty(amount) || string.IsNullOrEmpty(code))
            {
                return null;
            }
            var alpha = ThreeDigits.IsMatch(code) ? Currencies.FromNumeric(code)?.ToString() : code;
            return alpha == null ? null : Money.FromDecimal(amount, CurrencyOf(alpha));
        }

        private static Currency CurrencyOf(string code) =>
            Currencies.Find(code) ?? throw new ManatException(ManatErrorCode.ProviderError, $"azericard: unsupported currency \"{code}\"", ProviderName);

        private static DateTimeOffset? ParseTimestamp(string value) =>
            DateTime.TryParseExact(value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)
                ? new DateTimeOffset(time, TimeSpan.Zero)
                : null;

        /// <summary>Upper-case keys, trimmed values; query first, body after.</summary>
        private static Dictionary<string, string> ReadFields(IncomingCallback callback)
        {
            var output = new Dictionary<string, string>();
            foreach (var pair in callback.Query)
            {
                if (pair.Value.Count > 0)
                {
                    output[pair.Key.ToUpperInvariant()] = pair.Value[0].Trim();
                }
            }
            if (callback.BodyText != null)
            {
                foreach (var pair in Form.Decode(callback.BodyText))
                {
                    output[pair.Key.ToUpperInvariant()] = pair.Value.Trim();
                }
            }
            else if (callback.BodyFields != null)
            {
                foreach (var pair in callback.BodyFields)
                {
                    var value = Values.FirstString(pair.Value);
                    if (value != null)
                    {
                        output[pair.Key.ToUpperInvariant()] = value.Trim();
                    }
                }
            }
            return output;
        }

        private static JsonObject ToObject(IEnumerable<KeyValuePair<string, string>> fields)
        {
            var output = new JsonObject();
            foreach (var field in fields)
            {
                output[field.Key] = field.Value;
            }
            return output;
        }

        private static string? Field(IReadOnlyDictionary<string, string> fields, string name) => fields.TryGetValue(name, out var value) ? value : null;

        private static string Cut(string value, int max) => value.Length > max ? value.Substring(0, max) : value;

        private static string DefaultNonce()
        {
            var bytes = new byte[16];
            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(bytes);
            }
            return Crypto.Hex(bytes);
        }
    }
}
