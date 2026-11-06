using System.Runtime.CompilerServices;
using Provance.Core.Data;
using Provance.Core.Services;
using Provance.Core.Services.Interfaces;

namespace Provance.Core.Tests.TestSupport
{
    internal enum AppendFault
    {
        None,

        /// <summary>Throws before anything is written.</summary>
        FailBefore,

        /// <summary>Writes the whole batch, then throws (lost acknowledgement, the classic network timeout).</summary>
        FailAfterPersist,

        /// <summary>Writes only the first entry of the batch, then throws.</summary>
        PersistFirstThenFail
    }

    /// <summary>Wraps an <see cref="InMemoryLedgerStore"/> and injects failures.</summary>
    internal sealed class FaultyStore : ILedgerStore
    {
        private int _appendCalls;
        private int _getByIdCalls;

        public FaultyStore(InMemoryLedgerStore? inner = null) => Inner = inner ?? new InMemoryLedgerStore();

        public InMemoryLedgerStore Inner { get; }

        /// <summary>Fault for the n-th append call (0 based).</summary>
        public Func<int, AppendFault> OnAppend { get; set; } = _ => AppendFault.None;

        /// <summary>Returns true when the n-th GetById call (0 based) must throw.</summary>
        public Func<int, bool> FailGetById { get; set; } = _ => false;

        /// <summary>Awaited at the start of every append; use it to block the writer.</summary>
        public Func<CancellationToken, Task>? AppendGate { get; set; }

        public int AppendCalls => Volatile.Read(ref _appendCalls);

        public Task<LedgerEntry?> GetHeadAsync(CancellationToken cancellationToken = default) => Inner.GetHeadAsync(cancellationToken);

        public async Task AppendAsync(IReadOnlyList<LedgerEntry> entries, CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref _appendCalls) - 1;
            if (AppendGate is not null)
            {
                await AppendGate(cancellationToken);
            }

            switch (OnAppend(call))
            {
                case AppendFault.FailBefore:
                    throw new TimeoutException("Simulated store timeout (nothing written).");
                case AppendFault.FailAfterPersist:
                    await Inner.AppendAsync(entries, cancellationToken);
                    throw new TimeoutException("Simulated store timeout (batch was written).");
                case AppendFault.PersistFirstThenFail:
                    await Inner.AppendAsync([entries[0]], cancellationToken);
                    throw new TimeoutException("Simulated failure after the first entry.");
                default:
                    await Inner.AppendAsync(entries, cancellationToken);
                    break;
            }
        }

        public Task<LedgerEntry?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref _getByIdCalls) - 1;
            if (FailGetById(call))
            {
                throw new TimeoutException("Simulated read timeout.");
            }
            return Inner.GetByIdAsync(id, cancellationToken);
        }

        public async IAsyncEnumerable<LedgerEntry> ReadAsync(long fromSequence = 1, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var entry in Inner.ReadAsync(fromSequence, cancellationToken))
            {
                yield return entry;
            }
        }

        public Task<bool> RedactPayloadAsync(Guid id, CancellationToken cancellationToken = default) => Inner.RedactPayloadAsync(id, cancellationToken);
    }
}
