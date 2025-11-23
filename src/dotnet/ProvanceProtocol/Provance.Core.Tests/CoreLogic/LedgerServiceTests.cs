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
        // Constant Genesis Hash for all tests
        private const string GENESIS_HASH = "0000000000000000000000000000000000000000000000000000000000000000";
        private readonly Mock<IOptions<ProvanceOptions>> _mockOptions;
        private readonly Mock<IEntryQueue> _mockQueue;

        public LedgerServiceTests()
        {
            _mockOptions = new Mock<IOptions<ProvanceOptions>>();
            _mockOptions.Setup(o => o.Value).Returns(new ProvanceOptions { GenesisHash = GENESIS_HASH });
            _mockQueue = new Mock<IEntryQueue>();
        }

        // Helper to create simulated chained entries
        // Correction CS8604: Assurer que 'previousHash' est non-null (string)
        // Correction CA1822: Marquer la méthode comme 'static' car elle n'utilise pas les membres de l'instance
        private static LedgerEntry CreateSealedEntry(string previousHash, string eventType, int index)
        {
            var entry = new LedgerEntry
            {
                Id = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow.AddSeconds(index),
                EventType = eventType,
                PreviousHash = previousHash,
                // Assurez-vous que l'initialiseur de AuditedPayload est complet
                Payload = new AuditedPayload { ActorId = $"Actor{index}", Description = $"Event {index}" },
            };
            // Direct use of HashUtility (pure function, good for tests)
            entry.CurrentHash = HashUtility.CalculateHash(entry);
            return entry;
        }

        [Fact]
        public async Task SealEntryAsync_FirstEntry_UsesGenesisHashAndEnqueues()
        {
            // Arrange
            var mockStore = new Mock<ILedgerStore>();
            // Simulates an empty store (no previous entry)
            mockStore.Setup(s => s.GetLastEntryAsync()).ReturnsAsync((LedgerEntry?)null);
            var service = new LedgerService(_mockOptions.Object, mockStore.Object, _mockQueue.Object);

            // Ajout de l'initialisation complète pour éviter les erreurs CS8805 potentielles
            var rawEntry = new LedgerEntry
            {
                EventType = "TEST_EVENT",
                PreviousHash = string.Empty,
                Payload = new AuditedPayload()
            };

            // Act
            var sealedEntry = await service.SealEntryAsync(rawEntry);

            // Assert
            Assert.Equal(GENESIS_HASH, sealedEntry.PreviousHash);
            Assert.NotNull(sealedEntry.CurrentHash);
            // Verifies the sealed entry was correctly passed to the queue
            _mockQueue.Verify(q => q.EnqueueEntryAsync(sealedEntry), Times.Once());
        }

        [Fact]
        public async Task SealEntryAsync_NextEntry_UsesPreviousEntryHash()
        {
            // Arrange
            var mockStore = new Mock<ILedgerStore>();
            var firstEntry = CreateSealedEntry(GENESIS_HASH, "INITIAL_EVENT", 1);
            // Returns the first entry
            mockStore.Setup(s => s.GetLastEntryAsync()).ReturnsAsync(firstEntry);

            var service = new LedgerService(_mockOptions.Object, mockStore.Object, _mockQueue.Object);

            // Ajout de l'initialisation complète pour éviter les erreurs CS8805 potentielles
            var rawEntry = new LedgerEntry
            {
                EventType = "SECOND_EVENT",
                PreviousHash = string.Empty,
                Payload = new AuditedPayload()
            };

            // Act
            var sealedEntry = await service.SealEntryAsync(rawEntry);

            // Assert
            Assert.Equal(firstEntry.CurrentHash, sealedEntry.PreviousHash);
        }

        [Fact]
        public async Task VerifyChainIntegrityAsync_ValidChain_ReturnsTrue()
        {
            // Arrange
            var mockStore = new Mock<ILedgerStore>();

            // 1. Build a chain of 3 valid entries
            var e1 = CreateSealedEntry(GENESIS_HASH, "E1", 1);
            var e2 = CreateSealedEntry(e1.CurrentHash!, "E2", 2);
            var e3 = CreateSealedEntry(e2.CurrentHash!, "E3", 3);
            var validChain = new List<LedgerEntry> { e1, e2, e3 };

            // 2. Configure the Mock to return the valid chain
            mockStore.Setup(s => s.GetAllEntriesAsync()).ReturnsAsync(validChain);
            var service = new LedgerService(_mockOptions.Object, mockStore.Object, _mockQueue.Object);

            // Act
            var (isValid, reason) = await service.VerifyChainIntegrityAsync();

            // Assert
            Assert.True(isValid);
            Assert.Contains("successfully verified", reason);
        }

        [Fact]
        public async Task VerifyChainIntegrityAsync_BrokenChainLink_ReturnsFalse()
        {
            // Arrange
            var mockStore = new Mock<ILedgerStore>();

            // Valid chain up to E1
            var e1 = CreateSealedEntry(GENESIS_HASH, "E1", 1);

            // E2 has an incorrect PreviousHash (simulates chain break)
            var e2_CORRUPTED = CreateSealedEntry("BAD_HASH_LINK", "E2", 2);

            var corruptedChain = new List<LedgerEntry> { e1, e2_CORRUPTED };

            // Configure the Mock to return the corrupted chain
            mockStore.Setup(s => s.GetAllEntriesAsync()).ReturnsAsync(corruptedChain);
            var service = new LedgerService(_mockOptions.Object, mockStore.Object, _mockQueue.Object);

            // Act
            var (isValid, reason) = await service.VerifyChainIntegrityAsync();

            // Assert
            Assert.False(isValid);
            Assert.Contains($"Chain link broken at entry ID {e2_CORRUPTED.Id}", reason);
        }
    }
}