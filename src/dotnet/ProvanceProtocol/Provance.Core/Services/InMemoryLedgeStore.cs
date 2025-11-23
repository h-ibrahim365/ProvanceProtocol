using Provance.Core.Data;
using Provance.Core.Services.Interfaces;

namespace Provance.Core.Services
{
    /// <summary>
    /// An in-memory, thread-safe implementation of ILedgerStore, primarily for unit testing
    /// and demonstrating core functionality without a database dependency.
    /// </summary>
    public class InMemoryLedgerStore : ILedgerStore
    {
        // Thread-safe list to hold the ledger entries in memory.
        // Entries are added sequentially, maintaining the chain order.
        private readonly List<LedgerEntry> _ledger = [];
        // Semaphore to ensure only one writer or reader is accessing the list at a time.
        private readonly SemaphoreSlim _lock = new(1, 1);

        /// <summary>
        /// Retrieves the very last entry written to the ledger.
        /// </summary>
        public async Task<LedgerEntry?> GetLastEntryAsync()
        {
            await _lock.WaitAsync();
            try
            {
                // Returns the last element. If empty, returns null.
                return _ledger.LastOrDefault();
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Writes a sealed ledger entry to the permanent store (in-memory list).
        /// </summary>
        /// <param name="entry">The sealed entry to save.</param>
        public async Task WriteEntryAsync(LedgerEntry entry)
        {
            await _lock.WaitAsync();
            try
            {
                // In a real application, this would be the database write operation.
                _ledger.Add(entry);
                // Simulate an I/O operation delay
                await Task.Delay(5);
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Retrieves an entry by its ID. Used primarily for chain verification.
        /// </summary>
        /// <param name="entryId">The ID of the entry to retrieve.</param>
        /// <returns>A task that returns the LedgerEntry, or null if not found.</returns>
        public async Task<LedgerEntry?> GetEntryByIdAsync(Guid entryId)
        {
            await _lock.WaitAsync();
            try
            {
                return _ledger.FirstOrDefault(e => e.Id == entryId);
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Retrieves all entries from the ledger in sequential chain order.
        /// This is crucial for full chain integrity verification in the LedgerService.
        /// </summary>
        /// <returns>An ordered collection of all LedgerEntry objects.</returns>
        public async Task<IEnumerable<LedgerEntry>> GetAllEntriesAsync()
        {
            await _lock.WaitAsync();
            try
            {
                // Return a copy of the list to ensure the original list cannot be modified
                // while iterating outside the lock.
                return [.. _ledger];
            }
            finally
            {
                _lock.Release();
            }
        }
    }
}