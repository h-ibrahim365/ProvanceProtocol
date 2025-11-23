using Provance.Core.Data;
using System.Threading.Channels;

namespace Provance.Core.Services.Interfaces
{
    /// <summary>
    /// Defines the contract for a non-blocking queue used to route sealed entries 
    /// to the background writing service (Consumer).
    /// </summary>
    public interface IEntryQueue
    {
        /// <summary>
        /// Gets the reader instance for consuming entries from the queue.
        /// This is primarily used by the IHostedService for graceful consumption using WaitToReadAsync.
        /// </summary>
        ChannelReader<LedgerEntry> Reader { get; }

        /// <summary>
        /// Asynchronously adds a sealed entry to the queue (Producer).
        /// This call should return immediately to ensure zero-blocking API throughput.
        /// </summary>
        /// <param name="entry">The sealed LedgerEntry ready for saving.</param>
        /// <returns>A task representing the enqueue operation.</returns>
        ValueTask EnqueueEntryAsync(LedgerEntry entry);

        /// <summary>
        /// Asynchronously reads an entry from the queue (Consumer).
        /// This method is called by the IHostedService in the background.
        /// </summary>
        /// <returns>A task that returns the ledger entry to be processed.</returns>
        ValueTask<LedgerEntry> DequeueEntryAsync();

        /// <summary>
        /// Signals that no more entries will be written to the queue.
        /// This allows the reader to process the remaining items and finish gracefully.
        /// </summary>
        void CompleteWriter();
    }
}