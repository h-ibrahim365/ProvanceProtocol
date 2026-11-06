using System.Text.Json;
using Microsoft.Extensions.Options;
using Provance.Core.Canonicalization;
using Provance.Core.Cryptography;
using Provance.Core.Data;
using Provance.Core.Options;
using Provance.Core.Services.Interfaces;
using Provance.Core.Signing;
using Provance.Core.Verification;

namespace Provance.Core.Services.Internal
{
    internal sealed class LedgerService : ILedgerService
    {
        private const int MaxEventTypeLength = 200;

        private readonly EntryQueue _queue;
        private readonly ILedgerStore _store;
        private readonly ILedgerSigner _signer;
        private readonly LedgerVerifier _verifier;
        private readonly ProvanceOptions _options;
        private readonly TimeProvider _time;

        public LedgerService(
            EntryQueue queue,
            ILedgerStore store,
            ILedgerSigner signer,
            LedgerVerifier verifier,
            IOptions<ProvanceOptions> options,
            TimeProvider time)
        {
            _queue = queue;
            _store = store;
            _signer = signer;
            _verifier = verifier;
            _options = options.Value;
            _time = time;
        }

        public async Task<LedgerEntry> AddEntryAsync<TPayload>(string eventType, TPayload payload, CancellationToken cancellationToken = default)
            where TPayload : notnull
        {
            if (string.IsNullOrWhiteSpace(eventType))
            {
                throw new ArgumentException("Event type is required.", nameof(eventType));
            }
            if (eventType.Length > MaxEventTypeLength)
            {
                throw new ArgumentException($"Event type is limited to {MaxEventTypeLength} characters.", nameof(eventType));
            }
            if (payload is null)
            {
                throw new ArgumentNullException(nameof(payload));
            }
            cancellationToken.ThrowIfCancellationRequested();

            string payloadJson;
            try
            {
                payloadJson = JsonCanonicalizer.Serialize(payload, _options.PayloadSerializerOptions);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                throw new FormatException($"Payload of type {payload.GetType().Name} cannot be serialized to JSON.", ex);
            }

            var pending = new PendingEntry(eventType, payloadJson, LedgerHashing.Sha256Hex(payloadJson));
            await _queue.EnqueueAsync(pending, cancellationToken).ConfigureAwait(false);
            return await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task<LedgerEntry?> GetHeadAsync(CancellationToken cancellationToken = default)
            => _store.GetHeadAsync(cancellationToken);

        public Task<LedgerEntry?> GetEntryAsync(Guid id, CancellationToken cancellationToken = default)
            => _store.GetByIdAsync(id, cancellationToken);

        public Task<LedgerVerificationResult> VerifyAsync(LedgerVerificationOptions? options = null, CancellationToken cancellationToken = default)
            => _verifier.VerifyAsync(options, cancellationToken);

        public async Task<LedgerCheckpoint?> CreateCheckpointAsync(CancellationToken cancellationToken = default)
        {
            var head = await _store.GetHeadAsync(cancellationToken).ConfigureAwait(false);
            if (head is null)
            {
                return null;
            }

            var unsigned = new LedgerCheckpoint
            {
                FormatVersion = ProvanceProtocol.FormatVersion,
                LedgerId = head.LedgerId,
                Sequence = head.Sequence,
                Hash = head.Hash,
                CreatedAt = LedgerHashing.NormalizeTimestamp(_time.GetUtcNow()),
                KeyId = _signer.KeyId,
                Signature = string.Empty
            };

            byte[] digest = LedgerHashing.HashToBytes(LedgerHashing.ComputeCheckpointHash(unsigned));
            byte[] signature = await _signer.SignAsync(digest, cancellationToken).ConfigureAwait(false);
            return unsigned with { Signature = Convert.ToHexString(signature).ToLowerInvariant() };
        }

        public Task<bool> RedactPayloadAsync(Guid id, CancellationToken cancellationToken = default)
            => _store.RedactPayloadAsync(id, cancellationToken);
    }
}
