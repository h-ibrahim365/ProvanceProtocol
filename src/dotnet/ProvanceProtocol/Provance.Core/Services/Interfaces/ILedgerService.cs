using Provance.Core.Data;

namespace Provance.Core.Services.Interfaces
{
    /// <summary>
    /// Defines the basic operations for managing the PROVANCE tamper-evident audit ledger.
    /// This is the primary contract for external consumption.
    /// </summary>
    public interface ILedgerService
    {
        /// <summary>
        /// Creates a <see cref="LedgerEntry"/> from a fully constructed <see cref="AuditedPayload"/>,
        /// seals it by computing a <c>CurrentHash</c> (HMAC-SHA256 over a canonical representation),
        /// and enqueues it for background persistence (Producer/Consumer pipeline).
        /// </summary>
        /// <remarks>
        /// The payload may be a derived type (e.g., <c>HttpContextAuditedPayload</c>).
        /// Enqueueing may await if the queue is bounded and under backpressure.
        /// </remarks>
        /// <param name="eventType">The type of event being audited (e.g., "USER_LOGIN").</param>
        /// <param name="payload">The <see cref="AuditedPayload"/> instance containing the audit details.</param>
        /// <param name="cancellationToken">A token to cancel the operation (e.g., during shutdown/request abort).</param>
        /// <returns>
        /// A task that returns the sealed <see cref="LedgerEntry"/> with <c>PreviousHash</c> and <c>CurrentHash</c> populated.
        /// </returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="eventType"/> is null/empty/whitespace.</exception>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="payload"/> is null.</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled.</exception>
        Task<LedgerEntry> AddEntryAsync(string eventType, AuditedPayload payload, CancellationToken cancellationToken = default);

        /// <summary>
        /// Seals a raw <see cref="LedgerEntry"/> by linking its <c>PreviousHash</c> to the current chain head,
        /// computing its <c>CurrentHash</c> (HMAC-SHA256 over a canonical representation),
        /// and enqueuing it for background persistence.
        /// </summary>
        /// <remarks>
        /// Enqueueing may await if the queue is bounded and under backpressure.
        /// </remarks>
        /// <param name="entry">The raw <see cref="LedgerEntry"/> data to be sealed and processed.</param>
        /// <param name="cancellationToken">A token to cancel the operation (e.g., during shutdown).</param>
        /// <returns>
        /// A task that returns the sealed <see cref="LedgerEntry"/> with <c>PreviousHash</c> and <c>CurrentHash</c> populated.
        /// </returns>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled.</exception>
        Task<LedgerEntry> SealEntryAsync(LedgerEntry entry, CancellationToken cancellationToken = default);

        /// <summary>
        /// Retrieves the last entry written to the ledger to establish the chain head.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the operation (e.g., shutdown/request abort).</param>
        /// <returns>A task that returns the last <see cref="LedgerEntry"/>, or <c>null</c> if the ledger is empty.</returns>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled.</exception>
        Task<LedgerEntry?> GetLastEntryAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Verifies the complete cryptographic integrity of the ledger from the Genesis Hash to the latest entry.
        /// </summary>
        /// <param name="cancellationToken">
        /// A token to cancel the verification operation (e.g., request abort/shutdown). This is useful when verifying
        /// large ledgers where the operation may take noticeable time.
        /// </param>
        /// <returns>
        /// A tuple containing a boolean indicating validity and a string containing the reason for failure (if invalid),
        /// or a success message (if valid).
        /// </returns>
        /// <exception cref="OperationCanceledException">
        /// Thrown when <paramref name="cancellationToken"/> is canceled.
        /// </exception>
        Task<(bool IsValid, string Reason)> VerifyChainIntegrityAsync(CancellationToken cancellationToken = default);
    }
}