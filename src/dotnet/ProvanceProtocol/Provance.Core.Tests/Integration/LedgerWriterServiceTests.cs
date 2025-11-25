using Microsoft.Extensions.Logging;
using Moq;
using Provance.Core.Data;
using Provance.Core.Services;
using Provance.Core.Services.Interfaces;
using System.Threading.Channels;
using Xunit;

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

            var channel = Channel.CreateUnbounded<LedgerEntry>();
            mockQueue.SetupGet(q => q.Reader).Returns(channel.Reader);

            var writeTcs = new TaskCompletionSource<LedgerEntry>(TaskCreationOptions.RunContinuationsAsynchronously);

            mockStore
                .Setup(s => s.WriteEntryAsync(It.IsAny<LedgerEntry>(), It.IsAny<CancellationToken>()))
                .Callback<LedgerEntry, CancellationToken>((e, _) => writeTcs.TrySetResult(e))
                .Returns(Task.CompletedTask);

            var service = new LedgerWriterService(mockQueue.Object, mockStore.Object, mockLogger.Object);

            await service.StartAsync(CancellationToken.None);

            // Act
            await channel.Writer.WriteAsync(testEntry, CancellationToken.None);
            channel.Writer.Complete();

            // Wait deterministically for the write
            var completed = await Task.WhenAny(writeTcs.Task, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(writeTcs.Task, completed);

            // cleanup
            await service.StopAsync(CancellationToken.None);

            // Assert
            mockStore.Verify(
                s => s.WriteEntryAsync(
                    It.Is<LedgerEntry>(e => e.Id == testEntry.Id),
                    It.IsAny<CancellationToken>()),
                Times.Once);

            mockStore.VerifyNoOtherCalls();
        }
    }
}