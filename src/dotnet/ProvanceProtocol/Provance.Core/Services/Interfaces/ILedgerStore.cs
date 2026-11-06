using Provance.Core.Data;

namespace Provance.Core.Services.Interfaces
{
    /// <summary>
    /// Persistence of one ledger. Implementations do not compute hashes; they store what the writer
    /// sealed and enforce uniqueness, which is what keeps the chain linear when several application
    /// instances write to the same ledger.
    /// </summary>
    public interface ILedgerStore
    {
        /// <summary>Returns the entry with the highest sequence, or <c>null</c> if the ledger is empty.</summary>
        Task<LedgerEntry?> GetHeadAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Appends entries with contiguous, ascending sequences.
        /// </summary>
        /// <remarks>
        /// <para>Must throw <see cref="Exceptions.LedgerConcurrencyException"/> when an entry with the same
        /// <see cref="LedgerEntry.Sequence"/> or <see cref="LedgerEntry.Id"/> already exists.</para>
        /// <para>Entries must be inserted in order. If the call fails midway, a prefix of the batch may be
        /// persisted: the writer detects that through <see cref="GetByIdAsync"/>.</para>
        /// </remarks>
        Task AppendAsync(IReadOnlyList<LedgerEntry> entries, CancellationToken cancellationToken = default);

        /// <summary>Returns the entry with the given id, or <c>null</c>.</summary>
        Task<LedgerEntry?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

        /// <summary>Streams entries with <c>Sequence &gt;= fromSequence</c> in ascending sequence order.</summary>
        IAsyncEnumerable<LedgerEntry> ReadAsync(long fromSequence = 1, CancellationToken cancellationToken = default);

        /// <summary>
        /// Removes the payload of an entry (sets <see cref="LedgerEntry.PayloadJson"/> to <c>null</c>) and keeps
        /// everything else. The chain stays verifiable because only <see cref="LedgerEntry.PayloadHash"/> is signed.
        /// </summary>
        /// <returns><c>true</c> if the entry exists.</returns>
        Task<bool> RedactPayloadAsync(Guid id, CancellationToken cancellationToken = default);
    }
}
