namespace Provance.Core.Services.Interfaces
{
    /// <summary>
    /// A store that can remove its oldest entries. Required by retention (<see cref="ILedgerService.ApplyRetentionAsync"/>);
    /// the built-in in-memory and Entity Framework Core stores implement it.
    /// </summary>
    public interface IPrunableLedgerStore : ILedgerStore
    {
        /// <summary>Deletes every entry with a sequence lower than <paramref name="sequence"/>.</summary>
        /// <returns>The number of deleted entries.</returns>
        Task<long> DeleteBeforeAsync(long sequence, CancellationToken cancellationToken = default);
    }
}
