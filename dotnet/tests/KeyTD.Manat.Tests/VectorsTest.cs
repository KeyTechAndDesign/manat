using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using KeyTD.Manat.Internal;
using KeyTD.Manat.Providers;
using Xunit;

namespace KeyTD.Manat.Tests
{
    /// <summary>Runs every vector in spec/vectors, the same files the other languages run.</summary>
    public class VectorsTest
    {
        private static readonly string Root = FindVectors();

        public static IEnumerable<object[]> Files() =>
            Directory.GetDirectories(Root).OrderBy(d => d, StringComparer.Ordinal)
                .SelectMany(dir => Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
                .Select(file => new object[] { Path.GetFileName(Path.GetDirectoryName(file))! + "/" + Path.GetFileName(file) });

        [Fact]
        public void FindsVectors() => Assert.True(Files().Count() > 100);

        [Theory]
        [MemberData(nameof(Files))]
        public async Task Vector(string name)
        {
            var vector = Values.Map(Json.Parse(File.ReadAllText(Path.Combine(Root, name))))!;
            var provider = (string)vector["provider"]!;
            var operation = (string)vector["operation"]!;
            var config = Values.Map(vector["config"])!;

            if (operation == "mapStatus")
            {
                foreach (var testCase in Values.List(vector["cases"])!)
                {
                    var input = Values.Map(Values.Get(testCase, "input"))!;
                    Assert.Equal(Values.Get(testCase, "expected", "status"), MapStatus(provider, input).ToValue());
                }
                return;
            }
            if (operation == "macSource")
            {
                foreach (var testCase in Values.List(vector["cases"])!)
                {
                    var fields = Values.Map(Values.Get(testCase, "input", "fields"))!.ToDictionary(p => p.Key, p => (string)p.Value!);
                    var order = Values.List(Values.Get(testCase, "input", "order"))!.Select(f => (string)f!);
                    Assert.Equal(Values.Get(testCase, "expected", "source"), AzericardProvider.MacSource(fields, order));
                }
                return;
            }
            if (operation == "sign")
            {
                foreach (var testCase in Values.List(vector["cases"])!)
                {
                    var (data, signature) = EpointProvider.Encode((string)config["privateKey"]!, (string)Values.Get(testCase, "input", "json")!);
                    Assert.Equal(Values.Get(testCase, "expected", "data"), data);
                    Assert.Equal(Values.Get(testCase, "expected", "signature"), signature);
                }
                return;
            }

            var handler = new ScriptedHandler(Values.List(vector.TryGetValue("http", out var http) ? http : null));
            var client = new ManatClient(Provider(provider, config, new HttpClient(handler)));
            var request = Values.Map(vector.TryGetValue("input", out var input2) ? input2 : null) ?? new JsonObject();
            var expected = Values.Map(vector.TryGetValue("expected", out var e) ? e : null) ?? new JsonObject();
            if (expected.ContainsKey("error"))
            {
                var error = await Assert.ThrowsAsync<ManatException>(() => Invoke(client, operation, request));
                Assert.True(Equals(expected["error"], error.Code.ToValue()), $"expected {expected["error"]}, got {error.Code.ToValue()}: {error.Message}");
                if (Values.Get(expected, "providerCode") is string code)
                {
                    Assert.Equal(code, error.ProviderCode);
                }
            }
            else
            {
                Expect.Subset(expected, await Invoke(client, operation, request), "result");
            }
            handler.AssertDone();
        }

        /// <summary>Calls the operation with the vector's JSON input turned into the typed request, and returns the result's dictionary.</summary>
        private static async Task<IReadOnlyDictionary<string, object?>> Invoke(ManatClient client, string operation, IReadOnlyDictionary<string, object?> input)
        {
            switch (operation)
            {
                case "createPayment":
                    return (await client.CreatePaymentAsync(new CreatePaymentRequest
                    {
                        OrderId = (string)input["orderId"]!,
                        Amount = ToMoney(Values.Get(input, "amount")),
                        ReturnUrl = (string)input["returnUrl"]!,
                        CallbackUrl = Values.Get(input, "callbackUrl") as string,
                        Description = Values.Get(input, "description") as string,
                        Language = Values.Get(input, "language") is string language ? (Language)Enum.Parse(typeof(Language), language, true) : null,
                        Intent = Equals(Values.Get(input, "intent"), "authorize") ? Intent.Authorize : Intent.Purchase,
                        CustomerEmail = Values.Get(input, "customer", "email") as string,
                        CustomerPhone = Values.Get(input, "customer", "phone") as string,
                        Metadata = Values.Map(Values.Get(input, "metadata"))?.ToDictionary(p => p.Key, p => (string)p.Value!),
                    })).ToDictionary();
                case "verifyCallback":
                    var callback = new IncomingCallback((string)input["method"]!);
                    if (Values.Map(Values.Get(input, "query")) is { } query)
                    {
                        callback = callback.WithQuery(query.Select(p => new KeyValuePair<string, string[]>(p.Key, Strings(p.Value))));
                    }
                    if (Values.Get(input, "body") is string text)
                    {
                        callback = callback.WithBody(text);
                    }
                    else if (Values.Map(Values.Get(input, "body")) is { } fields)
                    {
                        callback = callback.WithBody(fields);
                    }
                    if (Values.Map(Values.Get(input, "headers")) is { } headers)
                    {
                        callback = callback.WithHeaders(headers.Select(p => new KeyValuePair<string, string[]>(p.Key, Strings(p.Value))));
                    }
                    return (await client.VerifyCallbackAsync(callback)).ToDictionary();
                case "getStatus":
                    return (await client.GetStatusAsync((string)input["paymentId"]!)).ToDictionary();
                case "refund":
                    return (await client.RefundAsync(new RefundRequest
                    {
                        PaymentId = (string)input["paymentId"]!,
                        Amount = Values.Get(input, "amount") == null ? null : ToMoney(Values.Get(input, "amount")),
                        ProviderRef = Values.Get(input, "providerRef") as string,
                    })).ToDictionary();
                case "capture":
                    return (await client.CaptureAsync(Action(input))).ToDictionary();
                case "cancel":
                    return (await client.CancelAsync(Action(input))).ToDictionary();
                default:
                    throw new ArgumentException("unknown operation " + operation);
            }
        }

        private static PaymentAction Action(IReadOnlyDictionary<string, object?> input) => new PaymentAction
        {
            PaymentId = (string)input["paymentId"]!,
            Amount = Values.Get(input, "amount") == null ? null : ToMoney(Values.Get(input, "amount")),
            ProviderRef = Values.Get(input, "providerRef") as string,
        };

        private static string[] Strings(object? value) => value switch
        {
            string text => new[] { text },
            IReadOnlyList<object?> list => list.OfType<string>().ToArray(),
            _ => Array.Empty<string>(),
        };

        /// <summary>
        /// <c>{amount, currency}</c> → Money. Money holds a long, so a fractional amount (the float vectors) cannot reach the library
        /// at all; the type system is the check, and it is reported the way the other languages report it.
        /// </summary>
        private static Money ToMoney(object? value)
        {
            if (!(Values.Get(value, "amount") is long amount))
            {
                throw new ManatException(ManatErrorCode.InvalidRequest, $"amount must be an integer in minor units, got {Json.Stringify(value)}");
            }
            var currency = Currencies.Find(Values.Get(value, "currency") as string)
                ?? throw new ManatException(ManatErrorCode.InvalidRequest, $"currency {Values.Get(value, "currency")} is not supported");
            return Money.Of(amount, currency);
        }

        private static PaymentStatus MapStatus(string provider, IReadOnlyDictionary<string, object?> input) => provider switch
        {
            "kapitalbank" => KapitalBankProvider.MapStatus(input),
            "payriff" => PayriffProvider.MapStatus(Values.Get(input, "paymentStatus") as string),
            "epoint" => EpointProvider.MapStatus(Values.Get(input, "status") as string),
            "goldenpay" => GoldenPayProvider.MapStatus(Values.Integer(Values.Get(input, "status", "code"))),
            "yigim" => YigimProvider.MapStatus(Values.Get(input, "status") as string),
            _ => throw new ArgumentException(provider + " has no mapStatus"),
        };

        /// <summary>
        /// Builds the adapter from the JSON-friendly vector config, the same way in every language: <c>&lt;name&gt;File</c> is read relative
        /// to the provider's vector folder, <c>clock</c> (ISO time) and <c>nonce</c> (string) become fixed generators.
        /// </summary>
        private static IPaymentProvider Provider(string name, IReadOnlyDictionary<string, object?> raw, HttpClient http)
        {
            var config = new Dictionary<string, object?>();
            foreach (var pair in raw)
            {
                if (pair.Key.EndsWith("File", StringComparison.Ordinal) && pair.Value is string file)
                {
                    config[pair.Key.Substring(0, pair.Key.Length - 4)] = File.ReadAllText(Path.Combine(Root, name, file));
                }
                else
                {
                    config[pair.Key] = pair.Value;
                }
            }
            string? Text(string key) => config.TryGetValue(key, out var value) ? value as string : null;
            var environment = Text("environment") == "test" ? ProviderEnvironment.Test : ProviderEnvironment.Production;
            switch (name)
            {
                case "kapitalbank":
                    return new KapitalBankProvider(new KapitalBankOptions
                    {
                        HttpClient = http, Username = Text("username") ?? "", Password = Text("password") ?? "", Environment = environment, BaseUrl = Text("baseUrl"),
                    });
                case "payriff":
                    return new PayriffProvider(new PayriffOptions { HttpClient = http, SecretKey = Text("secretKey") ?? "", BaseUrl = Text("baseUrl") });
                case "epoint":
                    return new EpointProvider(new EpointOptions
                    {
                        HttpClient = http, PublicKey = Text("publicKey") ?? "", PrivateKey = Text("privateKey") ?? "", BaseUrl = Text("baseUrl"),
                    });
                case "goldenpay":
                    return new GoldenPayProvider(new GoldenPayOptions
                    {
                        HttpClient = http, AuthKey = Text("authKey") ?? "", MerchantName = Text("merchantName") ?? "", Environment = environment, BaseUrl = Text("baseUrl"),
                    });
                case "yigim":
                    return new YigimProvider(new YigimOptions
                    {
                        HttpClient = http, Merchant = Text("merchant") ?? "", ApiKey = Text("apiKey") ?? "", Biller = Text("biller") ?? "",
                        Template = Text("template"), Environment = environment, BaseUrl = Text("baseUrl"),
                    });
                case "azericard":
                    var clock = Text("clock");
                    var nonce = Text("nonce");
                    return new AzericardProvider(new AzericardOptions
                    {
                        HttpClient = http,
                        Terminal = Text("terminal") ?? "",
                        PrivateKey = Text("privateKey") ?? "",
                        GatewayPublicKey = Text("gatewayPublicKey"),
                        MerchantName = Text("merchantName") ?? "",
                        MerchantUrl = Text("merchantUrl") ?? "",
                        Environment = environment,
                        GatewayUrl = Text("gatewayUrl"),
                        Email = Text("email"),
                        Country = Text("country"),
                        MerchantGmt = Text("merchantGmt"),
                        MacFields = Values.Map(config.TryGetValue("macFields", out var mac) ? mac : null)?
                            .ToDictionary(p => p.Key, p => (IReadOnlyList<string>)Values.List(p.Value)!.Select(f => (string)f!).ToList()),
                        Clock = clock == null ? null : () => DateTimeOffset.Parse(clock, System.Globalization.CultureInfo.InvariantCulture),
                        Nonce = nonce == null ? null : () => nonce,
                    });
                default:
                    throw new ArgumentException("no adapter registered for " + name);
            }
        }

        private static string FindVectors()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "spec", "vectors");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
            throw new DirectoryNotFoundException("spec/vectors not found above " + AppContext.BaseDirectory);
        }
    }
}
