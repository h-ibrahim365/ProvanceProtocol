using System.Text.Json;

namespace Provance.Core.Data
{
    /// <summary>
    /// A sealed, immutable entry of a PROVANCE ledger.
    /// </summary>
    /// <remarks>
    /// <para><see cref="Hash"/> is the SHA-256 of the canonical envelope (every field except
    /// <see cref="Hash"/>, <see cref="Signature"/> and <see cref="PayloadJson"/>). The payload is covered
    /// through <see cref="PayloadHash"/>, which is why a payload can be redacted without breaking the chain.</para>
    /// <para>See docs/SPEC.md for the exact byte layout.</para>
    /// </remarks>
    public sealed record LedgerEntry
    {
        /// <summary>Format version of the signed envelope. Current value: <see cref="ProvanceProtocol.FormatVersion"/>.</summary>
        public required int FormatVersion { get; init; }

        /// <summary>Identifier of the ledger this entry belongs to. Prevents moving entries between ledgers.</summary>
        public required string LedgerId { get; init; }

        /// <summary>Position in the chain, starting at 1, without gaps.</summary>
        public required long Sequence { get; init; }

        /// <summary>Unique identifier of the entry.</summary>
        public required Guid Id { get; init; }

        /// <summary>UTC time at which the writer sealed the entry, truncated to the millisecond.</summary>
        public required DateTimeOffset Timestamp { get; init; }

        /// <summary>Application defined event type, for example <c>USER_LOGIN</c>.</summary>
        public required string EventType { get; init; }

        /// <summary>Canonical JSON (RFC 8785) of the payload, or <c>null</c> when the payload was redacted.</summary>
        public string? PayloadJson { get; init; }

        /// <summary>Lowercase hex SHA-256 of the UTF-8 bytes of the canonical payload.</summary>
        public required string PayloadHash { get; init; }

        /// <summary><see cref="Hash"/> of the previous entry, or the genesis hash for the first entry.</summary>
        public required string PreviousHash { get; init; }

        /// <summary>Identifier of the key that produced <see cref="Signature"/>.</summary>
        public required string KeyId { get; init; }

        /// <summary>Lowercase hex SHA-256 of the canonical envelope.</summary>
        public required string Hash { get; init; }

        /// <summary>Lowercase hex signature of the 32 raw bytes of <see cref="Hash"/>.</summary>
        public required string Signature { get; init; }

        /// <summary><c>true</c> when the payload has been removed (for example after a GDPR erasure request).</summary>
        public bool IsRedacted => PayloadJson is null;

        /// <summary>Deserializes the payload. Returns <c>default</c> when the payload was redacted.</summary>
        public T? GetPayload<T>(JsonSerializerOptions? options = null)
            => PayloadJson is null ? default : JsonSerializer.Deserialize<T>(PayloadJson, options ?? ProvanceProtocol.WebSerializerOptions);
    }
}
