using System.ComponentModel.DataAnnotations.Schema;
using Provance.Core.Data;

namespace Provance.Storage.EntityFrameworkCore
{
    /// <summary>
    /// Storage shape of a ledger entry. Public so that it can be part of your own <c>DbContext</c> and migrations;
    /// do not modify these rows yourself.
    /// </summary>
    /// <remarks>Names are set with <c>[Column]</c>, which relational providers and the MongoDB provider both understand.</remarks>
    public sealed class LedgerEntryRecord
    {
        /// <summary>Entry id (primary key). Stored as <c>_id</c> on MongoDB, <c>id</c> on relational databases.</summary>
        public Guid Id { get; set; }

        /// <summary>Format version.</summary>
        [Column("format_version")]
        public int FormatVersion { get; set; }

        /// <summary>Ledger id.</summary>
        [Column("ledger_id")]
        public string LedgerId { get; set; } = string.Empty;

        /// <summary>Position in the chain (unique).</summary>
        [Column("seq")]
        public long Sequence { get; set; }

        /// <summary>UTC time, millisecond precision.</summary>
        [Column("sealed_at")]
        public DateTimeOffset SealedAt { get; set; }

        /// <summary>Event type.</summary>
        [Column("event_type")]
        public string EventType { get; set; } = string.Empty;

        /// <summary>Canonical JSON of the payload, stored as text so it round-trips byte for byte. <c>null</c> when redacted.</summary>
        [Column("payload")]
        public string? PayloadJson { get; set; }

        /// <summary>SHA-256 of the canonical payload.</summary>
        [Column("payload_hash")]
        public string PayloadHash { get; set; } = string.Empty;

        /// <summary>Hash of the previous entry.</summary>
        [Column("previous_hash")]
        public string PreviousHash { get; set; } = string.Empty;

        /// <summary>Signing key id.</summary>
        [Column("key_id")]
        public string KeyId { get; set; } = string.Empty;

        /// <summary>Entry hash.</summary>
        [Column("entry_hash")]
        public string Hash { get; set; } = string.Empty;

        /// <summary>Signature of the entry hash.</summary>
        [Column("signature")]
        public string Signature { get; set; } = string.Empty;

        /// <summary>When the payload was redacted, if it was.</summary>
        [Column("redacted_at")]
        public DateTimeOffset? RedactedAt { get; set; }

        internal static LedgerEntryRecord From(LedgerEntry entry) => new()
        {
            Id = entry.Id,
            FormatVersion = entry.FormatVersion,
            LedgerId = entry.LedgerId,
            Sequence = entry.Sequence,
            SealedAt = entry.Timestamp.ToUniversalTime(),
            EventType = entry.EventType,
            PayloadJson = entry.PayloadJson,
            PayloadHash = entry.PayloadHash,
            PreviousHash = entry.PreviousHash,
            KeyId = entry.KeyId,
            Hash = entry.Hash,
            Signature = entry.Signature
        };

        internal LedgerEntry ToEntry() => new()
        {
            Id = Id,
            FormatVersion = FormatVersion,
            LedgerId = LedgerId,
            Sequence = Sequence,
            Timestamp = SealedAt.ToUniversalTime(),
            EventType = EventType,
            PayloadJson = PayloadJson,
            PayloadHash = PayloadHash,
            PreviousHash = PreviousHash,
            KeyId = KeyId,
            Hash = Hash,
            Signature = Signature
        };
    }
}
