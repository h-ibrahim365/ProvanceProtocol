using Provance.Core.Data;
using Provance.Core.Services.Interfaces;

namespace Provance.Core.Services
{
    /// <summary>
    /// An in-memory, thread-safe implementation of <see cref="ILedgerStore"/>, primarily for tests
    /// and demos without a database dependency.
    /// </summary>
    public class InMemoryLedgerStore : ILedgerStore
    {
        private readonly List<LedgerEntry> _ledger = [];
        private readonly SemaphoreSlim _lock = new(1, 1);

        /// <inheritdoc />
        public async Task<LedgerEntry?> GetLastEntryAsync(CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                return _ledger.LastOrDefault();
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <inheritdoc />
        public async Task WriteEntryAsync(LedgerEntry entry, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entry);

            await _lock.WaitAsync(cancellationToken);
            try
            {
                _ledger.Add(entry);

                // Simulate I/O (cancellable)
                await Task.Delay(5, cancellationToken);
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <inheritdoc />
        public async Task<LedgerEntry?> GetEntryByIdAsync(Guid entryId, CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                return _ledger.FirstOrDefault(e => e.Id == entryId);
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <inheritdoc />
        public async Task<IEnumerable<LedgerEntry>> GetAllEntriesAsync(CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                return [.. _ledger];
            }
            finally
            {
                _lock.Release();
            }
        }
    }
}