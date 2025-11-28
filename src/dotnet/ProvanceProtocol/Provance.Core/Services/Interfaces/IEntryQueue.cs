using Provance.Core.Services.Internal;
using System.Threading.Channels;

namespace Provance.Core.Services.Interfaces
{
    /// <summary>
    /// Defines the contract for a queue used to route ledger transaction contexts (drafts + ACK)
    /// to the background single-writer service.
    /// </summary>
    public interface IEntryQueue
    {
        /// <summary>
        /// Gets the reader instance for consuming transaction contexts from the queue.
        /// This is primarily used by the background service via <c>WaitToReadAsync</c>.
        /// </summary>
        ChannelReader<LedgerTransactionContext> Reader { get; }

        /// <summary>
        /// Asynchronously enqueues a transaction context into the internal channel.
        /// For bounded channels, this call may apply backpressure (it can await) when the queue is full.
        /// </summary>
        /// <param name="context">The transaction context (draft and ack source) to enqueue.</param>
        /// <param name="cancellationToken">Token used to cancel the enqueue operation.</param>
        /// <returns>A <see cref="ValueTask"/> that completes when the item is accepted by the channel.</returns>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled.</exception>
        ValueTask EnqueueAsync(LedgerTransactionContext context, CancellationToken cancellationToken = default);

        /// <summary>
        /// Signals that no more entries will be written to the queue.
        /// This allows the reader to process remaining items and finish gracefully.
        /// </summary>
        void CompleteWriter();
    }
}