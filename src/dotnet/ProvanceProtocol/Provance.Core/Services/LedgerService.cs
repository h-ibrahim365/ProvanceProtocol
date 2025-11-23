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
        private readonly ProvanceOptions _options = ValidateGenesisHash(options.Value);
        private readonly ILedgerStore _store = store;
        private readonly IEntryQueue _queue = queue;

        /// <summary>
        /// Creates a minimal LedgerEntry from structured event data, seals it, and enqueues it for writing.
        /// This method acts as a convenience wrapper for external consumers (like API endpoints).
        /// </summary>
        /// <param name="eventType">The type of event being audited (e.g., "USER_LOGIN").</param>
        /// <param name="payload">The AuditedPayload instance containing the audit details.</param>
        /// <returns>The asynchronous task returning the sealed LedgerEntry.</returns>
        public Task<LedgerEntry> AddEntryAsync(string eventType, AuditedPayload payload)
        {
            if (string.IsNullOrWhiteSpace(eventType))
            {
                throw new ArgumentException("EventType cannot be null or empty.", nameof(eventType));
            }
            if (payload is null)
            {
                throw new ArgumentNullException(nameof(payload), "AuditedPayload cannot be null.");
            }

            var rawEntry = new LedgerEntry
            {
                Timestamp = DateTimeOffset.UtcNow,
                PreviousHash = string.Empty, // Temporary, will be set in SealEntryAsync
                EventType = eventType,
                Payload = payload,
            };

            return SealEntryAsync(rawEntry);
        }

        /// <summary>
        /// Seals a new audit entry: retrieves the last hash, calculates the current hash, 
        /// and enqueues the entry for background writing. This method is non-blocking.
        /// </summary>
        /// <param name="entry">The raw ledger entry data.</param>
        /// <returns>The sealed LedgerEntry with CurrentHash calculated.</returns>
        public async Task<LedgerEntry> SealEntryAsync(LedgerEntry entry)
        {
            // 1. Get the Previous Hash (Chain Link)
            LedgerEntry? lastEntry = await _store.GetLastEntryAsync();

            // Set PreviousHash: use the last entry's hash, or the Genesis Hash if the ledger is empty.
            // We use the null-coalescing operator here to handle the case where lastEntry is null (first entry).
            entry.PreviousHash = lastEntry?.CurrentHash ?? _options.GenesisHash;

            // 2. Calculate the Current Hash (Seal the Entry)
            entry.CurrentHash = HashUtility.CalculateHash(entry);

            // 3. Enqueue the sealed entry for background writing (Zero-Blocking principle)
            await _queue.EnqueueEntryAsync(entry);

            return entry;
        }

        /// <summary>
        /// Retrieves the very last entry written to the ledger by delegating to the store.
        /// </summary>
        /// <returns>A task that returns the last LedgerEntry, or null if the ledger is empty.</returns>
        public Task<LedgerEntry?> GetLastEntryAsync()
        {
            return _store.GetLastEntryAsync();
        }

        /// <summary>
        /// Verifies the complete cryptographic integrity of the ledger, from the newest hash 
        /// back to the Genesis Hash.
        /// </summary>
        /// <returns>A tuple containing a boolean indicating validity and a string containing 
        /// the reason for failure if invalid, or a success message if valid.</returns>
        public async Task<(bool IsValid, string Reason)> VerifyChainIntegrityAsync()
        {
            // Retrieve all entries from the store.
            IEnumerable<LedgerEntry> rawEntries = await _store.GetAllEntriesAsync();

            // --- CRITICAL CHECK: Ensure chronological order for verification ---
            var allEntries = rawEntries.OrderBy(e => e.Timestamp).ThenBy(e => e.Id).ToList();

            if (allEntries.Count == 0)
            {
                return (true, "Ledger is empty, integrity assumed (Genesis Hash is the current reference).");
            }

            string expectedPreviousHash = _options.GenesisHash;

            foreach (var entry in allEntries)
            {
                // Check 1: Chain Link Integrity (Checks if the pointer is correct)
                if (entry.PreviousHash != expectedPreviousHash)
                {
                    string reason = $"Chain link broken at entry ID {entry.Id}. " +
                                    $"Expected PreviousHash: {expectedPreviousHash}, but found: {entry.PreviousHash}. " +
                                    $"The ledger has been tampered with or corrupted.";
                    // Returns status and reason upon failure
                    return (false, reason);
                }

                // Check 2: Data Integrity (Checks if the data itself has been modified)
                string calculatedHash = HashUtility.CalculateHash(entry);
                if (calculatedHash != entry.CurrentHash)
                {
                    string reason = $"Data tampering detected in entry ID {entry.Id}. " +
                                    $"Calculated hash: {calculatedHash} does not match stored hash: {entry.CurrentHash}. " +
                                    $"The entry's payload has been modified.";
                    // Returns status and reason upon failure
                    return (false, reason);
                }

                // Update the expected hash for the next iteration
                expectedPreviousHash = calculatedHash;
            }

            // Returns success status and a message
            return (true, "Chain integrity successfully verified. All entries are valid.");
        }

        private static ProvanceOptions ValidateGenesisHash(ProvanceOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.GenesisHash))
            {
                throw new InvalidOperationException("PROVANCE Protocol requires a non-null and non-empty GenesisHash to be configured in ProvanceOptions.");
            }
            if (options.GenesisHash.Length != 64 || !HashUtility.IsValidHexString(options.GenesisHash))
            {
                throw new InvalidOperationException("GenesisHash must be a valid 64-character SHA-256 hash string.");
            }
            return options;
        }
    }
}