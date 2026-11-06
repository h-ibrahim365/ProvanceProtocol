using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Provance.Core.Canonicalization;
using Provance.Core.Data;

namespace Provance.Core.Cryptography
{
    /// <summary>
    /// Builds the exact bytes that PROVANCE hashes and signs. Public so that auditors and other
    /// implementations can reproduce the computation (see docs/SPEC.md and docs/test-vectors).
    /// </summary>
    public static class LedgerHashing
    {
        /// <summary>Timestamp format used in canonical bodies: UTC, millisecond precision, <c>Z</c> suffix.</summary>
        public const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

        /// <summary>Lowercase hex SHA-256 of the UTF-8 bytes of <paramref name="text"/>.</summary>
        public static string Sha256Hex(string text)
        {
            ArgumentNullException.ThrowIfNull(text);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        }

        /// <summary>Default genesis hash of a ledger: SHA-256 of <c>"provance:genesis:v1:" + ledgerId</c>.</summary>
        public static string DeriveGenesisHash(string ledgerId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(ledgerId);
            return Sha256Hex(ProvanceProtocol.GenesisPrefix + ledgerId);
        }

        /// <summary>Truncates a timestamp to the millisecond and converts it to UTC.</summary>
        public static DateTimeOffset NormalizeTimestamp(DateTimeOffset timestamp)
        {
            long ticks = timestamp.UtcTicks;
            return new DateTimeOffset(ticks - (ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
        }

        /// <summary>Formats a timestamp the way it appears in canonical bodies.</summary>
        public static string FormatTimestamp(DateTimeOffset timestamp)
            => NormalizeTimestamp(timestamp).UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

        /// <summary>
        /// Canonical envelope of an entry. Every field except <c>Hash</c>, <c>Signature</c> and the payload itself
        /// (which is covered by <c>PayloadHash</c>).
        /// </summary>
        public static string CanonicalEnvelope(LedgerEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (entry.Sequence < 1 || entry.Sequence > ProvanceProtocol.MaxSequence)
            {
                throw new ArgumentOutOfRangeException(nameof(entry), $"Sequence must be between 1 and {ProvanceProtocol.MaxSequence}.");
            }

            var buffer = new ArrayBufferWriter<byte>(512);
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("eventType", entry.EventType);
                writer.WriteString("id", entry.Id.ToString("D"));
                writer.WriteString("keyId", entry.KeyId);
                writer.WriteString("ledgerId", entry.LedgerId);
                writer.WriteString("payloadHash", entry.PayloadHash);
                writer.WriteString("previousHash", entry.PreviousHash);
                writer.WriteNumber("sequence", entry.Sequence);
                writer.WriteString("timestamp", FormatTimestamp(entry.Timestamp));
                writer.WriteNumber("v", entry.FormatVersion);
                writer.WriteEndObject();
            }
            return JsonCanonicalizer.Canonicalize(buffer.WrittenMemory);
        }

        /// <summary>Lowercase hex SHA-256 of the canonical envelope.</summary>
        public static string ComputeEntryHash(LedgerEntry entry) => Sha256Hex(CanonicalEnvelope(entry));

        /// <summary>Canonical body of a checkpoint (every field except <c>Signature</c>).</summary>
        public static string CanonicalCheckpoint(LedgerCheckpoint checkpoint)
        {
            ArgumentNullException.ThrowIfNull(checkpoint);
            var buffer = new ArrayBufferWriter<byte>(512);
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("createdAt", FormatTimestamp(checkpoint.CreatedAt));
                writer.WriteString("hash", checkpoint.Hash);
                writer.WriteString("keyId", checkpoint.KeyId);
                writer.WriteString("ledgerId", checkpoint.LedgerId);
                writer.WriteNumber("sequence", checkpoint.Sequence);
                writer.WriteString("type", "checkpoint");
                writer.WriteNumber("v", checkpoint.FormatVersion);
                writer.WriteEndObject();
            }
            return JsonCanonicalizer.Canonicalize(buffer.WrittenMemory);
        }

        /// <summary>Lowercase hex SHA-256 of the canonical checkpoint body.</summary>
        public static string ComputeCheckpointHash(LedgerCheckpoint checkpoint) => Sha256Hex(CanonicalCheckpoint(checkpoint));

        /// <summary>Decodes a 64 character lowercase hex hash into its 32 bytes.</summary>
        internal static byte[] HashToBytes(string hexHash)
        {
            if (!IsLowerHex(hexHash, 64))
            {
                throw new FormatException("Hash must be 64 lowercase hex characters.");
            }
            return Convert.FromHexString(hexHash);
        }

        /// <summary>Checks that <paramref name="value"/> is lowercase hex, optionally of an exact length.</summary>
        internal static bool IsLowerHex(string? value, int? length = null)
        {
            if (string.IsNullOrEmpty(value) || value.Length % 2 != 0) return false;
            if (length is int l && value.Length != l) return false;
            foreach (char c in value)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }
    }
}
