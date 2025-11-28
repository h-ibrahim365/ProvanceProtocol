using Provance.Core.Data;
using Provance.Core.Services.Interfaces;

namespace Provance.Core.Services
{
    /// <summary>
    /// Thread-safe in-memory implementation of <see cref="ILedgerStore"/> for tests and demos.
    /// </summary>
    public class InMemoryLedgerStore : ILedgerStore
    {
        private readonly List<LedgerEntry> _ledger = [];
        private readonly SemaphoreSlim _lock = new(1, 1);

        // In-memory lease simulation for single-writer lock tests.
        private readonly Dictionary<string, (string WorkerId, DateTimeOffset ExpiresAt)> _leases = [];
        private readonly SemaphoreSlim _leaseLock = new(1, 1);

        /// <inheritdoc />
        public async Task<LedgerEntry?> GetLastEntryAsync(CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                // Use Sequence for deterministic ordering.
                return _ledger
                    .OrderByDescending(e => e.Sequence)
                    .ThenByDescending(e => e.Id)
                    .FirstOrDefault();
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

                // Simulate cancellable I/O latency.
                await Task.Delay(TimeSpan.FromMilliseconds(0.5), cancellationToken);
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
                // Return a snapshot ordered by Sequence to match verification behavior.
                return [.. _ledger
                    .OrderBy(e => e.Sequence)
                    .ThenBy(e => e.Id)];
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<bool> AcquireOrRenewLeaseAsync(
            string resourceName,
            string workerId,
            TimeSpan duration,
            CancellationToken cancellationToken = default)
        {
            await _leaseLock.WaitAsync(cancellationToken);
            try
            {
                var now = DateTimeOffset.UtcNow;
                var expiresAt = now.Add(duration);

                if (!_leases.TryGetValue(resourceName, out var currentLease))
                {
                    _leases[resourceName] = (workerId, expiresAt);
                    return true;
                }

                if (currentLease.WorkerId == workerId)
                {
                    _leases[resourceName] = (workerId, expiresAt);
                    return true;
                }

                if (currentLease.ExpiresAt <= now)
                {
                    _leases[resourceName] = (workerId, expiresAt);
                    return true;
                }

                return false;
            }
            finally
            {
                _leaseLock.Release();
            }
        }
    }
}