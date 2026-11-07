using System.Runtime.CompilerServices;
using Provance.Core.Data;
using Provance.Core.Exceptions;
using Provance.Core.Services.Interfaces;

namespace Provance.Core.Services
{
    /// <summary>
    /// Thread safe in-memory store for tests and demos. Everything is lost when the process stops.
    /// Register it explicitly with <c>UseInMemoryStore()</c>; it is never used as a silent fallback.
    /// </summary>
    public sealed class InMemoryLedgerStore : IPrunableLedgerStore
    {
        private readonly object _gate = new();
        private readonly List<LedgerEntry> _entries = []; // ordered by sequence
        private readonly Dictionary<Guid, int> _indexById = [];
        private readonly Dictionary<long, int> _indexBySequence = [];

        /// <summary>Number of stored entries.</summary>
        public int Count
        {
            get { lock (_gate) return _entries.Count; }
        }

        /// <inheritdoc />
        public Task<LedgerEntry?> GetHeadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return Task.FromResult(_entries.Count == 0 ? null : _entries[^1]);
            }
        }

        /// <inheritdoc />
        public Task AppendAsync(IReadOnlyList<LedgerEntry> entries, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entries);
            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                // All or nothing: check the whole batch before writing.
                var seenIds = new HashSet<Guid>();
                long? previousSequence = null;
                foreach (var entry in entries)
                {
                    if (_indexBySequence.ContainsKey(entry.Sequence))
                        throw new LedgerConcurrencyException($"An entry with sequence {entry.Sequence} already exists.");
                    if (_indexById.ContainsKey(entry.Id) || !seenIds.Add(entry.Id))
                        throw new LedgerConcurrencyException($"An entry with id {entry.Id} already exists.");
                    if (previousSequence is long p && entry.Sequence != p + 1)
                        throw new ArgumentException("Entries of a batch must have contiguous ascending sequences.", nameof(entries));
                    previousSequence = entry.Sequence;
                }

                foreach (var entry in entries)
                {
                    Insert(entry);
                }
            }
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<LedgerEntry?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return Task.FromResult(_indexById.TryGetValue(id, out int i) ? _entries[i] : null);
            }
        }

        /// <inheritdoc />
        public async IAsyncEnumerable<LedgerEntry> ReadAsync(long fromSequence = 1, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LedgerEntry[] snapshot;
            lock (_gate)
            {
                snapshot = _entries.Where(e => e.Sequence >= fromSequence).ToArray();
            }

            foreach (var entry in snapshot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return entry;
            }
            await Task.CompletedTask.ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task<bool> RedactPayloadAsync(Guid id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_indexById.TryGetValue(id, out int i))
                {
                    return Task.FromResult(false);
                }
                _entries[i] = _entries[i] with { PayloadJson = null };
                return Task.FromResult(true);
            }
        }

        /// <inheritdoc />
        public Task<long> DeleteBeforeAsync(long sequence, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                int removed = _entries.RemoveAll(e => e.Sequence < sequence);
                if (removed > 0)
                {
                    Reindex();
                }
                return Task.FromResult((long)removed);
            }
        }

        /// <summary>
        /// Test helper: replaces a stored entry as an attacker with database access would.
        /// No check is performed.
        /// </summary>
        internal void Tamper(long sequence, Func<LedgerEntry, LedgerEntry> change)
        {
            lock (_gate)
            {
                int i = _indexBySequence[sequence];
                _entries[i] = change(_entries[i]);
            }
        }

        /// <summary>Test helper: deletes entries as an attacker with database access would.</summary>
        internal void Delete(Func<LedgerEntry, bool> predicate)
        {
            lock (_gate)
            {
                _entries.RemoveAll(e => predicate(e));
                Reindex();
            }
        }

        private void Insert(LedgerEntry entry)
        {
            if (_entries.Count == 0 || _entries[^1].Sequence < entry.Sequence)
            {
                _entries.Add(entry);
                _indexById[entry.Id] = _entries.Count - 1;
                _indexBySequence[entry.Sequence] = _entries.Count - 1;
                return;
            }

            int position = _entries.FindIndex(e => e.Sequence > entry.Sequence);
            _entries.Insert(position, entry);
            Reindex();
        }

        private void Reindex()
        {
            _indexById.Clear();
            _indexBySequence.Clear();
            for (int i = 0; i < _entries.Count; i++)
            {
                _indexById[_entries[i].Id] = i;
                _indexBySequence[_entries[i].Sequence] = i;
            }
        }
    }
}
