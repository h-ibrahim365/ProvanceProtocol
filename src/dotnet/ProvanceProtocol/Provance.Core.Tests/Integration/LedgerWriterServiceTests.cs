using Microsoft.Extensions.Logging;
using Moq;
using Provance.Core.Data;
using Provance.Core.Options;
using Provance.Core.Services;
using Provance.Core.Services.Interfaces;
using Provance.Core.Services.Internal;
using System.Threading.Channels;

namespace Provance.Core.Tests.Integration
{
    public class LedgerWriterServiceTests
    {
        [Fact]
        public async Task ExecuteAsync_EntryIsDequeued_ShouldWriteToStoreAndAck()
        {
            // Arrange
            var mockQueue = new Mock<IEntryQueue>();
            var mockStore = new Mock<ILedgerStore>();
            var mockLogger = new Mock<ILogger<LedgerWriterService>>();

            // 1. Setup Options
            var options = Microsoft.Extensions.Options.Options.Create(new ProvanceOptions
            {
                GenesisHash = "GENESIS_HASH_000",
                SecretKey = "TEST_SECRET_KEY"
            });

            // 2. Setup Queue Channel
            var channel = Channel.CreateUnbounded<LedgerTransactionContext>();
            mockQueue.SetupGet(q => q.Reader).Returns(channel.Reader);

            // 3. Setup Store Mocks
            mockStore
                .Setup(s => s.AcquireOrRenewLeaseAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            mockStore
                .Setup(s => s.GetLastEntryAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync((LedgerEntry?)null);

            mockStore
                .Setup(s => s.WriteEntryAsync(It.IsAny<LedgerEntry>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // 4. Create Service
            var service = new LedgerWriterService(mockQueue.Object, mockStore.Object, options, mockLogger.Object);

            await service.StartAsync(CancellationToken.None);

            // 5. Prepare Test Data
            var context = new LedgerTransactionContext
            {
                EventType = "SYSTEM_TEST",
                Payload = new AuditedPayload { Description = "Test Content" }
            };

            // Capture the task from the read-only property
            var ackTask = context.AckSource.Task;

            // Act
            await channel.Writer.WriteAsync(context, CancellationToken.None);

            // Wait deterministically for the AckSource to be set by the service
            var completedTask = await Task.WhenAny(ackTask, Task.Delay(TimeSpan.FromSeconds(2)));

            Assert.True(completedTask == ackTask, "The service did not process the entry within the timeout.");

            var processedEntry = await ackTask;

            // Cleanup
            channel.Writer.Complete();
            await service.StopAsync(CancellationToken.None);

            // Assertions
            Assert.NotNull(processedEntry);
            Assert.Equal("SYSTEM_TEST", processedEntry.EventType);
            Assert.Equal("GENESIS_HASH_000", processedEntry.PreviousHash);
            Assert.NotNull(processedEntry.CurrentHash);

            mockStore.Verify(
                s => s.WriteEntryAsync(
                    It.Is<LedgerEntry>(e => e.Id == processedEntry.Id),
                    It.IsAny<CancellationToken>()),
                Times.Once);

            mockStore.Verify(
                s => s.AcquireOrRenewLeaseAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
                Times.AtLeastOnce);
        }
    }
}