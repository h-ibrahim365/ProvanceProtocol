using Provance.Core.Data;

namespace Provance.Core.Services.Interfaces
{
    /// <summary>
    /// Defines the contract for persistence operations on the immutable ledger.
    /// This interface decouples the core protocol logic from the storage technology (SQL/NoSQL/File/etc.).
    /// </summary>
    public interface ILedgerStore
    {
        /// <summary>
        /// Retrieves the last entry written to the ledger (the current chain head).
        /// This is required to compute the next entry's <c>PreviousHash</c>.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the operation (e.g., shutdown).</param>
        /// <returns>A task that returns the last <see cref="LedgerEntry"/>, or <c>null</c> if the ledger is empty.</returns>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled.</exception>
        Task<LedgerEntry?> GetLastEntryAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Persists a sealed ledger entry.
        /// This operation is typically performed by the background consumer (<c>LedgerWriterService</c>).
        /// </summary>
        /// <param name="entry">The sealed entry to save.</param>
        /// <param name="cancellationToken">A token to cancel the operation (e.g., shutdown).</param>
        /// <returns>A task representing the completion of the write operation.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="entry"/> is null.</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled.</exception>
        Task WriteEntryAsync(LedgerEntry entry, CancellationToken cancellationToken = default);

        /// <summary>
        /// Retrieves an entry by its identifier. Mainly used for diagnostics / verification scenarios.
        /// </summary>
        /// <param name="entryId">The ID of the entry to retrieve.</param>
        /// <param name="cancellationToken">A token to cancel the operation (e.g., shutdown).</param>
        /// <returns>A task that returns the <see cref="LedgerEntry"/>, or <c>null</c> if not found.</returns>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled.</exception>
        Task<LedgerEntry?> GetEntryByIdAsync(Guid entryId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Retrieves all entries from the ledger in a deterministic order suitable for full chain verification.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the operation (e.g., shutdown).</param>
        /// <returns>A task that returns an ordered collection of all <see cref="LedgerEntry"/> objects.</returns>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled.</exception>
        Task<IEnumerable<LedgerEntry>> GetAllEntriesAsync(CancellationToken cancellationToken = default);
    }
}