using Provance.Core.Data;
using Provance.Core.Services;
using Xunit;

namespace Provance.Core.Tests.CoreLogic
{
    public class QueueStressTests
    {
        [Fact]
        public async Task Backpressure_ShouldBlockProducer_WhenQueueIsFull_AndResume_WhenSpaceAvailable()
        {
            // Setup
            var queue = new EntryQueue();

            var entry = new LedgerEntry
            {
                Id = Guid.NewGuid(),
                EventType = "STRESS",
                PreviousHash = string.Empty,
                Payload = new AuditedPayload(),
                CurrentHash = string.Empty
            };

            // Timeout safety: if something goes wrong, we don't hang forever.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var token = cts.Token;

            // Fill the queue to capacity (100_000)
            for (int i = 0; i < 100_000; i++)
            {
                await queue.EnqueueEntryAsync(entry, token);
            }

            // 100_001st write should block (backpressure = Wait)
            var producerTask = queue.EnqueueEntryAsync(entry, token).AsTask();

            // Verify it doesn't complete quickly
            var firstRace = await Task.WhenAny(producerTask, Task.Delay(100, token));
            Assert.NotSame(producerTask, firstRace);

            // Free up one slot (Consumption) — option A: via Reader with token (works even if Dequeue has no token)
            _ = await queue.Reader.ReadAsync(token);

            // Now the blocked producer should resume
            var secondRace = await Task.WhenAny(producerTask, Task.Delay(1000, token));
            Assert.Same(producerTask, secondRace);

            await producerTask; // propagate exceptions if any
            Assert.True(producerTask.IsCompletedSuccessfully, "The producer should have resumed successfully.");
        }
    }
}