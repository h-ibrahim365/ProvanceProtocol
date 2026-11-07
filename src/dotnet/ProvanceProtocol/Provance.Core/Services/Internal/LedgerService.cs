using System.Text.Json;
using Microsoft.Extensions.Logging;
using Provance.Core.Archive;
using Provance.Core.Canonicalization;
using Provance.Core.Comparison;
using Provance.Core.Cryptography;
using Provance.Core.Data;
using Provance.Core.Exceptions;
using Provance.Core.Retention;
using Provance.Core.Services.Interfaces;
using Provance.Core.Verification;

namespace Provance.Core.Services.Internal
{
    internal sealed class LedgerService : ILedgerService
    {
        private const int MaxEventTypeLength = 200;

        private readonly LedgerRuntime _runtime;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _retentionGate = new(1, 1);

        public LedgerService(LedgerRuntime runtime, ILogger<LedgerService> logger)
        {
            _runtime = runtime;
            _logger = logger;
        }

        public string Name => _runtime.Name;

        public Task<LedgerEntry> AddEntryAsync<TPayload>(string eventType, TPayload payload, CancellationToken cancellationToken = default)
            where TPayload : notnull
        {
            if (!string.IsNullOrEmpty(eventType) && eventType.StartsWith(LedgerRetentionRecord.ReservedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Event types starting with '{LedgerRetentionRecord.ReservedPrefix}' are reserved for the library.", nameof(eventType));
            }
            return AddCoreAsync(eventType, payload, cancellationToken);
        }

        private async Task<LedgerEntry> AddCoreAsync<TPayload>(string eventType, TPayload payload, CancellationToken cancellationToken)
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
                payloadJson = payload is CanonicalPayload canonical
                    ? canonical.Json
                    : JsonCanonicalizer.Serialize(payload, _runtime.Options.PayloadSerializerOptions);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                throw new FormatException($"Payload of type {payload.GetType().Name} cannot be serialized to JSON.", ex);
            }

            var pending = new PendingEntry(eventType, payloadJson, LedgerHashing.Sha256Hex(payloadJson));
            await _runtime.Queue.EnqueueAsync(pending, cancellationToken).ConfigureAwait(false);
            return await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task<LedgerEntry?> GetHeadAsync(CancellationToken cancellationToken = default)
            => _runtime.Store.GetHeadAsync(cancellationToken);

        public Task<LedgerEntry?> GetEntryAsync(Guid id, CancellationToken cancellationToken = default)
            => _runtime.Store.GetByIdAsync(id, cancellationToken);

        public Task<LedgerVerificationResult> VerifyAsync(LedgerVerificationOptions? options = null, CancellationToken cancellationToken = default)
            => _runtime.Verifier.VerifyAsync(options, cancellationToken);

        public async Task<LedgerCheckpoint?> CreateCheckpointAsync(CancellationToken cancellationToken = default)
        {
            var head = await _runtime.Store.GetHeadAsync(cancellationToken).ConfigureAwait(false);
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
                CreatedAt = LedgerHashing.NormalizeTimestamp(_runtime.Time.GetUtcNow()),
                KeyId = _runtime.Signer.KeyId,
                Signature = string.Empty
            };

            byte[] digest = LedgerHashing.HashToBytes(LedgerHashing.ComputeCheckpointHash(unsigned));
            byte[] signature = await _runtime.Signer.SignAsync(digest, cancellationToken).ConfigureAwait(false);
            return unsigned with { Signature = Convert.ToHexString(signature).ToLowerInvariant() };
        }

        public async Task<bool> RedactPayloadAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var archive = _runtime.Copier.Archive;
            var entry = await _runtime.Store.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
            LedgerEntry? archived = null;
            if (archive is not null)
            {
                try
                {
                    archived = await archive.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && entry is not null)
                {
                    _logger.LogWarning(ex, "PROVANCE could not read the archive before erasing entry {Id}.", id);
                }
            }

            var known = entry ?? archived;
            if (known is null)
            {
                return false;
            }
            if (known.EventType.StartsWith(LedgerRetentionRecord.ReservedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Entries written by the library cannot be redacted.");
            }

            // A payload already missing was not erased through this method: recording it now would make a deletion
            // done in the database look authorized.
            bool primaryHasPayload = entry?.PayloadJson is not null;
            bool archiveHasPayload = archived?.PayloadJson is not null;
            if (primaryHasPayload || archiveHasPayload)
            {
                // The signed record first, so that a crash never leaves an erased payload without its record.
                await AddCoreAsync(LedgerRedactionRecord.EventType,
                    new CanonicalPayload(LedgerRedactionRecord.CreatePayload(known.Sequence, known.Id)), cancellationToken).ConfigureAwait(false);
            }

            if (primaryHasPayload)
            {
                await _runtime.Store.RedactPayloadAsync(id, cancellationToken).ConfigureAwait(false);
            }

            if (archive is not null && (archiveHasPayload || archived is null))
            {
                try
                {
                    // false: not copied yet; the copy will carry the erased payload.
                    await archive.RedactPayloadAsync(id, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new LedgerArchiveException(
                        $"The payload of entry {id} was erased in the primary store but not in the archive: {ex.Message}", ex);
                }
            }
            return true;
        }

        public LedgerArchiveStatus? GetArchiveStatus() => _runtime.Copier.Enabled ? _runtime.Copier.Status : null;

        public async Task<LedgerComparison> CompareWithArchiveAsync(int maxDifferences = 1000, CancellationToken cancellationToken = default)
        {
            var archive = _runtime.Copier.Archive
                ?? throw new InvalidOperationException($"No archive is configured for ledger '{_runtime.Options.LedgerId}'. Call UseArchive(...) after AddProvance(...).");
            await TryCopyAsync(cancellationToken).ConfigureAwait(false);
            return await _runtime.Comparer.CompareAsync(_runtime.Store, archive, maxDifferences, cancellationToken).ConfigureAwait(false);
        }

        public async Task<LedgerRetentionResult> ApplyRetentionAsync(TimeSpan keepFor, CancellationToken cancellationToken = default)
        {
            if (keepFor <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(keepFor), "keepFor must be positive.");
            }
            if (_runtime.Store is not IPrunableLedgerStore prunable)
            {
                throw new NotSupportedException($"The store {_runtime.Store.GetType().Name} cannot delete entries (it does not implement IPrunableLedgerStore).");
            }

            await _retentionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ApplyRetentionCoreAsync(prunable, keepFor, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _retentionGate.Release();
            }
        }

        private async Task<LedgerRetentionResult> ApplyRetentionCoreAsync(IPrunableLedgerStore store, TimeSpan keepFor, CancellationToken ct)
        {
            static LedgerRetentionResult Skip(string message, long first = 0) => new() { Applied = false, Message = message, FirstSequence = first };

            var archive = _runtime.Copier.Archive;
            if (archive is null && _runtime.Options.Retention.RequireArchive)
            {
                return Skip("No archive is configured, so nothing is removed. Configure UseArchive(...), or set Retention:RequireArchive to false if you keep a copy another way.");
            }

            // Never delete evidence: the primary store must verify before anything is removed.
            var verification = await _runtime.Verifier.VerifyAsync(cancellationToken: ct).ConfigureAwait(false);
            if (!verification.IsValid)
            {
                return Skip($"The primary store does not verify, so nothing is removed: {verification.Message}");
            }

            // The boundary is the last entry older than the cutoff. It stays, as the first entry of the primary store.
            var cutoff = _runtime.Time.GetUtcNow() - keepFor;
            LedgerEntry? boundary = null;
            long first = 0;
            await foreach (var entry in store.ReadAsync(1, ct).ConfigureAwait(false))
            {
                if (first == 0) first = entry.Sequence;
                if (entry.Timestamp >= cutoff) break;
                boundary = entry;
            }

            if (boundary is null || boundary.Sequence <= first)
            {
                return Skip($"No entry older than {keepFor} can be removed.", first);
            }

            if (archive is not null)
            {
                long archived = await _runtime.Copier.CopyAsync(ct).ConfigureAwait(false);
                if (archived < boundary.Sequence)
                {
                    return Skip($"The archive holds entries up to {archived} only; entries before {boundary.Sequence} are kept until they are copied.", first);
                }

                var mismatch = await FirstMismatchAsync(store, archive, first, boundary.Sequence, ct).ConfigureAwait(false);
                if (mismatch is not null)
                {
                    return Skip($"The archive does not hold an identical copy, so nothing is removed: {mismatch}", first);
                }
            }

            var record = await AddCoreAsync(LedgerRetentionRecord.EventType,
                new CanonicalPayload(LedgerRetentionRecord.CreatePayload(boundary.Sequence, boundary.Hash)), ct).ConfigureAwait(false);
            long removed = await store.DeleteBeforeAsync(boundary.Sequence, ct).ConfigureAwait(false);

            LedgerCheckpoint? checkpoint = await CreateCheckpointAsync(ct).ConfigureAwait(false);
            if (checkpoint is not null && _runtime.Publisher.HasSinks)
            {
                try
                {
                    await _runtime.Publisher.PublishAsync(checkpoint, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "PROVANCE could not publish the checkpoint taken after retention; the next automatic checkpoint covers it.");
                }
            }
            _runtime.Copier.Signal();

            return new LedgerRetentionResult
            {
                Applied = true,
                Message = $"{removed} entries before sequence {boundary.Sequence} were removed from the primary store.",
                RemovedEntries = removed,
                FirstSequence = boundary.Sequence,
                Record = record,
                Checkpoint = checkpoint
            };
        }

        private async Task<string?> FirstMismatchAsync(ILedgerStore primary, ILedgerStore archive, long from, long through, CancellationToken ct)
        {
            await using var a = archive.ReadAsync(from, ct).GetAsyncEnumerator(ct);
            await foreach (var entry in primary.ReadAsync(from, ct).ConfigureAwait(false))
            {
                if (entry.Sequence > through) break;
                if (!await a.MoveNextAsync().ConfigureAwait(false))
                {
                    return $"entry {entry.Sequence} is missing from the archive.";
                }
                if (a.Current.Sequence != entry.Sequence)
                {
                    return $"the archive has entry {a.Current.Sequence} where entry {entry.Sequence} was expected.";
                }
                var difference = await _runtime.Comparer.CompareEntryAsync(a.Current, entry, ct).ConfigureAwait(false);
                if (difference is not null && difference.IsTampering)
                {
                    return difference.Summary;
                }
            }
            return null;
        }

        private async Task TryCopyAsync(CancellationToken ct)
        {
            try
            {
                await _runtime.Copier.CopyAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "PROVANCE could not copy the latest entries to the archive before comparing.");
            }
        }

        /// <summary>A payload that is already canonical JSON (used for the library's own records).</summary>
        private sealed record CanonicalPayload(string Json);
    }
}
