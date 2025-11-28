using Provance.Core.Services.Interfaces;
using Provance.Core.Services.Internal;
using System.Threading.Channels;

namespace Provance.Core.Services
{
    /// <summary>
    /// Implements IEntryQueue using a bounded Channel to buffer requests between
    /// high-concurrency producers (API) and the Single Writer consumer.
    /// </summary>
    public class EntryQueue : IEntryQueue
    {
        // Capacity limit to prevent OOM under heavy load (Backpressure)
        private const int MaxQueueCapacity = 100_000;
        private readonly Channel<LedgerTransactionContext> _channel;

        /// <summary>
        /// Initializes a new instance of the <see cref="EntryQueue"/> class.
        /// Configures a bounded channel with backpressure support, optimized for
        /// multiple concurrent producers and a single sequential consumer.
        /// </summary>
        public EntryQueue()
        {
            var options = new BoundedChannelOptions(MaxQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            };
            _channel = Channel.CreateBounded<LedgerTransactionContext>(options);
        }

        /// <inheritdoc />
        public ChannelReader<LedgerTransactionContext> Reader => _channel.Reader;

        /// <inheritdoc />
        public ValueTask EnqueueAsync(LedgerTransactionContext context, CancellationToken cancellationToken = default)
        {
            // Writes the context to the channel. Caller waits if the channel is full.
            return _channel.Writer.WriteAsync(context, cancellationToken);
        }

        /// <inheritdoc />
        public void CompleteWriter()
        {
            _channel.Writer.TryComplete();
        }
    }
}