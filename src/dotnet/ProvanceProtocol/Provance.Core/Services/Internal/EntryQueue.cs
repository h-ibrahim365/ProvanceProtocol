using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Provance.Core.Exceptions;
using Provance.Core.Options;

namespace Provance.Core.Services.Internal
{
    /// <summary>
    /// Bounded queue between producers (requests) and the writer of this process.
    /// </summary>
    internal sealed class EntryQueue
    {
        private readonly Channel<PendingEntry> _channel;
        private readonly QueueFullBehavior _fullBehavior;
        private bool _completed;

        public EntryQueue(IOptions<ProvanceOptions> options)
        {
            var o = options.Value;
            _fullBehavior = o.QueueFullBehavior;
            _channel = Channel.CreateBounded<PendingEntry>(new BoundedChannelOptions(o.QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
        }

        public ChannelReader<PendingEntry> Reader => _channel.Reader;

        public async ValueTask EnqueueAsync(PendingEntry entry, CancellationToken cancellationToken)
        {
            if (_channel.Writer.TryWrite(entry))
            {
                return;
            }

            if (Volatile.Read(ref _completed))
            {
                throw Stopped();
            }

            if (_fullBehavior == QueueFullBehavior.FailFast)
            {
                throw new LedgerOverloadedException("PROVANCE queue is full.");
            }

            try
            {
                await _channel.Writer.WriteAsync(entry, cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException ex)
            {
                throw Stopped(ex);
            }
        }

        /// <summary>Stops accepting new entries. Entries already queued are still processed.</summary>
        public void Complete()
        {
            Volatile.Write(ref _completed, true);
            _channel.Writer.TryComplete();
        }

        private static LedgerUnavailableException Stopped(Exception? inner = null)
            => new("PROVANCE writer is stopping; the entry was not queued.", inner);
    }
}
