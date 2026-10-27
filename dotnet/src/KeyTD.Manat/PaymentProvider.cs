using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace KeyTD.Manat
{
    /// <summary>What every provider adapter implements. Use it through <see cref="ManatClient"/>, which validates input first.</summary>
    public interface IPaymentProvider
    {
        /// <summary>Short lower-case name, such as <c>"kapitalbank"</c>.</summary>
        string Name { get; }

        /// <summary>The operations this adapter offers.</summary>
        IReadOnlyCollection<Capability> Capabilities { get; }

        /// <summary>Starts a payment.</summary>
        Task<CreatePaymentResult> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken = default);

        /// <summary>Authenticates a callback or redirect.</summary>
        Task<PaymentEvent> VerifyCallbackAsync(IncomingCallback callback, CancellationToken cancellationToken = default);

        /// <summary>Reads a payment's current state.</summary>
        Task<PaymentState> GetStatusAsync(string paymentId, CancellationToken cancellationToken = default);

        /// <summary>Refunds a payment.</summary>
        Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken cancellationToken = default);

        /// <summary>Completes a pre-authorised payment.</summary>
        Task<PaymentState> CaptureAsync(PaymentAction action, CancellationToken cancellationToken = default);

        /// <summary>Releases a hold or reverses a payment.</summary>
        Task<PaymentState> CancelAsync(PaymentAction action, CancellationToken cancellationToken = default);
    }

    /// <summary>Settings every adapter takes.</summary>
    public abstract class ProviderOptions
    {
        /// <summary>Your HttpClient (for example from IHttpClientFactory); a shared one is used when null.</summary>
        public HttpClient? HttpClient { get; init; }

        /// <summary>Per-request timeout; default 30 seconds.</summary>
        public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    }

    /// <summary>Base for adapters: optional operations throw <c>unsupported_operation</c> unless overridden.</summary>
    public abstract class PaymentProvider : IPaymentProvider
    {
        /// <inheritdoc />
        public abstract string Name { get; }

        /// <inheritdoc />
        public abstract IReadOnlyCollection<Capability> Capabilities { get; }

        /// <inheritdoc />
        public abstract Task<CreatePaymentResult> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken = default);

        /// <inheritdoc />
        public abstract Task<PaymentEvent> VerifyCallbackAsync(IncomingCallback callback, CancellationToken cancellationToken = default);

        /// <inheritdoc />
        public virtual Task<PaymentState> GetStatusAsync(string paymentId, CancellationToken cancellationToken = default) =>
            throw Unsupported(Capability.GetStatus);

        /// <inheritdoc />
        public virtual Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken cancellationToken = default) =>
            throw Unsupported(Capability.Refund);

        /// <inheritdoc />
        public virtual Task<PaymentState> CaptureAsync(PaymentAction action, CancellationToken cancellationToken = default) =>
            throw Unsupported(Capability.Capture);

        /// <inheritdoc />
        public virtual Task<PaymentState> CancelAsync(PaymentAction action, CancellationToken cancellationToken = default) =>
            throw Unsupported(Capability.Cancel);

        /// <summary>The error for an operation this adapter does not have.</summary>
        protected ManatException Unsupported(Capability capability) =>
            new ManatException(ManatErrorCode.UnsupportedOperation, $"{Name} does not support {capability.ToValue()}", Name);
    }
}
