using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Provance.Core.Services.Interfaces;

namespace Provance.Core.Services
{
    /// <summary>
    /// An IHostedService that runs perpetually in the background, consuming sealed entries 
    /// from the IEntryQueue and writing them to the ILedgerStore.
    /// This service ensures the core API remains non-blocking (Zero-Blocking principle).
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the LedgerWriterService.
    /// </remarks>
    /// <param name="queue">The non-blocking queue (Producer/Consumer buffer).</param>
    /// <param name="store">The persistence layer.</param>
    /// <param name="logger">The logging service.</param>
    public class LedgerWriterService(IEntryQueue queue, ILedgerStore store, ILogger<LedgerWriterService> logger) : BackgroundService
    {
        private readonly IEntryQueue _queue = queue;
        private readonly ILedgerStore _store = store;
        private readonly ILogger<LedgerWriterService> _logger = logger;

        /// <summary>
        /// Overrides the default StopAsync to ensure the queue writer is completed
        /// when the application starts shutting down. This prevents new entries from
        /// being added and allows the main loop to finish processing the backlog.
        /// </summary>
        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("PROVANCE Ledger Writer Service received stop signal. Completing queue writer.");

            // Critical: Signal to the IEntryQueue that no more entries will be added.
            _queue.CompleteWriter();

            // The base implementation handles token cancellation for ExecuteAsync.
            return base.StopAsync(cancellationToken);
        }

        /// <summary>
        /// The main execution loop of the background service.
        /// It continuously consumes entries from the queue until the writer completes or the token is cancelled.
        /// </summary>
        /// <param name="stoppingToken">A token to signal the service shutdown.</param>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("PROVANCE Ledger Writer Service started.");

            try
            {
                // This loop is the recommended pattern: it runs as long as the reader can wait for an item,
                // which means the loop continues even after the token is requested, as long as the queue
                // has items or the writer hasn't called CompleteWriter().
                while (await _queue.Reader.WaitToReadAsync(stoppingToken))
                {
                    // TryRead is guaranteed to succeed here because WaitToReadAsync returned true.
                    if (_queue.Reader.TryRead(out var entryToSave))
                    {
                        // Critical: Write the entry to the permanent store.
                        await _store.WriteEntryAsync(entryToSave);

                        _logger.LogDebug(
                            "Successfully wrote entry {EntryId} with hash {EntryHash}.",
                            entryToSave.Id,
                            entryToSave.CurrentHash);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // This is the expected and graceful exit when the service is stopped via the token.
            }
            catch (Exception ex)
            {
                // Log unhandled errors but allow the service to exit the loop.
                _logger.LogError(ex, "An unhandled critical error occurred in the ledger writer loop.");
            }

            // Final message confirming graceful shutdown or queue depletion.
            _logger.LogInformation("PROVANCE Ledger Writer Service stopped processing queue and exited ExecuteAsync.");
        }
    }
}