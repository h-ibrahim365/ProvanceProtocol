using Provance.Core.Data;

namespace Provance.Core.Services.Interfaces
{
    /// <summary>
    /// Defines the contract for persistence operations on the immutable ledger.
    /// This interface decouples the core logic from the actual storage technology (e.g., SQL, NoSQL).
    /// </summary>
    public interface ILedgerStore
    {
        /// <summary>
        /// Retrieves the very last entry written to the ledger to establish the chain.
        /// This is critical for finding the PreviousHash for the next entry.
        /// </summary>
        /// <returns>A task that returns the last LedgerEntry, or null if the ledger is empty.</returns>
        Task<LedgerEntry?> GetLastEntryAsync();

        /// <summary>
        /// Writes a sealed ledger entry to the permanent store.
        /// This operation should be performed by the background consumer service.
        /// </summary>
        /// <param name="entry">The sealed entry to save.</param>
        /// <returns>A task representing the completion of the write operation.</returns>
        Task WriteEntryAsync(LedgerEntry entry);

        /// <summary>
        /// Retrieves an entry by its ID. Used primarily for chain verification.
        /// </summary>
        /// <param name="entryId">The ID of the entry to retrieve.</param>
        /// <returns>A task that returns the LedgerEntry, or null if not found.</returns>
        Task<LedgerEntry?> GetEntryByIdAsync(Guid entryId);

        /// <summary>
        /// Retrieves all entries from the ledger in sequential chain order (e.g., by ID or timestamp).
        /// This is mandatory for full chain integrity verification.
        /// </summary>
        /// <returns>A task that returns an ordered collection of all LedgerEntry objects.</returns>
        Task<IEnumerable<LedgerEntry>> GetAllEntriesAsync();

        // NOTE: Additional methods for Merkle Tree indexing, pruning, and verification will be added later.
    }
}