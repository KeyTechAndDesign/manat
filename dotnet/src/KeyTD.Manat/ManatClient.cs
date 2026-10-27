using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace KeyTD.Manat
{
    /// <summary>
    /// Provider-neutral facade. Validates input once, then delegates to the adapter,
    /// so every provider gets the same checks and the same errors.
    /// <code>
    /// var pay = new ManatClient(new KapitalBankProvider(new KapitalBankOptions { Username = user, Password = password }));
    /// var result = await pay.CreatePaymentAsync(new CreatePaymentRequest { OrderId = "A-1001", Amount = Money.Azn("25.50"), ReturnUrl = returnUrl });
    /// return Redirect(result.Next.Url);
    /// </code>
    /// </summary>
    public sealed class ManatClient
    {
        /// <summary>Wraps an adapter.</summary>
        public ManatClient(IPaymentProvider provider)
        {
            Provider = provider ?? throw Invalid("provider is required");
        }

        /// <summary>The adapter.</summary>
        public IPaymentProvider Provider { get; }

        /// <summary>The operations the adapter offers.</summary>
        public IReadOnlyCollection<Capability> Capabilities => Provider.Capabilities;

        /// <summary>Whether the adapter offers this operation.</summary>
        public bool Supports(Capability capability) => Provider.Capabilities.Contains(capability);

        /// <summary>Starts a payment; <see cref="CreatePaymentResult.Next"/> says where to send the customer.</summary>
        public Task<CreatePaymentResult> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null || string.IsNullOrEmpty(request.OrderId))
            {
                throw Invalid("orderId is required");
            }
            RequireMoney(request.Amount);
            RequireUrl(request.ReturnUrl, "returnUrl");
            if (request.CallbackUrl != null)
            {
                RequireUrl(request.CallbackUrl, "callbackUrl");
            }
            return Provider.CreatePaymentAsync(request, cancellationToken);
        }

        /// <summary>
        /// Authenticates a provider callback or redirect. Throws <c>invalid_signature</c> when it is not authentic;
        /// an unsigned redirect is never trusted on its own (the adapter re-reads the payment from the provider).
        /// </summary>
        public Task<PaymentEvent> VerifyCallbackAsync(IncomingCallback callback, CancellationToken cancellationToken = default)
        {
            if (callback == null || callback.Method == null)
            {
                throw Invalid("callback.method is required");
            }
            return Provider.VerifyCallbackAsync(callback, cancellationToken);
        }

        /// <summary>Reads a payment's current state.</summary>
        public Task<PaymentState> GetStatusAsync(string paymentId, CancellationToken cancellationToken = default)
        {
            RequirePaymentId(paymentId);
            Require(Capability.GetStatus);
            return Provider.GetStatusAsync(paymentId, cancellationToken);
        }

        /// <summary>Full refund without an amount, partial with one.</summary>
        public Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken cancellationToken = default)
        {
            RequirePaymentId(request?.PaymentId);
            if (request!.Amount != null)
            {
                RequireMoney(request.Amount);
            }
            Require(Capability.Refund);
            return Provider.RefundAsync(request, cancellationToken);
        }

        /// <summary>Completes a payment made with <see cref="Intent.Authorize"/>.</summary>
        public Task<PaymentState> CaptureAsync(PaymentAction action, CancellationToken cancellationToken = default)
        {
            RequirePaymentId(action?.PaymentId);
            if (action!.Amount != null)
            {
                RequireMoney(action.Amount);
            }
            Require(Capability.Capture);
            return Provider.CaptureAsync(action, cancellationToken);
        }

        /// <summary>Releases a hold, or reverses a same-day payment where the provider allows it.</summary>
        public Task<PaymentState> CancelAsync(PaymentAction action, CancellationToken cancellationToken = default)
        {
            RequirePaymentId(action?.PaymentId);
            Require(Capability.Cancel);
            return Provider.CancelAsync(action!, cancellationToken);
        }

        private void Require(Capability capability)
        {
            if (!Supports(capability))
            {
                throw new ManatException(ManatErrorCode.UnsupportedOperation, $"{Provider.Name} does not support {capability.ToValue()}", Provider.Name);
            }
        }

        private static void RequirePaymentId(string? paymentId)
        {
            if (string.IsNullOrEmpty(paymentId))
            {
                throw Invalid("paymentId is required");
            }
        }

        private static void RequireMoney(Money? money)
        {
            if (money == null)
            {
                throw Invalid("amount is required");
            }
            if (money.Amount < 1)
            {
                throw Invalid($"amount.amount must be a positive integer in minor units (2550 = 25.50), got {money.Amount}");
            }
        }

        private static void RequireUrl(string? value, string field)
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                && !string.IsNullOrEmpty(uri.Authority))
            {
                return;
            }
            throw Invalid($"{field} must be an absolute http(s) URL");
        }

        private static ManatException Invalid(string message) => new ManatException(ManatErrorCode.InvalidRequest, message);
    }
}
