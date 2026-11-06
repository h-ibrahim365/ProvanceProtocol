using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Provance.Core.Canonicalization
{
    /// <summary>
    /// JSON Canonicalization Scheme (JCS) as defined by RFC 8785.
    /// </summary>
    /// <remarks>
    /// <para>The output is the same in every language that implements RFC 8785, which is what makes
    /// PROVANCE hashes reproducible outside of .NET.</para>
    /// <para>Rules applied: object members sorted by UTF-16 code units, no whitespace, strings escaped
    /// with the minimal ECMAScript escaping, numbers formatted as IEEE 754 doubles using the
    /// ECMAScript Number serialization. Integers above 2^53 and decimals with more than ~15 significant
    /// digits lose precision: store such values as strings in your payloads.</para>
    /// </remarks>
    public static class JsonCanonicalizer
    {
        private static readonly JsonDocumentOptions ParseOptions = new()
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64
        };

        /// <summary>Canonicalizes a UTF-8 encoded JSON document.</summary>
        /// <exception cref="FormatException">The input is not valid JSON, contains duplicate member names or a non finite number.</exception>
        public static string Canonicalize(ReadOnlyMemory<byte> utf8Json)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(utf8Json, ParseOptions);
            }
            catch (JsonException ex)
            {
                throw new FormatException("Input is not valid JSON.", ex);
            }

            using (document)
            {
                var builder = new StringBuilder(utf8Json.Length);
                WriteElement(document.RootElement, builder);
                return builder.ToString();
            }
        }

        /// <summary>Canonicalizes a JSON document given as a string.</summary>
        /// <exception cref="FormatException">The input is not valid JSON, contains duplicate member names or a non finite number.</exception>
        public static string Canonicalize(string json)
        {
            ArgumentNullException.ThrowIfNull(json);
            return Canonicalize(Encoding.UTF8.GetBytes(json));
        }

        /// <summary>Serializes <paramref name="value"/> with its runtime type, then canonicalizes the result.</summary>
        public static string Serialize(object value, JsonSerializerOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(value);
            byte[] utf8 = JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), options);
            return Canonicalize(utf8);
        }

        private static void WriteElement(JsonElement element, StringBuilder sb)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    WriteObject(element, sb);
                    break;
                case JsonValueKind.Array:
                    sb.Append('[');
                    bool first = true;
                    foreach (var item in element.EnumerateArray())
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteElement(item, sb);
                    }
                    sb.Append(']');
                    break;
                case JsonValueKind.String:
                    WriteString(element.GetString()!, sb);
                    break;
                case JsonValueKind.Number:
                    sb.Append(FormatNumber(element.GetRawText()));
                    break;
                case JsonValueKind.True:
                    sb.Append("true");
                    break;
                case JsonValueKind.False:
                    sb.Append("false");
                    break;
                case JsonValueKind.Null:
                    sb.Append("null");
                    break;
                default:
                    throw new FormatException($"Unsupported JSON value kind '{element.ValueKind}'.");
            }
        }

        private static void WriteObject(JsonElement element, StringBuilder sb)
        {
            var members = new List<KeyValuePair<string, JsonElement>>();
            foreach (var property in element.EnumerateObject())
            {
                members.Add(new KeyValuePair<string, JsonElement>(property.Name, property.Value));
            }

            // RFC 8785 section 3.2.3: sort by UTF-16 code units, which is exactly ordinal string comparison in .NET.
            members.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));

            sb.Append('{');
            for (int i = 0; i < members.Count; i++)
            {
                if (i > 0)
                {
                    if (string.Equals(members[i - 1].Key, members[i].Key, StringComparison.Ordinal))
                    {
                        throw new FormatException($"Duplicate member name '{members[i].Key}' is not allowed.");
                    }
                    sb.Append(',');
                }
                WriteString(members[i].Key, sb);
                sb.Append(':');
                WriteElement(members[i].Value, sb);
            }
            sb.Append('}');
        }

        private static void WriteString(string value, StringBuilder sb)
        {
            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u00");
                            sb.Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                        }
                        else if (char.IsSurrogate(c))
                        {
                            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                            {
                                sb.Append(c).Append(value[i + 1]);
                                i++;
                            }
                            else
                            {
                                throw new FormatException("Strings must be valid Unicode (lone surrogate found).");
                            }
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }

        /// <summary>
        /// Formats a JSON number literal the way ECMAScript <c>Number.prototype.toString()</c> does (RFC 8785 section 3.2.2.3).
        /// </summary>
        internal static string FormatNumber(string literal)
        {
            if (!double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
            {
                throw new FormatException($"Number '{literal}' cannot be represented as a finite IEEE 754 double.");
            }
            return FormatDouble(value);
        }

        internal static string FormatDouble(double value)
        {
            if (!double.IsFinite(value))
            {
                throw new FormatException("NaN and Infinity are not valid JSON numbers.");
            }
            if (value == 0)
            {
                return "0"; // also covers negative zero
            }

            // "R" returns the shortest string that round-trips, which gives the same digits as ECMAScript.
            string r = value.ToString("R", CultureInfo.InvariantCulture);
            bool negative = r[0] == '-';
            if (negative) r = r[1..];

            int exponent = 0;
            string mantissa = r;
            int ePos = r.IndexOfAny(['E', 'e']);
            if (ePos >= 0)
            {
                exponent = int.Parse(r.AsSpan(ePos + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                mantissa = r[..ePos];
            }

            string digits;
            int integerLength;
            int dot = mantissa.IndexOf('.');
            if (dot >= 0)
            {
                digits = string.Concat(mantissa.AsSpan(0, dot), mantissa.AsSpan(dot + 1));
                integerLength = dot;
            }
            else
            {
                digits = mantissa;
                integerLength = mantissa.Length;
            }

            int leadingZeros = 0;
            while (leadingZeros < digits.Length - 1 && digits[leadingZeros] == '0') leadingZeros++;
            digits = digits[leadingZeros..].TrimEnd('0');
            integerLength -= leadingZeros;

            // value = digits * 10^(n - k), with k the number of significant digits (ECMAScript notation).
            int k = digits.Length;
            int n = integerLength + exponent;

            string result;
            if (k <= n && n <= 21)
            {
                result = digits + new string('0', n - k);
            }
            else if (0 < n && n <= 21)
            {
                result = string.Concat(digits.AsSpan(0, n), ".", digits.AsSpan(n));
            }
            else if (-6 < n && n <= 0)
            {
                result = "0." + new string('0', -n) + digits;
            }
            else
            {
                int e = n - 1;
                string exponentText = (e < 0 ? "-" : "+") + Math.Abs(e).ToString(CultureInfo.InvariantCulture);
                result = k == 1
                    ? digits + "e" + exponentText
                    : $"{digits[0]}.{digits[1..]}e{exponentText}";
            }

            return negative ? "-" + result : result;
        }
    }
}
