using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KeyTD.Manat.Internal
{
    /// <summary>Status code and body text of an HTTP response.</summary>
    internal sealed class HttpReply
    {
        public HttpReply(int status, string body)
        {
            Status = status;
            Body = body;
        }

        public int Status { get; }

        public string Body { get; }
    }

    /// <summary>Wraps an HttpClient for one adapter: encodes bodies, applies the timeout and turns failures into ManatException.</summary>
    internal sealed class Http
    {
        /// <summary>Shared by adapters that were not given a client, as HttpClient is meant to be reused.</summary>
        private static readonly HttpClient Shared = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

        private readonly string provider;
        private readonly HttpClient client;
        private readonly TimeSpan timeout;

        public Http(string provider, ProviderOptions options)
        {
            this.provider = provider;
            client = options.HttpClient ?? Shared;
            timeout = options.Timeout;
        }

        /// <summary>Sends a request with a JSON body (<paramref name="json"/>) or, when given, a urlencoded one (<paramref name="form"/>).</summary>
        public async Task<HttpReply> SendAsync(string method, string url, IEnumerable<KeyValuePair<string, string>>? headers, object? json,
            IEnumerable<KeyValuePair<string, string>>? form, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), url);
            string? contentType = null;
            foreach (var header in headers ?? Array.Empty<KeyValuePair<string, string>>())
            {
                if (string.Equals(header.Key, "content-type", StringComparison.OrdinalIgnoreCase))
                {
                    contentType = header.Value;
                }
                else
                {
                    request.Headers.TryAddWithoutValidation(header.Key.ToLowerInvariant(), header.Value);
                }
            }
            string? body = null;
            if (form != null)
            {
                body = Form.Encode(form);
                contentType ??= "application/x-www-form-urlencoded";
            }
            else if (json != null)
            {
                body = Json.Stringify(json);
                contentType ??= "application/json";
            }
            if (body != null)
            {
                request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType!);
            }

            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timer.CancelAfter(timeout);
            try
            {
                using var response = await client.SendAsync(request, timer.Token).ConfigureAwait(false);
                var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                return new HttpReply((int)response.StatusCode, Decode(bytes, response.Content.Headers.ContentType?.CharSet));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException error)
            {
                throw new ManatException(ManatErrorCode.NetworkError, $"{provider}: {method} {url} timed out after {timeout.TotalMilliseconds} ms", provider, inner: error);
            }
            catch (Exception error) when (error is HttpRequestException || error is IOException)
            {
                throw new ManatException(ManatErrorCode.NetworkError, $"{provider}: {method} {url} {error.Message}", provider, inner: error);
            }
        }

        /// <summary>Like SendAsync, and parses the response as JSON (null for an empty body).</summary>
        public async Task<(int Status, object? Data)> JsonAsync(string method, string url, IEnumerable<KeyValuePair<string, string>>? headers, object? json,
            IEnumerable<KeyValuePair<string, string>>? form, CancellationToken cancellationToken)
        {
            var withAccept = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("accept", "application/json") };
            withAccept.AddRange(headers ?? Array.Empty<KeyValuePair<string, string>>());
            var reply = await SendAsync(method, url, withAccept, json, form, cancellationToken).ConfigureAwait(false);
            if (reply.Body.Length == 0)
            {
                return (reply.Status, null);
            }
            try
            {
                return (reply.Status, Json.Parse(reply.Body));
            }
            catch (JsonParseException error)
            {
                var excerpt = reply.Body.Length > 200 ? reply.Body.Substring(0, 200) : reply.Body;
                throw new ManatException(ManatErrorCode.NetworkError, $"{provider}: expected JSON, got HTTP {reply.Status} \"{excerpt}\"", provider, raw: reply.Body,
                    inner: error);
            }
        }

        private static string Decode(byte[] bytes, string? charset)
        {
            if (!string.IsNullOrEmpty(charset))
            {
                try
                {
                    return Encoding.GetEncoding(charset!.Trim('"')).GetString(bytes);
                }
                catch (ArgumentException)
                {
                    // unknown charset: fall back to UTF-8
                }
            }
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
