using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Provance.Core.Options;
using Provance.Core.Services.Interfaces;

namespace Provance.Core.Checkpoints
{
    /// <summary>
    /// Publishes a checkpoint of the head to every registered sink at <see cref="ProvanceOptions.CheckpointInterval"/>,
    /// when the head has moved. Does nothing if no sink is registered.
    /// </summary>
    internal sealed class CheckpointPublisher : BackgroundService
    {
        private readonly ILedgerService _ledger;
        private readonly IReadOnlyList<ILedgerCheckpointSink> _sinks;
        private readonly ProvanceOptions _options;
        private readonly TimeProvider _time;
        private readonly ILogger<CheckpointPublisher> _logger;
        private long _lastPublishedSequence;

        public CheckpointPublisher(
            ILedgerService ledger,
            IEnumerable<ILedgerCheckpointSink> sinks,
            IOptions<ProvanceOptions> options,
            TimeProvider time,
            ILogger<CheckpointPublisher> logger)
        {
            _ledger = ledger;
            _sinks = sinks.ToList();
            _options = options.Value;
            _time = time;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_sinks.Count == 0)
            {
                return;
            }

            using var timer = new PeriodicTimer(_options.CheckpointInterval, _time);
            try
            {
                while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    await PublishOnceAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutdown.
            }
        }

        internal async Task PublishOnceAsync(CancellationToken ct)
        {
            try
            {
                var checkpoint = await _ledger.CreateCheckpointAsync(ct).ConfigureAwait(false);
                if (checkpoint is null || checkpoint.Sequence == _lastPublishedSequence)
                {
                    return;
                }

                foreach (var sink in _sinks)
                {
                    await sink.PublishAsync(checkpoint, ct).ConfigureAwait(false);
                }
                _lastPublishedSequence = checkpoint.Sequence;
                _logger.LogInformation("PROVANCE checkpoint published at sequence {Sequence}.", checkpoint.Sequence);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PROVANCE checkpoint publication failed; it will be retried at the next interval.");
            }
        }
    }
}
