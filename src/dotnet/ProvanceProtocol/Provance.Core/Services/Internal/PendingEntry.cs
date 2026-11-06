using Provance.Core.Data;

namespace Provance.Core.Services.Internal
{
    /// <summary>
    /// An entry prepared by a producer (payload already canonicalized and hashed) waiting for the writer.
    /// The id is fixed here so that the writer can recognize the entry in the store after an ambiguous failure.
    /// </summary>
    internal sealed class PendingEntry
    {
        public PendingEntry(string eventType, string payloadJson, string payloadHash)
        {
            EventType = eventType;
            PayloadJson = payloadJson;
            PayloadHash = payloadHash;
        }

        public Guid Id { get; } = Guid.NewGuid();

        public string EventType { get; }

        public string PayloadJson { get; }

        public string PayloadHash { get; }

        public TaskCompletionSource<LedgerEntry> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
