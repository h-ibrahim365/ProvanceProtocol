using Microsoft.Extensions.Options;
using Moq;
using Provance.Core.Data;
using Provance.Core.Options;
using Provance.Core.Services;
using Provance.Core.Services.Interfaces;
using Provance.Core.Services.Internal;
using Provance.Core.Utilities;

namespace Provance.Core.Tests.CoreLogic
{
    public class LedgerServiceTests
    {
        private const string GENESIS_HASH = "0000000000000000000000000000000000000000000000000000000000000000";
        private const string TEST_SECRET_KEY = "MySuperSecretTestKey_12345";

        private readonly Mock<IOptions<ProvanceOptions>> _mockOptions;
        private readonly Mock<IEntryQueue> _mockQueue;
        private readonly Mock<ILedgerStore> _mockStore;
        private readonly LedgerService _service;

        public LedgerServiceTests()
        {
            _mockOptions = new Mock<IOptions<ProvanceOptions>>();
            _mockOptions.Setup(o => o.Value).Returns(new ProvanceOptions
            {
                GenesisHash = GENESIS_HASH,
                SecretKey = TEST_SECRET_KEY
            });

            _mockQueue = new Mock<IEntryQueue>();
            _mockStore = new Mock<ILedgerStore>();

            _service = new LedgerService(_mockOptions.Object, _mockStore.Object, _mockQueue.Object);
        }

        [Fact]
        public async Task AddEntryAsync_ValidInput_EnqueuesAndWaitsForAck()
        {
            var eventType = "TEST_EVENT";
            var payload = new AuditedPayload { Description = "Test" };
            var expectedId = Guid.NewGuid();

            _mockQueue
                .Setup(q => q.EnqueueAsync(It.IsAny<LedgerTransactionContext>(), It.IsAny<CancellationToken>()))
                .Callback<LedgerTransactionContext, CancellationToken>((context, ct) =>
                {
                    Assert.Equal(eventType, context.EventType);
                    Assert.Equal(payload, context.Payload);

                    var completedEntry = new LedgerEntry
                    {
                        Id = expectedId,
                        Timestamp = DateTimeOffset.UtcNow,
                        Sequence = 1,
                        CurrentHash = "HASH_123",
                        PreviousHash = GENESIS_HASH,
                        EventType = eventType,
                        Payload = payload
                    };

                    context.AckSource.SetResult(completedEntry);
                })
                .Returns(ValueTask.CompletedTask);

            var result = await _service.AddEntryAsync(eventType, payload);

            Assert.NotNull(result);
            Assert.Equal(expectedId, result.Id);

            _mockQueue.Verify(
                q => q.EnqueueAsync(It.IsAny<LedgerTransactionContext>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task AddEntryAsync_WhenQueueThrows_PropagatesException()
        {
            _mockQueue
                .Setup(q => q.EnqueueAsync(It.IsAny<LedgerTransactionContext>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Queue full"));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _service.AddEntryAsync("TEST", new AuditedPayload()));
        }

        [Fact]
        public async Task VerifyChainIntegrityAsync_ValidChain_ReturnsTrue()
        {
            var e1 = CreateSealedEntry(sequence: 1, previousHash: GENESIS_HASH, eventType: "E1", secretKey: TEST_SECRET_KEY);
            var e2 = CreateSealedEntry(sequence: 2, previousHash: e1.CurrentHash!, eventType: "E2", secretKey: TEST_SECRET_KEY);
            var e3 = CreateSealedEntry(sequence: 3, previousHash: e2.CurrentHash!, eventType: "E3", secretKey: TEST_SECRET_KEY);

            // Intentionally out-of-order to ensure verification does not rely on store ordering.
            var validChain = new List<LedgerEntry> { e3, e1, e2 };

            _mockStore
                .Setup(s => s.GetAllEntriesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(validChain);

            var (isValid, reason) = await _service.VerifyChainIntegrityAsync();

            Assert.True(isValid);
            Assert.Contains("Chain integrity", reason);
        }

        [Fact]
        public async Task VerifyChainIntegrityAsync_BrokenChainLink_ReturnsFalse()
        {
            var e1 = CreateSealedEntry(sequence: 1, previousHash: GENESIS_HASH, eventType: "E1", secretKey: TEST_SECRET_KEY);

            // Points to a wrong previous hash.
            var e2Corrupted = CreateSealedEntry(sequence: 2, previousHash: "BAD_HASH_LINK", eventType: "E2", secretKey: TEST_SECRET_KEY);

            var corruptedChain = new List<LedgerEntry> { e2Corrupted, e1 };

            _mockStore
                .Setup(s => s.GetAllEntriesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(corruptedChain);

            var (isValid, reason) = await _service.VerifyChainIntegrityAsync();

            Assert.False(isValid);
            Assert.Contains("Chain link broken", reason);
        }

        [Fact]
        public async Task VerifyChainIntegrityAsync_TamperedData_ReturnsFalse()
        {
            var e1 = CreateSealedEntry(sequence: 1, previousHash: GENESIS_HASH, eventType: "E1", secretKey: TEST_SECRET_KEY);

            // Modify after hashing.
            e1.Payload!.Description = "Tampered Content";

            _mockStore
                .Setup(s => s.GetAllEntriesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([e1]);

            var (isValid, reason) = await _service.VerifyChainIntegrityAsync();

            Assert.False(isValid);
            Assert.Contains("tampering", reason, StringComparison.OrdinalIgnoreCase);
        }

        private static LedgerEntry CreateSealedEntry(long sequence, string previousHash, string eventType, string secretKey)
        {
            var entry = new LedgerEntry
            {
                Id = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow.AddMilliseconds(sequence), // stable-ish, but Sequence is the real order
                Sequence = sequence,
                EventType = eventType,
                PreviousHash = previousHash,
                Payload = new AuditedPayload
                {
                    ActorId = $"Actor{sequence}",
                    Description = $"Event {sequence}"
                }
            };

            entry.CurrentHash = HashUtility.CalculateHash(entry, secretKey);
            return entry;
        }
    }
}