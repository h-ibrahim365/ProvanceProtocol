using Microsoft.Extensions.Logging;
using Provance.Core.Data;
using Provance.Core.Options;
using Provance.Core.Services;

namespace Provance.Core.Tests.CoreLogic
{
    public class ConcurrencyTests
    {
        private const string GENESIS_HASH = "0000000000000000000000000000000000000000000000000000000000000000";
        private const string SECRET_KEY = "TEST_SECRET_KEY_FOR_HMAC_SIGNING";

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public async Task HighConcurrency_AddEntryAsync_ShouldProduceLinearChain_WithoutForks(int iteration)
        {
            // Arrange: Setup full pipeline
            var options = Microsoft.Extensions.Options.Options.Create(new ProvanceOptions
            {
                GenesisHash = GENESIS_HASH,
                SecretKey = SECRET_KEY
            });

            // Note: InMemoryLedgerStore contains a Task.Delay(5).
            // With Single Writer pattern, 1000 items * 15ms (OS timer resolution) ~= 15 seconds.
            // We reduce the load to 100 to keep tests fast (approx 1.5s).
            var store = new InMemoryLedgerStore();

            // Queue with default capacity is fine, or we could strict-size it: new EntryQueue(200);
            var queue = new EntryQueue();

            // Using Console Logger to see errors if any occur during the test
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning));
            var loggerWriter = loggerFactory.CreateLogger<LedgerWriterService>();

            var writerService = new LedgerWriterService(queue, store, options, loggerWriter);
            var ledgerService = new LedgerService(options, store, queue);

            await writerService.StartAsync(CancellationToken.None);

            try
            {
                // Act: Parallel burst of 1000 requests
                int concurrentRequests = 1000;

                // Create tasks without starting them manually (AddEntryAsync starts them)
                var tasks = Enumerable.Range(0, concurrentRequests).Select(i =>
                {
                    var payload = new AuditedPayload
                    {
                        ActorId = $"User_{i}",
                        Description = $"Concurrency Test {iteration} - Item {i}"
                    };
                    return ledgerService.AddEntryAsync("STRESS_TEST", payload);
                });

                var results = await Task.WhenAll(tasks);

                // Assert: Verify integrity and absence of forks
                Assert.Equal(concurrentRequests, results.Length);
                Assert.Equal(concurrentRequests, results.Select(e => e.Id).Distinct().Count());

                // Standard integrity check
                var (isValid, reason) = await ledgerService.VerifyChainIntegrityAsync();
                Assert.True(isValid, $"Iteration {iteration} failed: {reason}");

                // Anti-fork check: PreviousHash must be unique across all entries
                var allEntries = await store.GetAllEntriesAsync();
                var forks = allEntries
                    .GroupBy(e => e.PreviousHash)
                    .Where(g => g.Count() > 1)
                    .ToList();

                Assert.Empty(forks);
            }
            finally
            {
                await writerService.StopAsync(CancellationToken.None);
            }
        }
    }
}