using Microsoft.Extensions.Logging;
using Moq;
using Provance.Core.Data;
using Provance.Core.Services;
using Provance.Core.Services.Interfaces;
using System.Threading.Channels;

namespace Provance.Core.Tests.Integration
{
    public class LedgerWriterServiceTests
    {
        [Fact]
        public async Task ExecuteAsync_EntryIsDequeued_ShouldWriteToStoreOnce()
        {
            // Arrange
            var mockQueue = new Mock<IEntryQueue>();
            var mockStore = new Mock<ILedgerStore>();
            var mockLogger = new Mock<ILogger<LedgerWriterService>>();

            var testEntry = new LedgerEntry
            {
                Id = Guid.NewGuid(),
                CurrentHash = "TEST_HASH_123",
                EventType = "SYSTEM_TEST",
                Payload = new AuditedPayload(),
                PreviousHash = "MOCK_GENESIS"
            };

            // 1. Create an unbounded channel to control the data flow.
            var channel = Channel.CreateUnbounded<LedgerEntry>();

            // 2. Configure the mock to return the ChannelReader (which the service uses).
            mockQueue.SetupGet(q => q.Reader).Returns(channel.Reader);

            // 3. Initialize the service
            var stoppingTokenSource = new CancellationTokenSource();
            var service = new LedgerWriterService(mockQueue.Object, mockStore.Object, mockLogger.Object);

            // Start the service (it immediately begins waiting on the channel)
            var executeTask = service.StartAsync(stoppingTokenSource.Token);

            // 4. Write the entry into the channel.
            await channel.Writer.WriteAsync(testEntry);

            // 5. Complete the writer to signal that the stream is finished.
            // This allows the 'while (await _queue.Reader.WaitToReadAsync)' loop to exit gracefully after processing the entry.
            channel.Writer.Complete();

            // Wait for the service to complete its execution (consumed the entry and exited the loop).
            // If the service does not stop, this will eventually timeout in the test runner.
            await executeTask;

            // Assert
            // Verifies that the consumed entry was transmitted to the store's WriteEntryAsync method
            mockStore.Verify(s => s.WriteEntryAsync(
                It.Is<LedgerEntry>(e => e.Id == testEntry.Id)),
                Times.Once(),
                "WriteEntryAsync should be called exactly once for the single dequeued entry.");

            // Verifies there were no other write attempts
            mockStore.VerifyNoOtherCalls();
        }
    }
}