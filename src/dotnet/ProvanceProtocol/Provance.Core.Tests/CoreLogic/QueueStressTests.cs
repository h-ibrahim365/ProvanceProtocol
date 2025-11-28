using Provance.Core.Data;
using Provance.Core.Services;
using Provance.Core.Services.Internal;

namespace Provance.Core.Tests.CoreLogic
{
    public class QueueStressTests
    {
        [Fact]
        public async Task Backpressure_ShouldBlockProducer_WhenQueueIsFull_AndResume_WhenSpaceAvailable()
        {
            var queue = new EntryQueue();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var token = cts.Token;

            // Fill to capacity (EntryQueue.MaxQueueCapacity = 100_000).
            for (int i = 0; i < 100_000; i++)
            {
                var ctx = new LedgerTransactionContext
                {
                    EventType = "STRESS",
                    Payload = new AuditedPayload()
                };

                await queue.EnqueueAsync(ctx, token);
            }

            // This enqueue should wait (backpressure).
            var blockedCtx = new LedgerTransactionContext
            {
                EventType = "STRESS",
                Payload = new AuditedPayload()
            };

            var producerTask = queue.EnqueueAsync(blockedCtx, token).AsTask();

            var firstRace = await Task.WhenAny(producerTask, Task.Delay(100, token));
            Assert.NotSame(producerTask, firstRace);

            // Consume one item to free a slot.
            _ = await queue.Reader.ReadAsync(token);

            var secondRace = await Task.WhenAny(producerTask, Task.Delay(1000, token));
            Assert.Same(producerTask, secondRace);

            await producerTask;
            Assert.True(producerTask.IsCompletedSuccessfully);
        }
    }
}