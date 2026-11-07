using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Provance.Core.Data;
using Provance.Core.Options;
using Provance.Core.Services.Interfaces;

namespace Provance.Core.Retention
{
    /// <summary>Result of <see cref="ILedgerService.ApplyRetentionAsync"/>.</summary>
    public sealed record LedgerRetentionResult
    {
        /// <summary><c>true</c> when entries were removed.</summary>
        public required bool Applied { get; init; }

        /// <summary>What happened, or why nothing was removed.</summary>
        public required string Message { get; init; }

        /// <summary>Number of entries removed from the primary store.</summary>
        public long RemovedEntries { get; init; }

        /// <summary>First sequence left in the primary store.</summary>
        public long FirstSequence { get; init; }

        /// <summary>The retention record appended before the removal.</summary>
        public LedgerEntry? Record { get; init; }

        /// <summary>
        /// A checkpoint of the head after the removal, already published to the checkpoint sinks when there are any.
        /// Keep it outside the database if you have no sink.
        /// </summary>
        public LedgerCheckpoint? Checkpoint { get; init; }
    }

    /// <summary>Runs retention at <see cref="LedgerRetentionOptions.Interval"/> when <see cref="LedgerRetentionOptions.KeepFor"/> is set.</summary>
    internal sealed class RetentionService : BackgroundService
    {
        private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(1);

        private readonly ILedgerService _ledger;
        private readonly ProvanceOptions _options;
        private readonly TimeProvider _time;
        private readonly ILogger<RetentionService> _logger;

        public RetentionService(ILedgerService ledger, ProvanceOptions options, TimeProvider time, ILogger<RetentionService> logger)
        {
            _ledger = ledger;
            _options = options;
            _time = time;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_options.Retention.KeepFor is not TimeSpan keepFor)
            {
                return;
            }

            try
            {
                // First run soon after startup, so that applications restarted every day still apply retention.
                var first = _options.Retention.Interval < FirstRunDelay ? _options.Retention.Interval : FirstRunDelay;
                await Task.Delay(first, _time, stoppingToken).ConfigureAwait(false);
                using var timer = new PeriodicTimer(_options.Retention.Interval, _time);
                do
                {
                    await RunOnceAsync(keepFor, stoppingToken).ConfigureAwait(false);
                }
                while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutdown.
            }
        }

        private async Task RunOnceAsync(TimeSpan keepFor, CancellationToken ct)
        {
            try
            {
                var result = await _ledger.ApplyRetentionAsync(keepFor, ct).ConfigureAwait(false);
                if (result.Applied)
                {
                    _logger.LogInformation("PROVANCE retention on ledger {LedgerId}: {Message}", _options.LedgerId, result.Message);
                }
                else
                {
                    _logger.LogDebug("PROVANCE retention on ledger {LedgerId}: {Message}", _options.LedgerId, result.Message);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PROVANCE retention failed on ledger {LedgerId}; it is retried at the next interval.", _options.LedgerId);
            }
        }
    }
}
