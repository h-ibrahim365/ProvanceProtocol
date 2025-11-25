using Provance.Core.Data;
using Provance.Core.Services.Interfaces;
using System.Threading.Channels;

namespace Provance.Core.Services
{
    /// <summary>
    /// Implements <see cref="IEntryQueue"/> using a bounded <see cref="Channel{T}"/> as a buffer
    /// between producers (web/API) and the consumer (background writer).
    /// </summary>
    public class EntryQueue : IEntryQueue
    {
        private const int MaxQueueCapacity = 100_000;
        private readonly Channel<LedgerEntry> _channel;

        /// <summary>
        /// Initializes a new instance of the <see cref="EntryQueue"/> class.
        /// Configures a bounded channel with backpressure.
        /// </summary>
        public EntryQueue()
        {
            var options = new BoundedChannelOptions(MaxQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            };

            _channel = Channel.CreateBounded<LedgerEntry>(options);
        }

        /// <inheritdoc />
        public ChannelReader<LedgerEntry> Reader => _channel.Reader;

        /// <inheritdoc />
        public ValueTask EnqueueEntryAsync(LedgerEntry entry, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entry);
            return _channel.Writer.WriteAsync(entry, cancellationToken);
        }

        /// <inheritdoc />
        public ValueTask<LedgerEntry> DequeueEntryAsync(CancellationToken cancellationToken = default)
        {
            return _channel.Reader.ReadAsync(cancellationToken);
        }

        /// <inheritdoc />
        public void CompleteWriter()
        {
            _channel.Writer.TryComplete();
        }
    }
}