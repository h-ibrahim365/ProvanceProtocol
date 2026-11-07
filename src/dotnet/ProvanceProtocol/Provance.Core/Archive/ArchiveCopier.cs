using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Provance.Core.Comparison;
using Provance.Core.Data;
using Provance.Core.Exceptions;
using Provance.Core.Options;
using Provance.Core.Services.Interfaces;

namespace Provance.Core.Archive
{
    /// <summary>State of the copy to the archive, from <see cref="ILedgerService.GetArchiveStatus"/>.</summary>
    public sealed record LedgerArchiveStatus
    {
        /// <summary><c>false</c> when copying stopped because the primary store does not continue the archive.</summary>
        public required bool IsHealthy { get; init; }

        /// <summary>Last sequence known to be in the archive (0 when nothing was copied yet).</summary>
        public long ArchivedThrough { get; init; }

        /// <summary>Why copying stopped, or the last transient error; <c>null</c> when everything is fine.</summary>
        public string? Problem { get; init; }

        /// <summary>Time of the last successful copy pass.</summary>
        public DateTimeOffset? LastCopyAt { get; init; }
    }

    /// <summary>
    /// Copies every entry of the primary store to the archive, in the background, after it is written. Each entry is
    /// checked before it is copied (it must continue the archive's chain and verify on its own), so an entry modified
    /// in the primary store before it was copied is not propagated: copying stops and the problem is logged.
    /// </summary>
    internal sealed class ArchiveCopier : BackgroundService
    {
        private readonly ILedgerStore _primary;
        private readonly ILedgerStore? _archive;
        private readonly LedgerComparer _checker;
        private readonly ProvanceOptions _options;
        private readonly TimeProvider _time;
        private readonly ILogger<ArchiveCopier> _logger;
        private readonly SemaphoreSlim _wake = new(0, 1);
        private readonly SemaphoreSlim _gate = new(1, 1);
        private LedgerArchiveStatus _status = new() { IsHealthy = true };
        private string? _loggedProblem;

        public ArchiveCopier(ILedgerStore primary, ILedgerStore? archive, LedgerComparer checker, ProvanceOptions options, TimeProvider time, ILogger<ArchiveCopier> logger)
        {
            _primary = primary;
            _archive = archive;
            _checker = checker;
            _options = options;
            _time = time;
            _logger = logger;
        }

        public bool Enabled => _archive is not null;

        public ILedgerStore? Archive => _archive;

        public LedgerArchiveStatus Status => Volatile.Read(ref _status);

        /// <summary>Wakes the copy loop (called by the writer after each append).</summary>
        public void Signal()
        {
            if (_archive is null) return;
            try
            {
                if (_wake.CurrentCount == 0) _wake.Release();
            }
            catch (SemaphoreFullException)
            {
                // Already signaled.
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_archive is null)
            {
                return;
            }

            _logger.LogInformation("PROVANCE archive copy started for ledger {LedgerId}.", _options.LedgerId);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CopyAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    SetStatus(Status with { Problem = $"Archive copy failed: {ex.Message}" });
                    _logger.LogWarning(ex, "PROVANCE archive copy failed for ledger {LedgerId}; it will be retried.", _options.LedgerId);
                }

                try
                {
                    await _wake.WaitAsync(_options.ArchiveInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
            if (_archive is null)
            {
                return;
            }

            // The writer stops first: copy what it wrote last, within the shutdown timeout.
            try
            {
                await CopyAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PROVANCE could not copy the last entries of ledger {LedgerId} to the archive before shutdown; they are copied at the next start.", _options.LedgerId);
            }
        }

        /// <summary>Copies every entry the archive does not have yet. Returns the last archived sequence.</summary>
        public async Task<long> CopyAsync(CancellationToken ct)
        {
            if (_archive is null)
            {
                return 0;
            }

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                for (int conflicts = 0; ; conflicts++)
                {
                    try
                    {
                        return await CopyOnceAsync(ct).ConfigureAwait(false);
                    }
                    catch (LedgerConcurrencyException) when (conflicts < 10)
                    {
                        // Another application instance copied the same entries first: start again from the archive head.
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<long> CopyOnceAsync(CancellationToken ct)
        {
            var head = await _archive!.GetHeadAsync(ct).ConfigureAwait(false);
            if (head is not null && !string.Equals(head.LedgerId, _options.LedgerId, StringComparison.Ordinal))
            {
                return Stop(head.Sequence, $"The archive contains ledger '{head.LedgerId}', not '{_options.LedgerId}'. Use one archive table per ledger.");
            }

            long next = (head?.Sequence ?? 0) + 1;
            string previous = head?.Hash ?? _options.EffectiveGenesisHash;
            long archived = head?.Sequence ?? 0;
            var batch = new List<LedgerEntry>(_options.MaxBatchSize);

            while (true)
            {
                batch.Clear();
                string? problem = null;

                await foreach (var entry in _primary.ReadAsync(next, ct).ConfigureAwait(false))
                {
                    if (entry.Sequence != next)
                    {
                        problem = entry.Sequence > next
                            ? $"Entry {next} is missing from the primary store but was never copied to the archive."
                            : $"Entry {entry.Sequence} appears twice or out of order in the primary store.";
                    }
                    else if (!string.Equals(entry.PreviousHash, previous, StringComparison.Ordinal))
                    {
                        problem = $"Entry {entry.Sequence} of the primary store does not link to the archived entry {entry.Sequence - 1}: the primary store was modified.";
                    }
                    else if (!await _checker.VerifiesAloneAsync(entry, ct).ConfigureAwait(false))
                    {
                        problem = $"Entry {entry.Sequence} of the primary store does not verify: it was modified before it was copied.";
                    }

                    if (problem is not null)
                    {
                        break;
                    }

                    batch.Add(entry);
                    previous = entry.Hash;
                    next++;
                    if (batch.Count >= _options.MaxBatchSize)
                    {
                        break;
                    }
                }

                if (batch.Count > 0)
                {
                    await _archive.AppendAsync(batch, ct).ConfigureAwait(false);
                    archived = batch[^1].Sequence;
                }

                if (problem is not null)
                {
                    return Stop(archived, problem);
                }

                if (batch.Count < _options.MaxBatchSize)
                {
                    break;
                }
            }

            _loggedProblem = null;
            SetStatus(new LedgerArchiveStatus { IsHealthy = true, ArchivedThrough = archived, LastCopyAt = _time.GetUtcNow() });
            return archived;
        }

        private long Stop(long archived, string problem)
        {
            if (!string.Equals(_loggedProblem, problem, StringComparison.Ordinal))
            {
                _logger.LogCritical("PROVANCE stopped copying ledger {LedgerId} to the archive: {Problem}", _options.LedgerId, problem);
                _loggedProblem = problem;
            }
            SetStatus(new LedgerArchiveStatus { IsHealthy = false, ArchivedThrough = archived, Problem = problem, LastCopyAt = Status.LastCopyAt });
            return archived;
        }

        private void SetStatus(LedgerArchiveStatus status) => Volatile.Write(ref _status, status);
    }
}
