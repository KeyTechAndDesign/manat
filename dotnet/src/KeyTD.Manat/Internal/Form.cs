using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace KeyTD.Manat.Internal
{
    /// <summary>application/x-www-form-urlencoded, encoded like JavaScript's URLSearchParams.</summary>
    internal static class Form
    {
        public static string Encode(IEnumerable<KeyValuePair<string, string>> fields)
        {
            var output = new StringBuilder();
            foreach (var field in fields)
            {
                if (output.Length > 0)
                {
                    output.Append('&');
                }
                output.Append(Escape(field.Key)).Append('=').Append(Escape(field.Value ?? ""));
            }
            return output.ToString();
        }

        /// <summary>Later duplicates are ignored; malformed %-escapes are kept as text.</summary>
        public static Dictionary<string, string> Decode(string? body)
        {
            var output = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(body))
            {
                return output;
            }
            foreach (var pair in body!.Split('&'))
            {
                if (pair.Length == 0)
                {
                    continue;
                }
                var eq = pair.IndexOf('=');
                var key = Unescape(eq < 0 ? pair : pair.Substring(0, eq));
                if (!output.ContainsKey(key))
                {
                    output[key] = eq < 0 ? "" : Unescape(pair.Substring(eq + 1));
                }
            }
            return output;
        }

        /// <summary>The urlencoded serializer: alphanumerics and <c>*-._</c> stay, space is <c>+</c>, the rest is %XX of UTF-8.</summary>
        private static string Escape(string text)
        {
            var output = new StringBuilder();
            foreach (var b in Encoding.UTF8.GetBytes(text))
            {
                var c = (char)b;
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '*' || c == '-' || c == '.' || c == '_')
                {
                    output.Append(c);
                }
                else if (c == ' ')
                {
                    output.Append('+');
                }
                else
                {
                    output.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
                }
            }
            return output.ToString();
        }

        private static string Unescape(string text)
        {
            var raw = Encoding.UTF8.GetBytes(text);
            using var bytes = new MemoryStream();
            for (var i = 0; i < raw.Length; i++)
            {
                var b = raw[i];
                if (b == '+')
                {
                    bytes.WriteByte((byte)' ');
                }
                else if (b == '%' && i + 2 < raw.Length && Hex(raw[i + 1]) >= 0 && Hex(raw[i + 2]) >= 0)
                {
                    bytes.WriteByte((byte)(Hex(raw[i + 1]) * 16 + Hex(raw[i + 2])));
                    i += 2;
                }
                else
                {
                    bytes.WriteByte(b);
                }
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        private static int Hex(byte b)
        {
            if (b >= '0' && b <= '9')
            {
                return b - '0';
            }
            if (b >= 'a' && b <= 'f')
            {
                return b - 'a' + 10;
            }
            if (b >= 'A' && b <= 'F')
            {
                return b - 'A' + 10;
            }
            return -1;
        }
    }
}
