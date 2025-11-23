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
        /// The main execution loop of the background service.
        /// It continuously consumes entries from the queue until the writer completes or the token is cancelled.
        /// </summary>
        /// <param name="stoppingToken">A token to signal the service shutdown.</param>
        /// <returns>A <see cref="Task"/> that represents the long-running operation.</returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("PROVANCE Ledger Writer Service started.");

            try
            {
                while (await _queue.Reader.WaitToReadAsync(stoppingToken))
                {
                    if (_queue.Reader.TryRead(out var entryToSave))
                    {
                        await _retryPolicy.ExecuteAsync(async () =>
                        {
                            await _store.WriteEntryAsync(entryToSave);
                        });

                        // Optimization: Check LogLevel before allocating memory for the log message
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
                // Graceful shutdown
            }
            catch (Exception ex)
            {
                // If retries are exhausted (e.g., DB is down for > 15s), the exception bubbles up here.
                // We log it as a critical error. In a real scenario, you might want to implement a "Dead Letter Queue" here.
                _logger.LogError(ex, "An unhandled critical error occurred in the ledger writer loop after retries.");
            }

            _logger.LogInformation("PROVANCE Ledger Writer Service stopped processing queue and exited ExecuteAsync.");
        }
    }
}