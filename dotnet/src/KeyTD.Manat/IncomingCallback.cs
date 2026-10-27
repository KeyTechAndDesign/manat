using System;
using System.Collections.Generic;
using System.Linq;
using KeyTD.Manat.Internal;

namespace KeyTD.Manat
{
    /// <summary>
    /// What the merchant's HTTP handler received, for <see cref="ManatClient.VerifyCallbackAsync"/>. Pass the raw body when you have it.
    /// <code>
    /// new IncomingCallback(Request.Method).WithQuery(Request.Query).WithBody(rawBody)
    /// </code>
    /// </summary>
    public sealed class IncomingCallback
    {
        private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Empty = new Dictionary<string, IReadOnlyList<string>>();

        private IncomingCallback(string method, IReadOnlyDictionary<string, IReadOnlyList<string>> query, string? bodyText,
            IReadOnlyDictionary<string, object?>? bodyFields, IReadOnlyDictionary<string, IReadOnlyList<string>> headers)
        {
            Method = method;
            Query = query;
            BodyText = bodyText;
            BodyFields = bodyFields;
            Headers = headers;
        }

        /// <summary>A callback with this HTTP method and nothing else yet.</summary>
        public IncomingCallback(string method)
            : this(method, Empty, null, null, Empty)
        {
        }

        /// <summary>The HTTP method.</summary>
        public string Method { get; }

        /// <summary>Query parameters.</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Query { get; }

        /// <summary>The raw body, or null when none was given or parsed fields were given instead.</summary>
        public string? BodyText { get; }

        /// <summary>A parsed body (form fields, or a JSON object as dictionaries and lists), or null.</summary>
        public IReadOnlyDictionary<string, object?>? BodyFields { get; }

        /// <summary>Request headers with lower-case names.</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Headers { get; }

        /// <summary>Query parameters with one value each.</summary>
        public IncomingCallback WithQuery(IEnumerable<KeyValuePair<string, string>> query) =>
            new IncomingCallback(Method, Multi(query.Select(p => new KeyValuePair<string, IEnumerable<string?>>(p.Key, new[] { p.Value })), false),
                BodyText, BodyFields, Headers);

        /// <summary>Query parameters with several values each, such as ASP.NET Core's <c>Request.Query</c>.</summary>
        public IncomingCallback WithQuery<TValues>(IEnumerable<KeyValuePair<string, TValues>> query) where TValues : IEnumerable<string?> =>
            new IncomingCallback(Method, Multi(query.Select(p => new KeyValuePair<string, IEnumerable<string?>>(p.Key, p.Value)), false),
                BodyText, BodyFields, Headers);

        /// <summary>The raw request body (urlencoded form or JSON text).</summary>
        public IncomingCallback WithBody(string? body) => new IncomingCallback(Method, Query, body, null, Headers);

        /// <summary>A body your framework already parsed: form fields, or a JSON object as dictionaries and lists.</summary>
        public IncomingCallback WithBody(IReadOnlyDictionary<string, object?>? body) => new IncomingCallback(Method, Query, null, body, Headers);

        /// <summary>Request headers with several values each.</summary>
        public IncomingCallback WithHeaders<TValues>(IEnumerable<KeyValuePair<string, TValues>> headers) where TValues : IEnumerable<string?> =>
            new IncomingCallback(Method, Query, BodyText, BodyFields,
                Multi(headers.Select(p => new KeyValuePair<string, IEnumerable<string?>>(p.Key, p.Value)), true));

        /// <summary>Request headers with one value each.</summary>
        public IncomingCallback WithHeaders(IEnumerable<KeyValuePair<string, string>> headers) =>
            new IncomingCallback(Method, Query, BodyText, BodyFields,
                Multi(headers.Select(p => new KeyValuePair<string, IEnumerable<string?>>(p.Key, new[] { p.Value })), true));

        /// <summary>The first value of a query parameter, or null.</summary>
        public string? QueryValue(string name) => Query.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;

        /// <summary>A field of an urlencoded text body or of a parsed body (first value of a list), when it is a string; or null.</summary>
        public string? BodyField(string name)
        {
            if (BodyText != null)
            {
                return Form.Decode(BodyText).TryGetValue(name, out var text) ? text : null;
            }
            if (BodyFields == null || !BodyFields.TryGetValue(name, out var value))
            {
                return null;
            }
            return Values.FirstString(value);
        }

        /// <summary>The spec's shape, for logging: <c>{method, query, body, headers}</c>.</summary>
        public IReadOnlyDictionary<string, object?> ToDictionary() => new Dictionary<string, object?>
        {
            ["method"] = Method,
            ["query"] = Query,
            ["body"] = (object?)BodyText ?? BodyFields,
            ["headers"] = Headers,
        };

        private static IReadOnlyDictionary<string, IReadOnlyList<string>> Multi(IEnumerable<KeyValuePair<string, IEnumerable<string?>>> pairs, bool lowerCase)
        {
            var output = new Dictionary<string, IReadOnlyList<string>>(lowerCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var pair in pairs)
            {
                var key = lowerCase ? pair.Key.ToLowerInvariant() : pair.Key;
                output[key] = (pair.Value ?? Enumerable.Empty<string?>()).Where(v => v != null).Select(v => v!).ToList();
            }
            return output;
        }
    }
}
