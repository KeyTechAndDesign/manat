using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace KeyTD.Manat.Internal
{
    /// <summary>Hashes, constant-time comparison and RSA keys from PEM text (PKCS#1 or PKCS#8 / SPKI), all in the base library.</summary>
    internal static class Crypto
    {
        private static readonly Regex Pem = new Regex(@"-----BEGIN ([A-Z0-9 ]+)-----([A-Za-z0-9+/=\s]+)-----END \1-----", RegexOptions.CultureInvariant);

        public static string Md5Hex(string text)
        {
            using var md5 = MD5.Create();
            return Hex(md5.ComputeHash(Encoding.UTF8.GetBytes(text)));
        }

        public static byte[] Sha1(byte[] data)
        {
            using var sha1 = SHA1.Create();
            return sha1.ComputeHash(data);
        }

        public static string Hex(byte[] bytes)
        {
            var output = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
            {
                output.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }
            return output.ToString();
        }

        /// <summary>Hex text to bytes; null unless it is an even number of hex digits.</summary>
        public static byte[]? Unhex(string text)
        {
            if (text.Length % 2 != 0)
            {
                return null;
            }
            var output = new byte[text.Length / 2];
            for (var i = 0; i < output.Length; i++)
            {
                var high = HexDigit(text[i * 2]);
                var low = HexDigit(text[i * 2 + 1]);
                if (high < 0 || low < 0)
                {
                    return null;
                }
                output[i] = (byte)(high * 16 + low);
            }
            return output;
        }

        /// <summary>Constant-time comparison of two strings' UTF-8 bytes.</summary>
        public static bool SafeEquals(string? actual, string expected)
        {
            if (actual == null)
            {
                return false;
            }
            var a = Encoding.UTF8.GetBytes(actual);
            var b = Encoding.UTF8.GetBytes(expected);
            var diff = a.Length ^ b.Length;
            for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                diff |= a[i] ^ b[i];
            }
            return diff == 0;
        }

        public static RSA RsaPrivateKey(string pem)
        {
            var (label, der) = Block(pem);
            var reader = new Der(der);
            switch (label)
            {
                case "RSA PRIVATE KEY":
                    return Import(PrivateParameters(reader));
                case "PRIVATE KEY":
                    // PrivateKeyInfo { version, AlgorithmIdentifier, OCTET STRING { RSAPrivateKey } }
                    var info = reader.Sequence();
                    info.Integer();
                    info.Sequence();
                    return Import(PrivateParameters(new Der(info.Read(0x04))));
                default:
                    throw new CryptographicException($"unsupported PEM block \"{label}\" (encrypted keys are not supported)");
            }
        }

        public static RSA RsaPublicKey(string pem)
        {
            var (label, der) = Block(pem);
            switch (label)
            {
                case "RSA PUBLIC KEY":
                    return Import(PublicParameters(new Der(der)));
                case "PUBLIC KEY":
                    // SubjectPublicKeyInfo { AlgorithmIdentifier, BIT STRING { RSAPublicKey } }
                    var info = new Der(der).Sequence();
                    info.Sequence();
                    var bits = info.Read(0x03);
                    var key = new byte[bits.Length - 1];
                    Array.Copy(bits, 1, key, 0, key.Length);
                    return Import(PublicParameters(new Der(key)));
                case "CERTIFICATE":
                    return new X509Certificate2(der).GetRSAPublicKey() ?? throw new CryptographicException("certificate has no RSA key");
                default:
                    throw new CryptographicException($"unsupported PEM block \"{label}\"");
            }
        }

        /// <summary>Modulus size in bits.</summary>
        public static int KeyBits(RSA key)
        {
            var modulus = key.ExportParameters(false).Modulus!;
            var start = 0;
            while (start < modulus.Length && modulus[start] == 0)
            {
                start++;
            }
            if (start == modulus.Length)
            {
                return 0;
            }
            var bits = (modulus.Length - start - 1) * 8;
            for (var top = modulus[start]; top != 0; top >>= 1)
            {
                bits++;
            }
            return bits;
        }

        /// <summary>RSA-SHA256 (PKCS#1 v1.5) over the UTF-8 text.</summary>
        public static byte[] SignSha256(RSA key, string text) =>
            key.SignData(Encoding.UTF8.GetBytes(text), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        public static bool VerifySha256(RSA key, string text, byte[] signature)
        {
            try
            {
                return key.VerifyData(Encoding.UTF8.GetBytes(text), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
            catch (CryptographicException)
            {
                return false;
            }
        }

        private static (string Label, byte[] Der) Block(string pem)
        {
            var match = Pem.Match(pem ?? "");
            if (!match.Success)
            {
                throw new CryptographicException("not a PEM key");
            }
            try
            {
                return (match.Groups[1].Value, Convert.FromBase64String(Regex.Replace(match.Groups[2].Value, @"\s", "")));
            }
            catch (FormatException error)
            {
                throw new CryptographicException("PEM body is not base64", error);
            }
        }

        private static RSA Import(RSAParameters parameters)
        {
            var rsa = RSA.Create();
            rsa.ImportParameters(parameters);
            return rsa;
        }

        /// <summary>RSAPrivateKey { version, n, e, d, p, q, dp, dq, qinv }, padded the way RSAParameters expects.</summary>
        private static RSAParameters PrivateParameters(Der der)
        {
            var key = der.Sequence();
            key.Integer();
            var modulus = Unsigned(key.Integer());
            var half = (modulus.Length + 1) / 2;
            return new RSAParameters
            {
                Modulus = modulus,
                Exponent = Unsigned(key.Integer()),
                D = Pad(Unsigned(key.Integer()), modulus.Length),
                P = Pad(Unsigned(key.Integer()), half),
                Q = Pad(Unsigned(key.Integer()), half),
                DP = Pad(Unsigned(key.Integer()), half),
                DQ = Pad(Unsigned(key.Integer()), half),
                InverseQ = Pad(Unsigned(key.Integer()), half),
            };
        }

        /// <summary>RSAPublicKey { n, e }.</summary>
        private static RSAParameters PublicParameters(Der der)
        {
            var key = der.Sequence();
            return new RSAParameters { Modulus = Unsigned(key.Integer()), Exponent = Unsigned(key.Integer()) };
        }

        private static byte[] Unsigned(byte[] integer)
        {
            var start = 0;
            while (start < integer.Length - 1 && integer[start] == 0)
            {
                start++;
            }
            var output = new byte[integer.Length - start];
            Array.Copy(integer, start, output, 0, output.Length);
            return output;
        }

        private static byte[] Pad(byte[] value, int length)
        {
            if (value.Length >= length)
            {
                return value;
            }
            var output = new byte[length];
            Array.Copy(value, 0, output, length - value.Length, value.Length);
            return output;
        }

        private static int HexDigit(char c) =>
            c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;

        /// <summary>Just enough DER reading for RSA keys.</summary>
        private sealed class Der
        {
            private readonly byte[] data;
            private int pos;

            public Der(byte[] data)
            {
                this.data = data;
            }

            public Der Sequence() => new Der(Read(0x30));

            public byte[] Integer() => Read(0x02);

            public byte[] Read(byte tag)
            {
                if (pos >= data.Length || data[pos] != tag)
                {
                    throw new CryptographicException($"bad key: expected DER tag 0x{tag:x2}");
                }
                pos++;
                var length = (int)data[pos++];
                if (length >= 0x80)
                {
                    var count = length & 0x7f;
                    if (count < 1 || count > 4)
                    {
                        throw new CryptographicException("bad key: DER length");
                    }
                    length = 0;
                    for (var i = 0; i < count; i++)
                    {
                        length = (length << 8) | data[pos++];
                    }
                }
                if (length < 0 || pos + length > data.Length)
                {
                    throw new CryptographicException("bad key: truncated");
                }
                var value = new byte[length];
                Array.Copy(data, pos, value, 0, length);
                pos += length;
                return value;
            }
        }

        /// <summary>Base64 of the bytes.</summary>
        public static string Base64(byte[] bytes) => Convert.ToBase64String(bytes);
    }
}
