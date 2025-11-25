using Microsoft.Extensions.Options;
using Provance.Core.Data;
using Provance.Core.Options;
using Provance.Core.Services.Interfaces;
using Provance.Core.Utilities;

namespace Provance.Core.Services
{
    /// <summary>
    /// The core implementation of the PROVANCE protocol logic.
    /// Handles chaining, hashing, and integrity verification.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the LedgerService.
    /// </remarks>
    /// <param name="options">Protocol configuration options (e.g., Genesis Hash).</param>
    /// <param name="store">The persistence layer to read/write entries.</param>
    /// <param name="queue">The non-blocking queue to push sealed entries to.</param>
    public class LedgerService(IOptions<ProvanceOptions> options, ILedgerStore store, IEntryQueue queue) : ILedgerService
    {
        private readonly ProvanceOptions _options = ValidateProtocolOptions(options.Value);
        private readonly ILedgerStore _store = store;
        private readonly IEntryQueue _queue = queue;

        /// <inheritdoc />
        public Task<LedgerEntry> AddEntryAsync(
            string eventType,
            AuditedPayload payload,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(eventType))
            {
                throw new ArgumentException("EventType cannot be null or empty.", nameof(eventType));
            }
            if (payload is null)
            {
                throw new ArgumentNullException(nameof(payload), "AuditedPayload cannot be null.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var rawEntry = new LedgerEntry
            {
                Timestamp = DateTimeOffset.UtcNow,
                PreviousHash = string.Empty, // Temporary, will be set in SealEntryAsync
                EventType = eventType,
                Payload = payload,
            };

            return SealEntryAsync(rawEntry, cancellationToken);
        }

        /// <inheritdoc />
        public async Task<LedgerEntry> SealEntryAsync(LedgerEntry entry, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 1. Get the Previous Hash (Chain Link)
            LedgerEntry? lastEntry = await _store.GetLastEntryAsync(cancellationToken);

            // Set PreviousHash: use last entry's hash, or GenesisHash if ledger is empty.
            entry.PreviousHash = lastEntry?.CurrentHash ?? _options.GenesisHash;

            cancellationToken.ThrowIfCancellationRequested();

            // 2. Calculate CurrentHash (HMAC)
            entry.CurrentHash = HashUtility.CalculateHash(entry, _options.SecretKey);

            // 3. Enqueue for background persistence (may await due to backpressure)
            await _queue.EnqueueEntryAsync(entry, cancellationToken);

            return entry;
        }

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
                .OrderBy(e => e.Timestamp)
                .ThenBy(e => e.Id)
                .ToList();

            if (allEntries.Count == 0)
            {
                return (true, "Ledger is empty, integrity assumed (Genesis Hash is the current reference).");
            }

            string expectedPreviousHash = _options.GenesisHash;

            for (int i = 0; i < allEntries.Count; i++)
            {
                if ((i & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();

                var entry = allEntries[i];

                if (entry.PreviousHash != expectedPreviousHash)
                {
                    return (false,
                        $"Chain link broken at entry ID {entry.Id}. Expected PreviousHash: {expectedPreviousHash}, but found: {entry.PreviousHash}.");
                }

                string calculatedHash = HashUtility.CalculateHash(entry, _options.SecretKey);

                if (calculatedHash != entry.CurrentHash)
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