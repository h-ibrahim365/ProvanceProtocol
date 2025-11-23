using Provance.Core.Data;

namespace Provance.Core.Services.Interfaces
{
    /// <summary>
    /// Defines the basic operations for managing the immutable audit ledger.
    /// This is the primary contract for external consumption.
    /// </summary>
    public interface ILedgerService
    {
        /// <summary>
        /// Creates a LedgerEntry from a fully constructed AuditedPayload, seals it, and enqueues it for writing.
        /// The Payload can be a derived type (e.g., HttpContextAuditedPayload).
        /// </summary>
        /// <param name="eventType">The type of event being audited (e.g., "USER_LOGIN").</param>
        /// <param name="payload">The AuditedPayload instance containing the audit details.</param>
        /// <returns>The asynchronous task returning the sealed LedgerEntry.</returns>
        Task<LedgerEntry> AddEntryAsync(string eventType, AuditedPayload payload);

        /// <summary>
        /// Seals a new audit entry: retrieves the last hash, calculates the current hash, 
        /// and enqueues the entry for background writing (Zero-Blocking principle).
        /// </summary>
        /// <param name="entry">The raw LedgerEntry data to be sealed and processed.</param>
        /// <returns>The asynchronous task returning the sealed LedgerEntry with CurrentHash calculated.</returns>
        Task<LedgerEntry> SealEntryAsync(LedgerEntry entry);

        /// <summary>
        /// Retrieves the very last entry written to the ledger to establish the chain.
        /// This is critical for finding the PreviousHash for the next entry.
        /// </summary>
        /// <returns>A task that returns the last LedgerEntry, or null if the ledger is empty.</returns>
        Task<LedgerEntry?> GetLastEntryAsync();

        /// <summary>
        /// Verifies the complete cryptographic integrity of the ledger, from the newest hash 
        /// back to the Genesis Hash.
        /// </summary>
        /// <returns>A tuple containing a boolean indicating validity and a string containing 
        /// the reason for failure if invalid, or a success message if valid.</returns>
        Task<(bool IsValid, string Reason)> VerifyChainIntegrityAsync();
    }
}