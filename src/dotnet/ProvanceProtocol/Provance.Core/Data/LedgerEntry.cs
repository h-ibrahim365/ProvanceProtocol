using System.Text.Json.Serialization;

namespace Provance.Core.Data
{
    /// <summary>
    /// Represents a unique entry in the immutable data ledger.
    /// Each entry is cryptographically linked to the previous one via its hash.
    /// </summary>
    public class LedgerEntry
    {
        /// <summary>
        /// Unique identifier for the entry.
        /// </summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>
        /// Precise timestamp of the recording, critical for audit and included in the hash.
        /// </summary>
        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// Hash of the previous entry in the chain. Key element for immutability.
        /// </summary>
        public required string PreviousHash { get; set; }

        /// <summary>
        /// The calculated cryptographic hash of this entry.
        /// This field is omitted when calculating the entry's own hash.
        /// </summary>
        [JsonIgnore]
        public string? CurrentHash { get; set; }

        /// <summary>
        /// Type of audited event (e.g., "USER_LOGIN", "DATA_DELETION").
        /// </summary>
        public required string EventType { get; set; }

        /// <summary>
        /// The content of the audited event (business data).
        /// </summary>
        public required AuditedPayload Payload { get; set; }
    }
}