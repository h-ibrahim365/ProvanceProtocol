using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using Provance.Core.Data;
using Provance.Core.Options;
using Provance.Core.Services.Interfaces;
using Provance.Core.Utilities;

namespace Provance.Core.Services
{
    /// <summary>
    /// A background service that acts as the "Single Writer" (Consumer).
    /// It strictly linearizes concurrent requests, computes cryptographic hashes,
    /// and ensures no forks are created in the ledger chain.
    /// </summary>
    public class LedgerWriterService : BackgroundService
    {
        private readonly IEntryQueue _queue;
        private readonly ILedgerStore _store;
        private readonly ProvanceOptions _options; // Needed for SecretKey/GenesisHash
        private readonly ILogger<LedgerWriterService> _logger;
        private readonly AsyncRetryPolicy _retryPolicy;

        private readonly string _workerId = Guid.NewGuid().ToString(); // Unique ID for this service instance
        private const string LOCK_RESOURCE = "ledger_writer_lock_v1";

        /// <summary>
        /// Initializes a new instance of the <see cref="LedgerWriterService"/> class.
        /// Configures the retry policy and initializes the unique worker identity.
        /// </summary>
        /// <param name="queue">The entry queue interface for consuming pending ledger entries.</param>
        /// <param name="store">The store interface for persisting sealed entries and managing locks.</param>
        /// <param name="options">The configuration options containing the Genesis Hash and Secret Key.</param>
        /// <param name="logger">The logger used for diagnostic information and error reporting.</param>
        public LedgerWriterService(
            IEntryQueue queue,
            ILedgerStore store,
            IOptions<ProvanceOptions> options,
            ILogger<LedgerWriterService> logger)
        {
            _queue = queue;
            _store = store;
            _options = options.Value;
            _logger = logger;

            // Configure Polly for retries (Exponential backoff):
            _retryPolicy = Policy
                .Handle<Exception>()
                .WaitAndRetryAsync(
                    retryCount: 3,
                    sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                    onRetry: (exception, timeSpan, retryCount, context) =>
                    {
                        if (_logger.IsEnabled(LogLevel.Warning))
                            _logger.LogWarning(
                                exception,
                                "Write failed (Attempt {RetryCount}). Retrying in {TimeSpan}s...",
                                retryCount,
                                timeSpan.TotalSeconds);
                    });
        }

        /// <summary>
        /// Overrides the default StopAsync to ensure the queue writer is completed
        /// when the application starts shutting down.
        /// </summary>
        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("PROVANCE Ledger Writer Service received stop signal. Completing queue writer.");
            _queue.CompleteWriter();
            return base.StopAsync(cancellationToken);
        }

        /// <summary>
        /// The main background execution loop. Continuously drains the queue and persists entries.
        /// </summary>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("PROVANCE Single Writer Loop ({WorkerId}) starting.", _workerId);

            bool lockAcquired = await _store.AcquireOrRenewLeaseAsync(
                LOCK_RESOURCE,
                _workerId,
                TimeSpan.FromSeconds(30),
                stoppingToken);

            if (!lockAcquired)
            {
                if (_logger.IsEnabled(LogLevel.Critical))
                {
                    _logger.LogCritical(
                        "FATAL: Another Writer instance holds the lock. This instance ({WorkerId}) will shut down to prevent forks.",
                        _workerId);
                }

                throw new InvalidOperationException(
                    $"Could not acquire Single Writer lock for worker {_workerId}. Another instance is active.");
            }

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Writer Lock acquired. Starting Heartbeat loop.");

            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            Task heartbeatTask = RunHeartbeatAsync(heartbeatCts.Token);

            try
            {
                string localPreviousHash;
                long localSequence;

                var lastEntry = await _store.GetLastEntryAsync(stoppingToken);

                localPreviousHash = lastEntry?.CurrentHash ?? _options.GenesisHash;
                localSequence = lastEntry?.Sequence ?? 0;

                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "Chain head initialized at: {Hash} (Sequence={Sequence})",
                        localPreviousHash,
                        localSequence);
                }

                while (!stoppingToken.IsCancellationRequested)
                {
                    var waitToReadTask = _queue.Reader.WaitToReadAsync(stoppingToken).AsTask();
                    var completed = await Task.WhenAny(waitToReadTask, heartbeatTask);

                    if (completed == heartbeatTask)
                    {
                        // If the heartbeat failed, this throws and stops the writer immediately.
                        await heartbeatTask;
                        break;
                    }

                    if (!await waitToReadTask)
                        break;

                    while (_queue.Reader.TryRead(out var context))
                    {
                        try
                        {
                            var nextSequence = localSequence + 1;

                            var entry = new LedgerEntry
                            {
                                Id = Guid.NewGuid(),
                                Timestamp = DateTimeOffset.UtcNow,
                                EventType = context.EventType,
                                Payload = context.Payload,
                                Sequence = nextSequence,
                                PreviousHash = localPreviousHash,
                                CurrentHash = null
                            };

                            entry.CurrentHash = HashUtility.CalculateHash(entry, _options.SecretKey);

                            await _retryPolicy.ExecuteAsync(
                                async ct => await _store.WriteEntryAsync(entry, ct),
                                stoppingToken);

                            localPreviousHash = entry.CurrentHash!;
                            localSequence = nextSequence;

                            context.AckSource.TrySetResult(entry);

                            if (_logger.IsEnabled(LogLevel.Debug))
                            {
                                _logger.LogDebug(
                                    "Successfully sealed and wrote entry {EntryId} (Seq={Seq}).",
                                    entry.Id,
                                    entry.Sequence);
                            }
                        }
                        catch (Exception ex)
                        {
                            if (_logger.IsEnabled(LogLevel.Error))
                                _logger.LogError(ex, "Failed to process entry draft after all retries.");

                            context.AckSource.TrySetException(ex);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation("PROVANCE Single Writer Loop ({WorkerId}) stopping (cancellation requested).", _workerId);
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError(ex, "Critical error occurred in the writer loop.");

                throw;
            }
            finally
            {
                heartbeatCts.Cancel();

                try
                {
                    await heartbeatTask;
                }
                catch (OperationCanceledException)
                {
                    // Expected on shutdown.
                }
                catch (Exception ex)
                {
                    if (_logger.IsEnabled(LogLevel.Error))
                        _logger.LogError(ex, "Heartbeat task terminated with error.");
                }
            }

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("PROVANCE Single Writer Loop ({WorkerId}) stopped.", _workerId);
        }


        private async Task RunHeartbeatAsync(CancellationToken ct)
        {
            // Renew the 30-second lease every 10 seconds.
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));

            _logger.LogInformation("Heartbeat loop started (Renewal every 10s).");

            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                {
                    // Attempt to renew the 30-second lease.
                    var renewed = await _store.AcquireOrRenewLeaseAsync(LOCK_RESOURCE, _workerId, TimeSpan.FromSeconds(30), ct);

                    if (!renewed)
                    {
                        _logger.LogCritical("LOST WRITER LOCK during heartbeat! Shutting down worker.");
                        // Lock lost: throw an exception to force the main service to stop.
                        throw new InvalidOperationException($"Lost lease for lock resource {LOCK_RESOURCE}.");
                    }

                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug("Heartbeat successful. Lease renewed.");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Heartbeat loop stopped gracefully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Heartbeat loop failed critically.");
                throw;
            }
        }
    }
}