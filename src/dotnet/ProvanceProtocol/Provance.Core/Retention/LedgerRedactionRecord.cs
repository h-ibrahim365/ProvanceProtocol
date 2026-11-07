using System.Globalization;
using System.Text.Json;
using Provance.Core.Canonicalization;

namespace Provance.Core.Retention
{
    /// <summary>
    /// The entry the library appends before erasing a payload (see docs/SPEC.md, section 6). It names the erased entry,
    /// so a verifier can tell an authorized erasure from a payload removed directly in the database.
    /// </summary>
    public static class LedgerRedactionRecord
    {
        /// <summary>Event type of redaction records.</summary>
        public const string EventType = "provance.redaction";

        /// <summary>Canonical payload of a redaction record.</summary>
        public static string CreatePayload(long sequence, Guid entryId)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
            return JsonCanonicalizer.Canonicalize(string.Create(CultureInfo.InvariantCulture,
                $"{{\"entryId\":\"{entryId:D}\",\"sequence\":{sequence}}}"));
        }

        /// <summary>Reads the payload of a redaction record. Returns <c>false</c> when it is missing or malformed.</summary>
        public static bool TryParse(string? payloadJson, out long sequence, out Guid entryId)
        {
            sequence = 0;
            entryId = Guid.Empty;
            if (payloadJson is null)
            {
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(payloadJson);
                var root = document.RootElement;
                return root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("sequence", out var s) && s.TryGetInt64(out sequence) && sequence >= 1
                    && root.TryGetProperty("entryId", out var id) && id.ValueKind == JsonValueKind.String
                    && Guid.TryParseExact(id.GetString(), "D", out entryId);
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }
}
