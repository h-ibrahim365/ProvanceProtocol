using System.Globalization;
using System.Text;
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

            if (await IsValidAsync(path, checkpoint, cancellationToken).ConfigureAwait(false))
            {
                return; // same head already published
            }

            // Written to a temporary file, then renamed: a crash never leaves a truncated checkpoint behind.
            string temp = Path.Combine(folder, $".{FileName(checkpoint.Sequence)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(checkpoint.ToJson());
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                try
                {
                    File.Move(temp, path, overwrite: false);
                }
                catch (IOException) when (File.Exists(path))
                {
                    // Another instance published the same head concurrently, or an earlier crash left a broken file
                    // (written before checkpoints were renamed into place): only a broken file is replaced.
                    if (!await IsValidAsync(path, checkpoint, cancellationToken).ConfigureAwait(false))
                    {
                        File.Move(temp, path, overwrite: true);
                    }
                }
            }
            finally
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
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

            var candidates = Directory.EnumerateFiles(folder, "*.json")
                .Select(path => (path, sequence: long.TryParse(Path.GetFileNameWithoutExtension(path), NumberStyles.None, CultureInfo.InvariantCulture, out long s) ? s : -1))
                .Where(x => x.sequence > 0)
                .OrderByDescending(x => x.sequence);

            foreach (var (path, _) in candidates)
            {
                var checkpoint = await TryReadAsync(path, cancellationToken).ConfigureAwait(false);
                if (checkpoint is not null)
                {
                    return checkpoint; // a broken file is skipped rather than hiding the older valid ones
                }
            }
            return null;
        }

        private static async Task<bool> IsValidAsync(string path, LedgerCheckpoint expected, CancellationToken ct)
        {
            var existing = await TryReadAsync(path, ct).ConfigureAwait(false);
            return existing is not null
                && existing.Sequence == expected.Sequence
                && string.Equals(existing.LedgerId, expected.LedgerId, StringComparison.Ordinal);
        }

        private static async Task<LedgerCheckpoint?> TryReadAsync(string path, CancellationToken ct)
        {
            try
            {
                return LedgerCheckpoint.FromJson(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is FileNotFoundException or System.Text.Json.JsonException or FormatException or IOException)
            {
                return null;
            }
        }

        private static string FileName(long sequence) => sequence.ToString("D16", CultureInfo.InvariantCulture) + ".json";
    }
}
