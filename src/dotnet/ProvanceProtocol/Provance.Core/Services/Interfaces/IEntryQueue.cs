using Provance.Core.Data;
using System.Threading.Channels;

namespace Provance.Core.Services.Interfaces
{
    /// <summary>
    /// Defines the contract for a queue used to route ledger entries
    /// to the background writing service (Consumer).
    /// </summary>
    public interface IEntryQueue
    {
        /// <summary>
        /// Gets the reader instance for consuming entries from the queue.
        /// This is primarily used by the background service via <c>WaitToReadAsync</c>.
        /// </summary>
        ChannelReader<LedgerEntry> Reader { get; }

        /// <summary>
        /// Asynchronously enqueues an entry into the internal channel.
        /// For bounded channels, this call may apply backpressure (it can await) when the queue is full.
        /// </summary>
        /// <param name="entry">The ledger entry to enqueue.</param>
        /// <param name="cancellationToken">Token used to cancel the enqueue operation (e.g., shutdown/request abort).</param>
        /// <returns>A <see cref="ValueTask"/> that completes when the entry is accepted by the channel.</returns>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled.</exception>
        ValueTask EnqueueEntryAsync(LedgerEntry entry, CancellationToken cancellationToken = default);

        /// <summary>
        /// Asynchronously reads an entry from the queue (Consumer).
        /// </summary>
        /// <param name="cancellationToken">Token used to cancel the dequeue operation (e.g., shutdown).</param>
        /// <returns>A task that returns the next ledger entry to be processed.</returns>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled.</exception>
        ValueTask<LedgerEntry> DequeueEntryAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Signals that no more entries will be written to the queue.
        /// This allows the reader to process remaining items and finish gracefully.
        /// </summary>
        void CompleteWriter();
    }
}