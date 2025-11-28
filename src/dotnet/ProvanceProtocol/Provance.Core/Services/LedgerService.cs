using Microsoft.Extensions.Options;
using Provance.Core.Data;
using Provance.Core.Options;
using Provance.Core.Services.Interfaces;
using Provance.Core.Services.Internal;
using Provance.Core.Utilities;

namespace Provance.Core.Services
{
    /// <summary>
    /// The producer service for the PROVANCE protocol.
    /// It validates requests, creates transaction contexts, and queues them for the Single Writer.
    /// It also handles read-only verification operations.
    /// </summary>
    public class LedgerService(IOptions<ProvanceOptions> options, ILedgerStore store, IEntryQueue queue) : ILedgerService
    {
        private readonly ProvanceOptions _options = ValidateProtocolOptions(options.Value);
        private readonly ILedgerStore _store = store;
        private readonly IEntryQueue _queue = queue;

        /// <inheritdoc />
        public async Task<LedgerEntry> AddEntryAsync(
            string eventType,
            AuditedPayload payload,
            CancellationToken cancellationToken = default)
        {
            // 1. Validation
            if (string.IsNullOrWhiteSpace(eventType))
            {
                throw new ArgumentException("EventType cannot be null or empty.", nameof(eventType));
            }
            if (payload is null)
            {
                throw new ArgumentNullException(nameof(payload), "AuditedPayload cannot be null.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            // 2. Create the Transaction Context (Draft + Promise)
            // We do NOT calculate the hash here. We send the intent to the Single Writer.
            var context = new LedgerTransactionContext
            {
                EventType = eventType,
                Payload = payload
            };

            // 3. Enqueue the intent (may wait here if queue is full - Backpressure)
            await _queue.EnqueueAsync(context, cancellationToken);

            // 4. AWAIT ACKNOWLEDGMENT (Critical for v0.0.3 Correctness)
            // We wait until the Single Writer has sequenced, hashed, and persisted the entry.
            // This guarantees linear consistency to the client without race conditions.
            return await context.AckSource.Task.WaitAsync(cancellationToken);
        }

        // NOTE: SealEntryAsync has been removed from this service. 
        // In the Single Writer pattern, sealing is the exclusive responsibility of the background writer 
        // to ensure strict sequencing.

        /// <inheritdoc />
        public Task<LedgerEntry?> GetLastEntryAsync(CancellationToken cancellationToken = default)
        {
            return _store.GetLastEntryAsync(cancellationToken);
        }

        /// <inheritdoc />
        public async Task<(bool IsValid, string Reason)> VerifyChainIntegrityAsync(CancellationToken cancellationToken = default)
        {
            IEnumerable<LedgerEntry> rawEntries = await _store.GetAllEntriesAsync(cancellationToken);

            var allEntries = rawEntries
                .OrderBy(e => e.Sequence)
                .ThenBy(e => e.Id)
                .ToList();

            if (allEntries.Count == 0)
                return (true, "Ledger is empty, integrity assumed (Genesis Hash is the current reference).");

            // Sequence must be usable if we rely on it.
            if (allEntries.Any(e => e.Sequence <= 0))
                return (false, "Invalid Sequence detected (Sequence must be > 0).");

            if (allEntries.Select(e => e.Sequence).Distinct().Count() != allEntries.Count)
                return (false, "Duplicate Sequence values detected (Sequence must be unique).");

            string expectedPreviousHash = _options.GenesisHash.ToLowerInvariant();

            for (int i = 0; i < allEntries.Count; i++)
            {
                if ((i & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();

                var entry = allEntries[i];

                if (!string.Equals(entry.PreviousHash, expectedPreviousHash, StringComparison.OrdinalIgnoreCase))
                {
                    return (false,
                        $"Chain link broken at entry ID {entry.Id}. Expected PreviousHash: {expectedPreviousHash}, but found: {entry.PreviousHash}.");
                }

                string calculatedHash = HashUtility.CalculateHash(entry, _options.SecretKey);

                if (!string.Equals(calculatedHash, entry.CurrentHash, StringComparison.Ordinal))
                {
                    return (false,
                        $"Data tampering detected in entry ID {entry.Id}. Calculated hash: {calculatedHash} does not match stored hash: {entry.CurrentHash}.");
                }

                expectedPreviousHash = calculatedHash;
            }

            return (true, "Chain integrity successfully verified. All entries are valid and authentically signed.");
        }

        private static ProvanceOptions ValidateProtocolOptions(ProvanceOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.GenesisHash))
            {
                throw new InvalidOperationException("PROVANCE Protocol requires a GenesisHash.");
            }
            if (options.GenesisHash.Length != 64 || !HashUtility.IsValidHexString(options.GenesisHash))
            {
                throw new InvalidOperationException("GenesisHash must be a valid 64-character hex hash string.");
            }

            if (string.IsNullOrWhiteSpace(options.SecretKey))
            {
                throw new InvalidOperationException("PROVANCE Protocol requires a SecretKey for HMAC signing.");
            }

            if (options.SecretKey == "DEFAULT_INSECURE_KEY_CHANGE_ME")
            {
                // TODO: throw exception when PROD env
            }

            return options;
        }
    }
}