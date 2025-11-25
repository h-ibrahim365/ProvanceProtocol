using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Provance.Core.Services.Interfaces;

namespace Provance.Core.Services
{
    /// <summary>
    /// An IHostedService that runs perpetually in the background, consuming sealed entries 
    /// from the IEntryQueue and writing them to the ILedgerStore.
    /// This service ensures the core API remains non-blocking (Zero-Blocking principle).
    /// </summary>
    public class LedgerWriterService : BackgroundService
    {
        private readonly IEntryQueue _queue;
        private readonly ILedgerStore _store;
        private readonly ILogger<LedgerWriterService> _logger;

        private readonly AsyncRetryPolicy _retryPolicy;

        /// <summary>
        /// Initializes a new instance of the <see cref="LedgerWriterService"/> class.
        /// Configures the Polly retry policy with exponential backoff.
        /// </summary>
        /// <param name="queue">The non-blocking queue (Producer/Consumer buffer).</param>
        /// <param name="store">The persistence layer.</param>
        /// <param name="logger">The logging service.</param>
        public LedgerWriterService(IEntryQueue queue, ILedgerStore store, ILogger<LedgerWriterService> logger)
        {
            _queue = queue;
            _store = store;
            _logger = logger;

            // Configure Polly:
            // If ANY exception occurs (database down, timeout, etc.), we retry 3 times.
            // We use "Exponential Backoff": wait 2s, then 4s, then 8s.
            _retryPolicy = Policy
                .Handle<Exception>()
                .WaitAndRetryAsync(
                    retryCount: 3,
                    sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                    onRetry: (exception, timeSpan, retryCount, context) =>
                    {
                        if (_logger.IsEnabled(LogLevel.Warning))
                        {
                            _logger.LogWarning(
                                exception,
                                "Write failed (Attempt {RetryCount}). Retrying in {TimeSpan}s...",
                                retryCount,
                                timeSpan.TotalSeconds);
                        }
                    });
        }

        /// <summary>
        /// Overrides the default StopAsync to ensure the queue writer is completed
        /// when the application starts shutting down. This prevents new entries from
        /// being added and allows the main loop to finish processing the backlog.
        /// </summary>
        /// <param name="cancellationToken">Indicates that the shutdown process should no longer be graceful.</param>
        /// <returns>A <see cref="Task"/> that represents the asynchronous stop operation.</returns>
        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("PROVANCE Ledger Writer Service received stop signal. Completing queue writer.");
            _queue.CompleteWriter();
            return base.StopAsync(cancellationToken);
        }

        /// <summary>
        /// The main background execution loop.
        /// Continuously drains the queue and persists entries to the store.
        /// Uses a retry policy for transient failures and respects <paramref name="stoppingToken"/>
        /// for graceful shutdown.
        /// </summary>
        /// <param name="stoppingToken">
        /// A token that signals the service to stop processing and exit gracefully.
        /// </param>
        /// <returns>A task that represents the lifetime of the background processing loop.</returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("PROVANCE Ledger Writer Service started.");

            try
            {
                while (await _queue.Reader.WaitToReadAsync(stoppingToken))
                {
                    while (_queue.Reader.TryRead(out var entryToSave))
                    {
                        await _retryPolicy.ExecuteAsync(async ct =>
                        {
                            await _store.WriteEntryAsync(entryToSave, ct);
                        }, stoppingToken);

                        if (_logger.IsEnabled(LogLevel.Debug))
                        {
                            _logger.LogDebug(
                                "Successfully wrote entry {EntryId} with hash {EntryHash}.",
                                entryToSave.Id,
                                entryToSave.CurrentHash);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // graceful shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Critical error occurred in the writer loop after retries.");
            }

            _logger.LogInformation("PROVANCE Ledger Writer Service stopped.");
        }
    }
}