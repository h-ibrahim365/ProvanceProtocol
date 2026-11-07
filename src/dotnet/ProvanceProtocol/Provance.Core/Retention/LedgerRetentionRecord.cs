using System.Text.Json;
using Provance.Core.Canonicalization;
using Provance.Core.Cryptography;

namespace Provance.Core.Retention
{
    /// <summary>
    /// The entry the library appends before removing old entries from the primary store (see docs/SPEC.md,
    /// section "Retention"). Its payload names the first entry that stays and that entry's hash. Because the
    /// record is chained and signed like any other entry, a verifier accepts a ledger that starts after
    /// sequence 1 only when such a record vouches for its first entry.
    /// </summary>
    public static class LedgerRetentionRecord
    {
        /// <summary>Event type of retention records. Event types starting with <c>provance.</c> are reserved.</summary>
        public const string EventType = "provance.retention";

        /// <summary>Prefix reserved for entries written by the library itself.</summary>
        public const string ReservedPrefix = "provance.";

        /// <summary>Canonical payload of a retention record.</summary>
        public static string CreatePayload(long firstSequence, string firstHash)
        {
            if (firstSequence < 2)
            {
                throw new ArgumentOutOfRangeException(nameof(firstSequence), "Retention keeps entries from sequence 2 or later.");
            }
            if (!LedgerHashing.IsLowerHex(firstHash, 64))
            {
                throw new ArgumentException("Hash must be 64 lowercase hex characters.", nameof(firstHash));
            }
            return JsonCanonicalizer.Canonicalize($"{{\"firstHash\":\"{firstHash}\",\"firstSequence\":{firstSequence}}}");
        }

        /// <summary>Reads the payload of a retention record. Returns <c>false</c> when it is missing or malformed.</summary>
        public static bool TryParse(string? payloadJson, out long firstSequence, out string firstHash)
        {
            firstSequence = 0;
            firstHash = string.Empty;
            if (payloadJson is null)
            {
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(payloadJson);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("firstSequence", out var sequence) || !sequence.TryGetInt64(out firstSequence)
                    || !root.TryGetProperty("firstHash", out var hash) || hash.ValueKind != JsonValueKind.String)
                {
                    return false;
                }
                firstHash = hash.GetString()!;
                return firstSequence > 1 && LedgerHashing.IsLowerHex(firstHash, 64);
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }
}
