using Provance.Core.Data;
using Provance.Core.Services.Interfaces;
using System.Threading.Channels;

namespace Provance.Core.Services
{
    /// <summary>
    /// Implements the IEntryQueue contract using a high-performance,
    /// non-blocking System.Threading.Channels.Channel&lt;T&gt; instance.
    /// This queue acts as a buffer between the fast-paced web API (Producer)
    /// and the slower database I/O (Consumer).
    /// </summary>
    public class EntryQueue : IEntryQueue
    {
        // Channel is a thread-safe, non-blocking queue optimized for async operations.
        private const int MaxQueueCapacity = 100_000;
        private readonly Channel<LedgerEntry> _channel;

        /// <summary>
        /// Initializes a new instance of the EntryQueue class, configuring a bounded channel
        /// to manage the flow of ledger entries with backpressure enabled.
        /// </summary>
        public EntryQueue()
        {
            // Configure the channel to handle backpressure.
            var options = new BoundedChannelOptions(MaxQueueCapacity)
            {
                // FullMode.Wait ensures that EnqueueEntryAsync will wait when the channel is full,
                // applying backpressure to the producer (web API) to prevent system crash.
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true, // Optimization assumption: only one background service reads
                SingleWriter = false // Assumption: multiple web threads write
            };

            _channel = Channel.CreateBounded<LedgerEntry>(options);
        }

        /// <summary>
        /// Gets the reader instance for consuming entries from the queue.
        /// This is primarily used by the IHostedService for graceful consumption.
        /// </summary>
        public ChannelReader<LedgerEntry> Reader => _channel.Reader;

        /// <summary>
        /// Asynchronously adds a sealed entry to the queue (Producer).
        /// </summary>
        /// <param name="entry">The sealed LedgerEntry ready for saving.</param>
        public async ValueTask EnqueueEntryAsync(LedgerEntry entry)
        {
            // WriteAsync handles backpressure if the queue is full.
            await _channel.Writer.WriteAsync(entry);
        }

        /// <summary>
        /// Asynchronously reads an entry from the queue (Consumer).
        /// </summary>
        /// <returns>A task that returns the ledger entry to be processed.</returns>
        public ValueTask<LedgerEntry> DequeueEntryAsync()
        {
            // Wait to read until an item is available.
            return _channel.Reader.ReadAsync();
        }

        /// <summary>
        /// Signals to the channel that no more data will be written.
        /// This is crucial during application shutdown to allow the LedgerWriterService
        /// to process remaining queued items and exit gracefully.
        /// </summary>
        public void CompleteWriter()
        {
            _channel.Writer.TryComplete();
        }
    }
}