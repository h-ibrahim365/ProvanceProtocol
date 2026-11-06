using System.Globalization;
using Provance.Core.Data;

namespace Provance.Core.Checkpoints
{
    /// <summary>
    /// Writes each checkpoint to <c>{directory}/{ledgerId}/{sequence}.json</c> and never overwrites a file.
    /// </summary>
    /// <remarks>
    /// Useful when the directory is mounted from immutable storage (for example an Azure Files share or
    /// a bucket with retention lock). On a plain local disk it only protects against mistakes, not against
    /// someone with access to the machine.
    /// </remarks>
    public sealed class FileCheckpointSink : ILedgerCheckpointSink
    {
        private readonly string _directory;

        /// <summary>Creates a sink writing under <paramref name="directory"/>.</summary>
        public FileCheckpointSink(string directory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(directory);
            _directory = directory;
        }

        /// <inheritdoc />
        public async Task PublishAsync(LedgerCheckpoint checkpoint, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(checkpoint);
            string folder = Path.Combine(_directory, checkpoint.LedgerId);
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, FileName(checkpoint.Sequence));

            if (File.Exists(path))
            {
                return; // same head already published
            }

            try
            {
                await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(checkpoint.ToJson().AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another instance published the same head concurrently.
            }
        }

        /// <summary>Reads the checkpoint with the highest sequence for a ledger, or <c>null</c> if there is none.</summary>
        public static async Task<LedgerCheckpoint?> ReadLatestAsync(string directory, string ledgerId, CancellationToken cancellationToken = default)
        {
            string folder = Path.Combine(directory, ledgerId);
            if (!Directory.Exists(folder))
            {
                return null;
            }

            string? latest = Directory.EnumerateFiles(folder, "*.json")
                .Select(path => (path, sequence: long.TryParse(Path.GetFileNameWithoutExtension(path), NumberStyles.None, CultureInfo.InvariantCulture, out long s) ? s : -1))
                .Where(x => x.sequence > 0)
                .OrderByDescending(x => x.sequence)
                .Select(x => x.path)
                .FirstOrDefault();

            return latest is null
                ? null
                : LedgerCheckpoint.FromJson(await File.ReadAllTextAsync(latest, cancellationToken).ConfigureAwait(false));
        }

        private static string FileName(long sequence) => sequence.ToString("D16", CultureInfo.InvariantCulture) + ".json";
    }
}
