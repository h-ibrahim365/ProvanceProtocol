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
        /// Creates a transaction draft from the payload, enqueues it to the Single Writer service,
        /// and awaits its successful sealing and persistence.
        /// </summary>
        /// <remarks>
        /// This operation guarantees linear consistency by delegating the cryptographic sealing
        /// to a single background writer. It waits for an acknowledgment (ACK) before returning
        /// the fully populated <see cref="LedgerEntry"/>.
        /// </remarks>
        /// <param name="eventType">The type of event being audited (e.g., "USER_LOGIN").</param>
        /// <param name="payload">The <see cref="AuditedPayload"/> instance containing the audit details.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>
        /// A task that returns the sealed <see cref="LedgerEntry"/> with <c>PreviousHash</c> and <c>CurrentHash</c> populated.
        /// </returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="eventType"/> is null/empty/whitespace.</exception>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="payload"/> is null.</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled.</exception>
        Task<LedgerEntry> AddEntryAsync(string eventType, AuditedPayload payload, CancellationToken cancellationToken = default);

        /// <summary>
        /// Retrieves the last entry written to the ledger to establish the chain head.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>A task that returns the last <see cref="LedgerEntry"/>, or <c>null</c> if the ledger is empty.</returns>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled.</exception>
        Task<LedgerEntry?> GetLastEntryAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Verifies the complete cryptographic integrity of the ledger from the Genesis Hash to the latest entry.
        /// </summary>
        /// <param name="cancellationToken">
        /// A token to cancel the verification operation. This is useful when verifying
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