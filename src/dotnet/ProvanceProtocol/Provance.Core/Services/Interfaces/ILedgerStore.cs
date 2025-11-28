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

        /// <summary>
        /// Attempts to acquire an exclusive, time-bound lease for a named resource, or renew an existing lease held by the same worker.
        /// This is used to implement the "Single Writer" safety lock.
        /// </summary>
        /// <param name="resourceName">The name of the resource to lock (e.g., "ledger_writer_lock_v1").</param>
        /// <param name="workerId">A unique ID identifying the worker instance attempting the operation (for ownership checking).</param>
        /// <param name="duration">The duration for which the lease should be acquired or extended.</param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        /// <returns>A task that returns <c>true</c> if the lease was successfully acquired or renewed; <c>false</c> otherwise (e.g., if another non-expired instance holds the lock).</returns>
        Task<bool> AcquireOrRenewLeaseAsync(string resourceName, string workerId, TimeSpan duration, CancellationToken cancellationToken = default);
    }
}