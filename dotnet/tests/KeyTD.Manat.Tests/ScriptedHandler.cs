using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KeyTD.Manat.Internal;
using Xunit;

namespace KeyTD.Manat.Tests
{
    /// <summary>Serves a vector's <c>http</c> steps in order and checks that each request matches <c>expect</c>.</summary>
    internal sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly IReadOnlyList<object?> steps;
        private int index;

        public ScriptedHandler(IReadOnlyList<object?>? steps)
        {
            this.steps = steps ?? new List<object?>();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var n = index + 1;
            var url = request.RequestUri!.OriginalString;
            Assert.True(index < steps.Count, $"unexpected request #{n}: {request.Method} {url}");
            var step = steps[index++];
            var expect = Values.Map(Values.Get(step, "expect"))!;
            Assert.Equal(Values.Get(expect, "method"), request.Method.Method);
            Assert.Equal(Values.Get(expect, "url"), url);
            if (Values.Map(Values.Get(expect, "headers")) is { } headers)
            {
                foreach (var header in headers)
                {
                    var actual = request.Headers.TryGetValues(header.Key, out var values) ? string.Join(",", values)
                        : request.Content != null && request.Content.Headers.TryGetValues(header.Key, out var content) ? string.Join(",", content) : null;
                    Assert.True(Equals(header.Value, actual), $"request #{n} header {header.Key}: expected {header.Value}, got {actual}");
                }
            }
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (expect.ContainsKey("json"))
            {
                Expect.Equal(Values.Get(expect, "json"), Json.Parse(body), $"request #{n} body");
            }
            if (expect.ContainsKey("form"))
            {
                Expect.Equal(Values.Get(expect, "form"), Form.Decode(body), $"request #{n} form");
            }
            var respond = Values.Map(Values.Get(step, "respond"))!;
            var text = respond.ContainsKey("json") ? Json.Stringify(Values.Get(respond, "json")) : Values.Get(respond, "text") as string ?? "";
            return new HttpResponseMessage((HttpStatusCode)Values.Integer(Values.Get(respond, "status"))!.Value)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(text)),
            };
        }

        public void AssertDone() => Assert.True(index == steps.Count, $"all scripted requests were made: {index} of {steps.Count}");
    }

    /// <summary>
    /// Comparisons with the vectors' JSON semantics: numbers compare by value (1 equals 1.0), a string never equals a number.
    /// </summary>
    internal static class Expect
    {
        /// <summary>Deep equality, like JavaScript's toEqual.</summary>
        public static void Equal(object? expected, object? actual, string path) => Compare(expected, actual, path, false);

        /// <summary>Every key in <paramref name="expected"/> must be in <paramref name="actual"/> with a matching value, like toMatchObject.</summary>
        public static void Subset(object? expected, object? actual, string path) => Compare(expected, actual, path, true);

        private static void Compare(object? expected, object? actual, string path, bool subset)
        {
            if (AsMap(expected) is { } want)
            {
                var got = AsMap(actual);
                Assert.True(got != null, $"{path}: expected an object, got {actual}");
                if (!subset)
                {
                    Assert.True(want.Keys.OrderBy(k => k).SequenceEqual(got!.Keys.OrderBy(k => k)), $"{path}: keys {string.Join(",", got!.Keys)}");
                }
                foreach (var pair in want)
                {
                    Assert.True(got!.ContainsKey(pair.Key), $"{path}.{pair.Key} is missing");
                    Compare(pair.Value, got[pair.Key], $"{path}.{pair.Key}", subset);
                }
                return;
            }
            if (expected is IReadOnlyList<object?> list)
            {
                var items = (actual as System.Collections.IEnumerable)?.Cast<object?>().ToList();
                Assert.True(items != null && !(actual is string), $"{path}: expected a list, got {actual}");
                Assert.True(list.Count == items!.Count, $"{path}: list length {items.Count}, expected {list.Count}");
                for (var i = 0; i < list.Count; i++)
                {
                    Compare(list[i], items[i], $"{path}[{i}]", subset);
                }
                return;
            }
            if (Number(expected) is { } a && Number(actual) is { } b)
            {
                Assert.True(a == b, $"{path}: expected {expected}, got {actual}");
                return;
            }
            Assert.True(Equals(expected, actual), $"{path}: expected {expected ?? "null"}, got {actual ?? "null"}");
        }

        private static Dictionary<string, object?>? AsMap(object? value)
        {
            switch (value)
            {
                case IEnumerable<KeyValuePair<string, object?>> map:
                    return map.ToDictionary(p => p.Key, p => p.Value);
                case IEnumerable<KeyValuePair<string, string>> strings:
                    return strings.ToDictionary(p => p.Key, p => (object?)p.Value);
                case System.Collections.IDictionary dictionary:
                    var output = new Dictionary<string, object?>();
                    foreach (System.Collections.DictionaryEntry entry in dictionary)
                    {
                        output[entry.Key.ToString()!] = entry.Value;
                    }
                    return output;
                default:
                    return null;
            }
        }

        private static decimal? Number(object? value) => value switch
        {
            long n => n,
            int n => n,
            decimal n => n,
            double n => (decimal)n,
            _ => null,
        };
    }
}
