using Provance.Core.Data;
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

        /// <summary>Removes the payload of an entry while keeping the chain verifiable (GDPR erasure).</summary>
        Task<bool> RedactPayloadAsync(Guid id, CancellationToken cancellationToken = default);
    }
}
