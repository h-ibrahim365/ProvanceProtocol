using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Provance.Core.Cryptography;
using Provance.Core.Data;
using Provance.Core.Exceptions;
using Provance.Core.Options;
using Provance.Core.Services.Interfaces;
using Provance.Core.Signing;

namespace Provance.Core.Services.Internal
{
    /// <summary>
    /// Background writer of one process. Seals queued entries in batches and appends them to the store.
    /// </summary>
    /// <remarks>
    /// <para>Linearity across processes comes from the store: it rejects a sequence or id that already
    /// exists. On any failure the writer reloads the head from the store, acknowledges the entries that
    /// were in fact persisted (looked up by their fixed id) and reseals the others on top of the real head.
    /// Several application instances can therefore write to the same ledger.</para>
    /// <para>On shutdown the writer stops accepting entries and drains the queue until the host shutdown
    /// timeout. Entries that cannot be written fail with <see cref="LedgerUnavailableException"/>.</para>
    /// </remarks>
    internal sealed class LedgerWriter : BackgroundService
    {
        private readonly EntryQueue _queue;
        private readonly ILedgerStore _store;
        private readonly ILedgerSigner _signer;
        private readonly ProvanceOptions _options;
        private readonly TimeProvider _time;
        private readonly ILogger<LedgerWriter> _logger;
        private readonly string _genesisHash;
        private readonly Action? _onAppended;

        private ChainHead? _head; // null: must be reloaded from the store before the next seal

        public LedgerWriter(
            EntryQueue queue,
            ILedgerStore store,
            ILedgerSigner signer,
            ProvanceOptions options,
            TimeProvider time,
            ILogger<LedgerWriter> logger,
            Action? onAppended = null)
        {
            _queue = queue;
            _store = store;
            _signer = signer;
            _options = options;
            _time = time;
            _logger = logger;
            _genesisHash = _options.EffectiveGenesisHash;
            _onAppended = onAppended;
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _queue.Complete();

            var running = ExecuteTask;
            if (running is not null && !running.IsCompleted)
            {
                try
                {
                    await running.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("PROVANCE writer did not drain its queue before the shutdown timeout; remaining entries fail.");
                }
                catch (Exception)
                {
                    // Already logged by the loop.
                }
            }

            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("PROVANCE writer started for ledger {LedgerId}.", _options.LedgerId);
            var batch = new List<PendingEntry>(_options.MaxBatchSize);

            try
            {
                while (await _queue.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
                {
                    batch.Clear();
                    while (batch.Count < _options.MaxBatchSize && _queue.Reader.TryRead(out var pending))
                    {
                        batch.Add(pending);
                    }

                    if (batch.Count == 0)
                    {
                        continue;
                    }

                    try
                    {
                        await ProcessBatchAsync(batch, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        FailAll(batch, new LedgerUnavailableException("PROVANCE writer stopped before the entry was written."));
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogCritical(ex, "Unexpected error in the PROVANCE writer. The batch is failed and the writer continues.");
                        _head = null;
                        FailAll(batch, new LedgerUnavailableException("Unexpected PROVANCE writer error.", ex));
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutdown.
            }
            finally
            {
                _queue.Complete();
                while (_queue.Reader.TryRead(out var leftover))
                {
                    leftover.Completion.TrySetException(new LedgerUnavailableException("PROVANCE writer stopped before the entry was written."));
                }
                _logger.LogInformation("PROVANCE writer stopped for ledger {LedgerId}.", _options.LedgerId);
            }
        }

        private async Task ProcessBatchAsync(List<PendingEntry> batch, CancellationToken ct)
        {
            var pending = new List<PendingEntry>(batch);
            int transientFailures = 0;
            int conflicts = 0;

            while (pending.Count > 0)
            {
                List<LedgerEntry>? sealedEntries = null;
                Exception failure;
                bool isConflict = false;

                try
                {
                    var head = _head ?? await LoadHeadAsync(ct).ConfigureAwait(false);
                    sealedEntries = await SealAsync(pending, head, ct).ConfigureAwait(false);
                    await _store.AppendAsync(sealedEntries, ct).ConfigureAwait(false);

                    for (int i = 0; i < pending.Count; i++)
                    {
                        pending[i].Completion.TrySetResult(sealedEntries[i]);
                    }
                    var last = sealedEntries[^1];
                    _head = new ChainHead(last.Sequence, last.Hash);
                    _onAppended?.Invoke();
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (LedgerConcurrencyException ex)
                {
                    failure = ex;
                    isConflict = true;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                // Whatever happened, our view of the head is no longer trustworthy.
                _head = null;

                if (sealedEntries is not null)
                {
                    int before = pending.Count;
                    await AcknowledgePersistedAsync(pending, ct).ConfigureAwait(false);
                    if (pending.Count < before)
                    {
                        _onAppended?.Invoke();
                    }
                    if (pending.Count == 0)
                    {
                        return;
                    }
                }

                if (isConflict)
                {
                    conflicts++;
                    if (conflicts > _options.MaxConflictRetries)
                    {
                        _logger.LogError(failure, "PROVANCE writer gave up after {Conflicts} sequence conflicts.", conflicts);
                        FailAll(pending, new LedgerUnavailableException($"Gave up after {conflicts} sequence conflicts with other writers.", failure));
                        return;
                    }
                    _logger.LogDebug("Sequence conflict (another writer appended first). Reloading head and resealing {Count} entries.", pending.Count);
                    continue;
                }

                transientFailures++;
                if (transientFailures >= _options.MaxAppendAttempts)
                {
                    _logger.LogError(failure, "PROVANCE append failed {Attempts} times; failing {Count} entries.", transientFailures, pending.Count);
                    FailAll(pending, new LedgerUnavailableException($"The ledger store failed {transientFailures} times.", failure));
                    return;
                }

                var delay = Backoff(transientFailures);
                _logger.LogWarning(failure, "PROVANCE append failed (attempt {Attempt}/{Max}). Retrying in {Delay} ms.",
                    transientFailures, _options.MaxAppendAttempts, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// After a failed append, acknowledges the leading entries that the store did persist. Ids are fixed
        /// per pending entry, so this also catches writes persisted by an earlier attempt.
        /// </summary>
        private async Task AcknowledgePersistedAsync(List<PendingEntry> pending, CancellationToken ct)
        {
            int acknowledged = 0;
            try
            {
                foreach (var entry in pending)
                {
                    var stored = await _store.GetByIdAsync(entry.Id, ct).ConfigureAwait(false);
                    if (stored is null)
                    {
                        break;
                    }
                    entry.Completion.TrySetResult(stored);
                    acknowledged++;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // If an entry was persisted after all, the next append reuses its id, the store reports a
                // conflict and this method runs again.
                _logger.LogWarning(ex, "Could not check which entries were persisted after a failed append.");
            }

            if (acknowledged > 0)
            {
                _logger.LogInformation("{Count} entries were persisted despite the reported failure.", acknowledged);
                pending.RemoveRange(0, acknowledged);
            }
        }

        private async Task<ChainHead> LoadHeadAsync(CancellationToken ct)
        {
            var head = await _store.GetHeadAsync(ct).ConfigureAwait(false);
            if (head is null)
            {
                return new ChainHead(0, _genesisHash);
            }

            if (!string.Equals(head.LedgerId, _options.LedgerId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The store contains ledger '{head.LedgerId}' but this application is configured for '{_options.LedgerId}'. Use one collection per ledger.");
            }
            return new ChainHead(head.Sequence, head.Hash);
        }

        private async Task<List<LedgerEntry>> SealAsync(List<PendingEntry> pending, ChainHead head, CancellationToken ct)
        {
            var sealedEntries = new List<LedgerEntry>(pending.Count);
            long sequence = head.Sequence;
            string previousHash = head.Hash;
            var timestamp = LedgerHashing.NormalizeTimestamp(_time.GetUtcNow());
            string keyId = _signer.KeyId;

            foreach (var item in pending)
            {
                sequence++;
                if (sequence > ProvanceProtocol.MaxSequence)
                {
                    throw new InvalidOperationException("The ledger reached the maximum sequence number.");
                }

                var draft = new LedgerEntry
                {
                    FormatVersion = ProvanceProtocol.FormatVersion,
                    LedgerId = _options.LedgerId,
                    Sequence = sequence,
                    Id = item.Id,
                    Timestamp = timestamp,
                    EventType = item.EventType,
                    PayloadJson = item.PayloadJson,
                    PayloadHash = item.PayloadHash,
                    PreviousHash = previousHash,
                    KeyId = keyId,
                    Hash = string.Empty,
                    Signature = string.Empty
                };

                string hash = LedgerHashing.ComputeEntryHash(draft);
                byte[] signature = await _signer.SignAsync(LedgerHashing.HashToBytes(hash), ct).ConfigureAwait(false);

                sealedEntries.Add(draft with { Hash = hash, Signature = Convert.ToHexString(signature).ToLowerInvariant() });
                previousHash = hash;
            }

            return sealedEntries;
        }

        private TimeSpan Backoff(int failures)
        {
            double baseMs = _options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, failures - 1);
            double jitter = baseMs * 0.2 * Random.Shared.NextDouble();
            return TimeSpan.FromMilliseconds(Math.Min(baseMs + jitter, 2000));
        }

        private static void FailAll(IEnumerable<PendingEntry> entries, Exception error)
        {
            foreach (var entry in entries)
            {
                entry.Completion.TrySetException(error);
            }
        }

        private readonly record struct ChainHead(long Sequence, string Hash);
    }
}
