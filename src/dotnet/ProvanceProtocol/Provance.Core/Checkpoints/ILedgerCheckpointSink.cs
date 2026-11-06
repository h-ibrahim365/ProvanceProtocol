using Provance.Core.Data;

namespace Provance.Core.Checkpoints
{
    /// <summary>
    /// Destination of automatic checkpoints. Implement it for the storage you trust: an object storage
    /// bucket with retention lock, WORM storage, a timestamping service, a log owned by another team.
    /// </summary>
    /// <remarks>
    /// The value of a checkpoint depends entirely on where it goes. A sink the application can overwrite
    /// with the same credentials it uses for the ledger adds little protection.
    /// </remarks>
    public interface ILedgerCheckpointSink
    {
        /// <summary>Publishes a checkpoint. Must not overwrite an existing checkpoint.</summary>
        Task PublishAsync(LedgerCheckpoint checkpoint, CancellationToken cancellationToken = default);
    }
}
