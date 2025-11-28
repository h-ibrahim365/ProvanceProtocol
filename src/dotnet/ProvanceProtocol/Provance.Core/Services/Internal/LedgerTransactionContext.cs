using Provance.Core.Data;

namespace Provance.Core.Services.Internal
{
    /// <summary>
    /// Internal wrapper representing an intention to write to the ledger.
    /// It carries the draft data and a completion source to acknowledge the result to the caller.
    /// </summary>
    public class LedgerTransactionContext
    {
        /// <summary>
        /// The type of the event (e.g., "USER_LOGIN").
        /// </summary>
        public required string EventType { get; init; }

        /// <summary>
        /// The business payload of the event.
        /// </summary>
        public required AuditedPayload Payload { get; init; }

        /// <summary>
        /// The TaskCompletionSource used to signal the producer (API) 
        /// that the entry has been successfully sequenced, sealed, and persisted.
        /// </summary>
        public TaskCompletionSource<LedgerEntry> AckSource { get; }
            = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}