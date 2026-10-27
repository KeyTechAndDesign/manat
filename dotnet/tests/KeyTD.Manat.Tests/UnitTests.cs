using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace KeyTD.Manat.Tests
{
    public class MoneyTest
    {
        [Fact]
        public void FormatsMinorUnitsWithoutFloatMaths()
        {
            Assert.Equal("25.50", Money.Of(2550, Currency.AZN).ToDecimalString());
            Assert.Equal("0.05", Money.Of(5, Currency.AZN).ToDecimalString());
            Assert.Equal("1.00", Money.Of(100, Currency.USD).ToDecimalString());
            Assert.Equal("1234567.89", Money.Of(123456789, Currency.AZN).ToDecimalString());
        }

        [Fact]
        public void ParsesDecimalsIntoMinorUnits()
        {
            Assert.Equal(Money.Of(2550, Currency.AZN), Money.Azn("25.5"));
            Assert.Equal(Money.Of(2550, Currency.AZN), Money.FromDecimal(25.50m, Currency.AZN));
            Assert.Equal(Money.Of(5, Currency.AZN), Money.Azn("0.05"));
            Assert.Equal(Money.Of(1000, Currency.EUR), Money.FromDecimal("10", Currency.EUR));
            Assert.Equal(Money.Of(500, Currency.AZN), Money.FromDecimal(5.000m, Currency.AZN));
        }

        [Theory]
        [InlineData("25.555")]
        [InlineData("-1")]
        [InlineData("abc")]
        [InlineData("")]
        [InlineData("1e3")]
        [InlineData(null)]
        public void RejectsAmountsThatCannotBeRepresentedExactly(string? bad)
        {
            Assert.Equal(ManatErrorCode.InvalidRequest, Assert.Throws<ManatException>(() => Money.Azn(bad!)).Code);
        }

        [Fact]
        public void RejectsNegativeMinorUnits() => Assert.Throws<ManatException>(() => Money.Of(-5, Currency.AZN));

        [Fact]
        public void MapsIsoNumericCodesBothWays()
        {
            Assert.Equal("944", Currency.AZN.NumericCode());
            Assert.Equal(Currency.AZN, Currencies.FromNumeric("944"));
            Assert.Null(Currencies.FromNumeric("000"));
        }
    }

    public class ManatClientTest
    {
        private static readonly CreatePaymentRequest Valid = new CreatePaymentRequest
        {
            OrderId = "A-1", Amount = Money.Of(2550, Currency.AZN), ReturnUrl = "https://shop.example/return",
        };

        /// <summary>Records calls; offers createPayment, verifyCallback and getStatus.</summary>
        private sealed class FakeProvider : PaymentProvider
        {
            public List<string> Calls { get; } = new List<string>();

            public override string Name => "fake";

            public override IReadOnlyCollection<Capability> Capabilities { get; } = new[] { Capability.CreatePayment, Capability.VerifyCallback, Capability.GetStatus };

            public override Task<CreatePaymentResult> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken = default)
            {
                Calls.Add("createPayment");
                return Task.FromResult(new CreatePaymentResult("p-" + request.OrderId, PaymentStatus.Pending, NextAction.Redirect("https://pay.example/1"), null));
            }

            public override Task<PaymentEvent> VerifyCallbackAsync(IncomingCallback callback, CancellationToken cancellationToken = default)
            {
                Calls.Add("verifyCallback");
                return Task.FromResult(new PaymentEvent("p-1", PaymentStatus.Paid));
            }

            public override Task<PaymentState> GetStatusAsync(string paymentId, CancellationToken cancellationToken = default)
            {
                Calls.Add("getStatus");
                return Task.FromResult(new PaymentState(paymentId, PaymentStatus.Paid));
            }
        }

        private static async Task ExpectCode(ManatErrorCode code, Func<Task> call) =>
            Assert.Equal(code, (await Assert.ThrowsAsync<ManatException>(call)).Code);

        [Fact]
        public async Task DelegatesValidRequestsToTheProvider()
        {
            var provider = new FakeProvider();
            Assert.Equal("p-A-1", (await new ManatClient(provider).CreatePaymentAsync(Valid)).PaymentId);
            Assert.Equal(new[] { "createPayment" }, provider.Calls);
        }

        [Fact]
        public async Task ValidatesInputBeforeCallingTheProvider()
        {
            var provider = new FakeProvider();
            var client = new ManatClient(provider);
            await ExpectCode(ManatErrorCode.InvalidRequest, () => client.CreatePaymentAsync(new CreatePaymentRequest { Amount = Valid.Amount, ReturnUrl = Valid.ReturnUrl }));
            await ExpectCode(ManatErrorCode.InvalidRequest, () => client.CreatePaymentAsync(new CreatePaymentRequest { OrderId = "A-1", ReturnUrl = Valid.ReturnUrl }));
            await ExpectCode(ManatErrorCode.InvalidRequest, () => client.CreatePaymentAsync(new CreatePaymentRequest
            {
                OrderId = "A-1", Amount = Money.Of(0, Currency.AZN), ReturnUrl = Valid.ReturnUrl,
            }));
            await ExpectCode(ManatErrorCode.InvalidRequest, () => client.CreatePaymentAsync(new CreatePaymentRequest
            {
                OrderId = "A-1", Amount = Valid.Amount, ReturnUrl = "javascript:alert(1)",
            }));
            await ExpectCode(ManatErrorCode.InvalidRequest, () => client.CreatePaymentAsync(new CreatePaymentRequest
            {
                OrderId = "A-1", Amount = Valid.Amount, ReturnUrl = Valid.ReturnUrl, CallbackUrl = "/relative",
            }));
            Assert.Empty(provider.Calls);
        }

        [Fact]
        public async Task ReportsUnsupportedOperations()
        {
            var client = new ManatClient(new FakeProvider());
            Assert.False(client.Supports(Capability.Refund));
            await ExpectCode(ManatErrorCode.UnsupportedOperation, () => client.RefundAsync(new RefundRequest { PaymentId = "p-1" }));
            await ExpectCode(ManatErrorCode.UnsupportedOperation, () => client.CaptureAsync(new PaymentAction { PaymentId = "p-1" }));
        }

        [Fact]
        public async Task RequiresAPaymentIdForFollowUpOperations()
        {
            var client = new ManatClient(new FakeProvider());
            await ExpectCode(ManatErrorCode.InvalidRequest, () => client.GetStatusAsync(""));
            await ExpectCode(ManatErrorCode.InvalidRequest, () => client.RefundAsync(new RefundRequest()));
        }
    }

    /// <summary>The package is advertised as dependency-free: the library project may not reference any package.</summary>
    public class NoDependenciesTest
    {
        [Fact]
        public void LibraryHasNoPackageReferences()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "KeyTD.Manat", "KeyTD.Manat.csproj")))
            {
                dir = dir.Parent;
            }
            Assert.NotNull(dir);
            var project = File.ReadAllText(Path.Combine(dir!.FullName, "src", "KeyTD.Manat", "KeyTD.Manat.csproj"));
            Assert.Empty(Regex.Matches(project, "<PackageReference").Cast<Match>().Select(m => m.Value));
        }
    }
}
