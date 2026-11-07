using Provance.Core.Archive;
using Provance.Core.Comparison;
using Provance.Core.Data;
using Provance.Core.Retention;
using Provance.Core.Verification;

namespace Provance.Core.Services.Interfaces
{
    /// <summary>
    /// Entry point of the library: append audit events, read them back, verify the chain and create checkpoints.
    /// </summary>
    public interface ILedgerService
    {
        /// <summary>
        /// Serializes and canonicalizes <paramref name="payload"/>, hands it to the writer and waits until the
        /// sealed entry is persisted.
        /// </summary>
        /// <remarks>
        /// The payload is serialized with its runtime type, so properties of derived types are signed.
        /// Canceling <paramref name="cancellationToken"/> stops the wait, not the write: an entry already
        /// queued may still be persisted.
        /// </remarks>
        /// <exception cref="ArgumentException"><paramref name="eventType"/> is empty or longer than 200 characters.</exception>
        /// <exception cref="FormatException">The payload does not serialize to valid canonical JSON.</exception>
        /// <exception cref="Exceptions.LedgerOverloadedException">The queue is full and fail fast is configured.</exception>
        /// <exception cref="Exceptions.LedgerUnavailableException">The writer is stopped or the store kept failing.</exception>
        Task<LedgerEntry> AddEntryAsync<TPayload>(string eventType, TPayload payload, CancellationToken cancellationToken = default)
            where TPayload : notnull;

        /// <summary>Returns the last entry of the chain, or <c>null</c> if the ledger is empty.</summary>
        Task<LedgerEntry?> GetHeadAsync(CancellationToken cancellationToken = default);

        /// <summary>Returns an entry by id, or <c>null</c>.</summary>
        Task<LedgerEntry?> GetEntryAsync(Guid id, CancellationToken cancellationToken = default);

        /// <summary>Verifies the chain. See <see cref="LedgerVerificationOptions"/> for checkpoint based verification.</summary>
        Task<LedgerVerificationResult> VerifyAsync(LedgerVerificationOptions? options = null, CancellationToken cancellationToken = default);

        /// <summary>Creates a signed checkpoint of the current head, or returns <c>null</c> if the ledger is empty.</summary>
        Task<LedgerCheckpoint?> CreateCheckpointAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Removes the payload of an entry while keeping the chain verifiable (GDPR erasure). A signed redaction record
        /// is appended first, so verification can tell this erasure from a payload deleted directly in the database.
        /// When an archive is configured, the payload is removed from the archive copy too.
        /// </summary>
        /// <returns><c>true</c> if the entry exists, in the primary store or in the archive.</returns>
        /// <exception cref="Exceptions.LedgerArchiveException">The payload was removed from the primary store but not from the archive.</exception>
        Task<bool> RedactPayloadAsync(Guid id, CancellationToken cancellationToken = default);

        /// <summary>Name of the ledger in this application: empty for the default ledger, otherwise the name given to <c>AddProvance(name, ...)</c>.</summary>
        string Name => string.Empty;

        /// <summary>State of the copy to the archive, or <c>null</c> when no archive is configured.</summary>
        LedgerArchiveStatus? GetArchiveStatus() => null;

        /// <summary>
        /// Compares the primary store with the archive, field by field, after copying the latest entries.
        /// </summary>
        /// <param name="maxDifferences">Stop listing differences after this many. Default 1000.</param>
        /// <param name="cancellationToken">Cancels the comparison.</param>
        /// <exception cref="InvalidOperationException">No archive is configured.</exception>
        Task<LedgerComparison> CompareWithArchiveAsync(int maxDifferences = 1000, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("This ledger service does not support archives.");

        /// <summary>
        /// Removes entries older than <paramref name="keepFor"/> from the primary store, keeping the chain verifiable:
        /// the primary store is verified first, the removed entries must be identical in the archive
        /// (unless <see cref="Options.LedgerRetentionOptions.RequireArchive"/> is <c>false</c>), and a signed retention
        /// record is appended before anything is deleted. See docs/RETENTION.md.
        /// </summary>
        /// <exception cref="NotSupportedException">The store cannot delete entries (it does not implement <see cref="IPrunableLedgerStore"/>).</exception>
        Task<LedgerRetentionResult> ApplyRetentionAsync(TimeSpan keepFor, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("This ledger service does not support retention.");
    }
}
