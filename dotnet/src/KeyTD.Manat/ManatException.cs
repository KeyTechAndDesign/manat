using System;

namespace KeyTD.Manat
{
    /// <summary>The spec's error codes.</summary>
    public enum ManatErrorCode
    {
        /// <summary>The input is wrong: fix the call.</summary>
        InvalidRequest,
        /// <summary>A callback is not authentic. Never fulfil an order on it.</summary>
        InvalidSignature,
        /// <summary>The provider refused the request or answered something unexpected.</summary>
        ProviderError,
        /// <summary>The provider could not be reached, timed out or did not answer JSON.</summary>
        NetworkError,
        /// <summary>The provider has no such operation.</summary>
        UnsupportedOperation,
    }

    /// <summary>Spec names for <see cref="ManatErrorCode"/>.</summary>
    public static class ManatErrorCodeExtensions
    {
        /// <summary>The spec's name, such as <c>"provider_error"</c>.</summary>
        public static string ToValue(this ManatErrorCode code) => code switch
        {
            ManatErrorCode.InvalidRequest => "invalid_request",
            ManatErrorCode.InvalidSignature => "invalid_signature",
            ManatErrorCode.ProviderError => "provider_error",
            ManatErrorCode.NetworkError => "network_error",
            _ => "unsupported_operation",
        };
    }

    /// <summary>
    /// Every error the library throws. <see cref="Code"/> says what kind of failure it is; for provider errors
    /// <see cref="ProviderCode"/> and <see cref="ProviderMessage"/> hold what the provider said and <see cref="Raw"/> its reply.
    /// </summary>
    public sealed class ManatException : Exception
    {
        /// <summary>Creates an error.</summary>
        public ManatException(ManatErrorCode code, string message, string? provider = null, string? providerCode = null,
            string? providerMessage = null, object? raw = null, Exception? inner = null)
            : base(message, inner)
        {
            Code = code;
            Provider = provider;
            ProviderCode = providerCode;
            ProviderMessage = providerMessage;
            Raw = raw;
        }

        /// <summary>What kind of failure this is.</summary>
        public ManatErrorCode Code { get; }

        /// <summary>The adapter's name, such as <c>"payriff"</c>, or null for input checks made before any adapter runs.</summary>
        public string? Provider { get; }

        /// <summary>The provider's own error code, when it gave one.</summary>
        public string? ProviderCode { get; }

        /// <summary>The provider's own error text, when it gave one.</summary>
        public string? ProviderMessage { get; }

        /// <summary>The provider's whole reply (parsed JSON or text), when there was one.</summary>
        public object? Raw { get; }
    }
}
