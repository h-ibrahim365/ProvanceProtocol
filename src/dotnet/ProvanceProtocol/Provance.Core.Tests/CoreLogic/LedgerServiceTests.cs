using Microsoft.Extensions.Options;
using Moq;
using Provance.Core.Data;
using Provance.Core.Options;
using Provance.Core.Services;
using Provance.Core.Services.Interfaces;
using Provance.Core.Utilities;

namespace Provance.Core.Tests.CoreLogic
{
    public class LedgerServiceTests
    {
        private const string GENESIS_HASH = "0000000000000000000000000000000000000000000000000000000000000000";
        private const string TEST_SECRET_KEY = "MySuperSecretTestKey_12345";

        private readonly Mock<IOptions<ProvanceOptions>> _mockOptions;
        private readonly Mock<IEntryQueue> _mockQueue;

        public LedgerServiceTests()
        {
            _mockOptions = new Mock<IOptions<ProvanceOptions>>();
            _mockOptions.Setup(o => o.Value).Returns(new ProvanceOptions
            {
                GenesisHash = GENESIS_HASH,
                SecretKey = TEST_SECRET_KEY
            });

            _mockQueue = new Mock<IEntryQueue>();
        }

        private static LedgerEntry CreateSealedEntry(string previousHash, string eventType, int index, string secretKey)
        {
            var entry = new LedgerEntry
            {
                Id = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow.AddSeconds(index),
                EventType = eventType,
                PreviousHash = previousHash,
                Payload = new AuditedPayload { ActorId = $"Actor{index}", Description = $"Event {index}" },
            };

            entry.CurrentHash = HashUtility.CalculateHash(entry, secretKey);
            return entry;
        }

        [Fact]
        public async Task SealEntryAsync_FirstEntry_UsesGenesisHashAndEnqueues()
        {
            // Arrange
            var token = CancellationToken.None;

            var mockStore = new Mock<ILedgerStore>();
            mockStore
                .Setup(s => s.GetLastEntryAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync((LedgerEntry?)null);

            _mockQueue
                .Setup(q => q.EnqueueEntryAsync(It.IsAny<LedgerEntry>(), It.IsAny<CancellationToken>()))
                .Returns(ValueTask.CompletedTask);

            var service = new LedgerService(_mockOptions.Object, mockStore.Object, _mockQueue.Object);

            var rawEntry = new LedgerEntry
            {
                EventType = "TEST_EVENT",
                PreviousHash = string.Empty,
                Payload = new AuditedPayload()
            };

            // Act
            var sealedEntry = await service.SealEntryAsync(rawEntry, token);

            // Assert
            Assert.Equal(GENESIS_HASH, sealedEntry.PreviousHash);
            Assert.False(string.IsNullOrWhiteSpace(sealedEntry.CurrentHash));

            _mockQueue.Verify(
                q => q.EnqueueEntryAsync(sealedEntry, It.Is<CancellationToken>(ct => ct == token)),
                Times.Once);
        }

        [Fact]
        public async Task SealEntryAsync_NextEntry_UsesPreviousEntryHash_AndEnqueues()
        {
            // Arrange
            var token = CancellationToken.None;

            var mockStore = new Mock<ILedgerStore>();
            var firstEntry = CreateSealedEntry(GENESIS_HASH, "INITIAL_EVENT", 1, TEST_SECRET_KEY);

            mockStore
                .Setup(s => s.GetLastEntryAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(firstEntry);

            _mockQueue
                .Setup(q => q.EnqueueEntryAsync(It.IsAny<LedgerEntry>(), It.IsAny<CancellationToken>()))
                .Returns(ValueTask.CompletedTask);

            var service = new LedgerService(_mockOptions.Object, mockStore.Object, _mockQueue.Object);

            var rawEntry = new LedgerEntry
            {
                EventType = "SECOND_EVENT",
                PreviousHash = string.Empty,
                Payload = new AuditedPayload()
            };

            // Act
            var sealedEntry = await service.SealEntryAsync(rawEntry, token);

            // Assert
            Assert.Equal(firstEntry.CurrentHash, sealedEntry.PreviousHash);
            Assert.False(string.IsNullOrWhiteSpace(sealedEntry.CurrentHash));

            _mockQueue.Verify(
                q => q.EnqueueEntryAsync(sealedEntry, It.Is<CancellationToken>(ct => ct == token)),
                Times.Once);
        }

        [Fact]
        public async Task VerifyChainIntegrityAsync_ValidChain_ReturnsTrue()
        {
            // Arrange
            var token = CancellationToken.None;

            var mockStore = new Mock<ILedgerStore>();

            var e1 = CreateSealedEntry(GENESIS_HASH, "E1", 1, TEST_SECRET_KEY);
            var e2 = CreateSealedEntry(e1.CurrentHash!, "E2", 2, TEST_SECRET_KEY);
            var e3 = CreateSealedEntry(e2.CurrentHash!, "E3", 3, TEST_SECRET_KEY);

            var validChain = new List<LedgerEntry> { e1, e2, e3 };

            mockStore
                .Setup(s => s.GetAllEntriesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(validChain);

            var service = new LedgerService(_mockOptions.Object, mockStore.Object, _mockQueue.Object);

            // Act
            var (isValid, reason) = await service.VerifyChainIntegrityAsync(token);

            // Assert
            Assert.True(isValid);
            Assert.Contains("Chain integrity successfully verified", reason);

            mockStore.Verify(s => s.GetAllEntriesAsync(It.Is<CancellationToken>(ct => ct == token)), Times.Once);
        }

        [Fact]
        public async Task VerifyChainIntegrityAsync_BrokenChainLink_ReturnsFalse()
        {
            // Arrange
            var token = CancellationToken.None;

            var mockStore = new Mock<ILedgerStore>();

            var e1 = CreateSealedEntry(GENESIS_HASH, "E1", 1, TEST_SECRET_KEY);
            var e2_CORRUPTED = CreateSealedEntry("BAD_HASH_LINK", "E2", 2, TEST_SECRET_KEY);

            var corruptedChain = new List<LedgerEntry> { e1, e2_CORRUPTED };

            mockStore
                .Setup(s => s.GetAllEntriesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(corruptedChain);

            var service = new LedgerService(_mockOptions.Object, mockStore.Object, _mockQueue.Object);

            // Act
            var (isValid, reason) = await service.VerifyChainIntegrityAsync(token);

            // Assert
            Assert.False(isValid);
            Assert.Contains("Chain link broken at entry ID", reason);

            mockStore.Verify(s => s.GetAllEntriesAsync(It.Is<CancellationToken>(ct => ct == token)), Times.Once);
        }
    }
}