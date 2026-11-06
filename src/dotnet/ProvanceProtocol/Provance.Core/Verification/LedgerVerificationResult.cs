using Provance.Core.Data;

namespace Provance.Core.Verification
{
    /// <summary>Reason of a verification failure.</summary>
    public enum VerificationFailure
    {
        /// <summary>No failure.</summary>
        None,

        /// <summary>A sequence number is missing, duplicated or out of order (deleted or inserted entry).</summary>
        SequenceGap,

        /// <summary><c>PreviousHash</c> does not match the hash of the previous entry.</summary>
        BrokenLink,

        /// <summary>The recomputed envelope hash differs from the stored <c>Hash</c> (a signed field was modified).</summary>
        HashMismatch,

        /// <summary>The payload does not match <c>PayloadHash</c>.</summary>
        PayloadHashMismatch,

        /// <summary>The signature is not valid for the declared key.</summary>
        InvalidSignature,

        /// <summary>The entry was signed with a key the verifier does not know.</summary>
        UnknownKey,

        /// <summary>The entry belongs to another ledger.</summary>
        LedgerIdMismatch,

        /// <summary>The entry uses a format version this library does not understand.</summary>
        UnsupportedFormat,

        /// <summary>The trusted checkpoint is invalid (signature, ledger id) or does not match the stored entry at its sequence.</summary>
        CheckpointMismatch,

        /// <summary>The ledger ends before the sequence of the trusted checkpoint: entries were removed from the end.</summary>
        Truncated
    }

    /// <summary>Options of <see cref="LedgerVerifier.VerifyAsync"/>.</summary>
    public sealed record LedgerVerificationOptions
    {
        /// <summary>
        /// A checkpoint obtained from a trusted place. When set, the ledger must contain that exact entry,
        /// which detects truncation and full rewrites.
        /// </summary>
        public LedgerCheckpoint? TrustedCheckpoint { get; init; }

        /// <summary>
        /// When <c>true</c> and <see cref="TrustedCheckpoint"/> is set, verification starts at the checkpoint
        /// instead of the genesis entry (incremental verification).
        /// </summary>
        public bool StartFromCheckpoint { get; init; }

        /// <summary>Throw <see cref="Exceptions.LedgerTamperedException"/> instead of returning a failed result.</summary>
        public bool ThrowOnFailure { get; init; }
    }

    /// <summary>Result of a ledger verification.</summary>
    public sealed record LedgerVerificationResult
    {
        /// <summary><c>true</c> when every verified entry is consistent.</summary>
        public required bool IsValid { get; init; }

        /// <summary>Reason of the failure, <see cref="VerificationFailure.None"/> when valid.</summary>
        public required VerificationFailure Failure { get; init; }

        /// <summary>Human readable explanation.</summary>
        public required string Message { get; init; }

        /// <summary>Sequence of the first entry that failed, if any.</summary>
        public long? FailedAtSequence { get; init; }

        /// <summary>Id of the first entry that failed, if any.</summary>
        public Guid? FailedEntryId { get; init; }

        /// <summary>Number of entries checked.</summary>
        public long EntriesVerified { get; init; }

        /// <summary>Number of checked entries whose payload was redacted.</summary>
        public long RedactedPayloads { get; init; }

        /// <summary>Sequence of the last valid entry (0 when none).</summary>
        public long LastSequence { get; init; }

        /// <summary>
        /// Hash of the last valid entry. Compare it with an external checkpoint: without one, removing
        /// entries from the end of the ledger cannot be detected.
        /// </summary>
        public string? LastHash { get; init; }
    }
}
