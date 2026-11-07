using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Provance.Core.Cryptography;
using Provance.Core.Data;
using Provance.Core.Services.Interfaces;

namespace Provance.Core.Export
{
    /// <summary>
    /// Ledger export as JSON Lines: one entry per line, with the field names <c>v</c>, <c>ledgerId</c>, <c>sequence</c>,
    /// <c>id</c>, <c>timestamp</c>, <c>eventType</c>, <c>payloadJson</c>, <c>payloadHash</c>, <c>previousHash</c>,
    /// <c>keyId</c>, <c>hash</c> and <c>signature</c>.
    /// </summary>
    /// <remarks>
    /// The same format is written and read by the Java and Python libraries (<c>provance export</c>), so an export
    /// made by one implementation can be verified or compared by another.
    /// </remarks>
    public static class LedgerJsonLines
    {
        // Non-ASCII characters stay readable; U+2028 and U+2029 are still escaped, so every tool sees one entry per line.
        private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        /// <summary>Serializes one entry to a single line (without the line break).</summary>
        public static string Serialize(LedgerEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            {
                writer.WriteStartObject();
                writer.WriteNumber("v", entry.FormatVersion);
                writer.WriteString("ledgerId", entry.LedgerId);
                writer.WriteNumber("sequence", entry.Sequence);
                writer.WriteString("id", entry.Id.ToString("D"));
                writer.WriteString("timestamp", LedgerHashing.FormatTimestamp(entry.Timestamp));
                writer.WriteString("eventType", entry.EventType);
                if (entry.PayloadJson is null)
                {
                    writer.WriteNull("payloadJson");
                }
                else
                {
                    writer.WriteString("payloadJson", entry.PayloadJson);
                }
                writer.WriteString("payloadHash", entry.PayloadHash);
                writer.WriteString("previousHash", entry.PreviousHash);
                writer.WriteString("keyId", entry.KeyId);
                writer.WriteString("hash", entry.Hash);
                writer.WriteString("signature", entry.Signature);
                writer.WriteEndObject();
            }
            return Escape(Encoding.UTF8.GetString(buffer.ToArray()));
        }

        /// <summary>Parses one line written by any PROVANCE implementation.</summary>
        /// <exception cref="FormatException">The line is not a valid entry.</exception>
        public static LedgerEntry Parse(string line)
        {
            ArgumentNullException.ThrowIfNull(line);
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var payload = root.GetProperty("payloadJson");
                return new LedgerEntry
                {
                    FormatVersion = root.GetProperty("v").GetInt32(),
                    LedgerId = root.GetProperty("ledgerId").GetString()!,
                    Sequence = root.GetProperty("sequence").GetInt64(),
                    Id = Guid.Parse(root.GetProperty("id").GetString()!),
                    Timestamp = DateTimeOffset.Parse(root.GetProperty("timestamp").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                    EventType = root.GetProperty("eventType").GetString()!,
                    PayloadJson = payload.ValueKind == JsonValueKind.Null ? null : payload.GetString(),
                    PayloadHash = root.GetProperty("payloadHash").GetString()!,
                    PreviousHash = root.GetProperty("previousHash").GetString()!,
                    KeyId = root.GetProperty("keyId").GetString()!,
                    Hash = root.GetProperty("hash").GetString()!,
                    Signature = root.GetProperty("signature").GetString()!
                };
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentNullException or FormatException)
            {
                throw new FormatException($"Not a PROVANCE ledger entry: {ex.Message}", ex);
            }
        }

        /// <summary>Writes entries to <paramref name="stream"/>, one per line, and returns how many were written.</summary>
        public static async Task<long> WriteAsync(IAsyncEnumerable<LedgerEntry> entries, Stream stream, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entries);
            ArgumentNullException.ThrowIfNull(stream);
            long count = 0;
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };
            await foreach (var entry in entries.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                await writer.WriteLineAsync(Serialize(entry).AsMemory(), cancellationToken).ConfigureAwait(false);
                count++;
            }
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            return count;
        }

        /// <summary>Exports a whole ledger to <paramref name="stream"/>.</summary>
        public static Task<long> WriteAsync(ILedgerStore store, Stream stream, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(store);
            return WriteAsync(store.ReadAsync(1, cancellationToken), stream, cancellationToken);
        }

        /// <summary>Reads entries from a stream, in file order. Blank lines are skipped.</summary>
        public static async IAsyncEnumerable<LedgerEntry> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(stream);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            int lineNumber = 0;
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                LedgerEntry entry;
                try
                {
                    entry = Parse(line);
                }
                catch (FormatException ex)
                {
                    throw new FormatException($"Line {lineNumber}: {ex.Message}", ex);
                }
                yield return entry;
            }
        }

        /// <summary>Loads an export file as a read-only store, to verify it or compare it with another copy.</summary>
        public static async Task<JsonLinesLedgerFile> OpenAsync(string path, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            await using var stream = File.OpenRead(path);
            var entries = new List<LedgerEntry>();
            await foreach (var entry in ReadAsync(stream, cancellationToken).ConfigureAwait(false))
            {
                entries.Add(entry);
            }
            return new JsonLinesLedgerFile(entries);
        }

        private const char LineSeparator = (char)0x2028;
        private const char ParagraphSeparator = (char)0x2029;

        private static string Escape(string json)
            => json.Replace(LineSeparator.ToString(), "\\u2028", StringComparison.Ordinal)
                   .Replace(ParagraphSeparator.ToString(), "\\u2029", StringComparison.Ordinal);
    }

    /// <summary>
    /// A ledger export loaded in memory. Read only: verification and comparison read it like any other store,
    /// in the order of the file, so entries that were moved or duplicated in the file are reported.
    /// </summary>
    public sealed class JsonLinesLedgerFile : ILedgerStore
    {
        private readonly IReadOnlyList<LedgerEntry> _entries;

        /// <summary>Wraps entries, kept in the given order.</summary>
        public JsonLinesLedgerFile(IReadOnlyList<LedgerEntry> entries)
        {
            ArgumentNullException.ThrowIfNull(entries);
            _entries = entries;
        }

        /// <summary>The entries, in file order.</summary>
        public IReadOnlyList<LedgerEntry> Entries => _entries;

        /// <inheritdoc />
        public Task<LedgerEntry?> GetHeadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_entries.Count == 0 ? null : _entries.MaxBy(e => e.Sequence));

        /// <inheritdoc />
        public Task<LedgerEntry?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(_entries.FirstOrDefault(e => e.Id == id));

        /// <inheritdoc />
        public async IAsyncEnumerable<LedgerEntry> ReadAsync(long fromSequence = 1, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var entry in _entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Sequence >= fromSequence)
                {
                    yield return entry;
                }
            }
            await Task.CompletedTask.ConfigureAwait(false);
        }

        /// <summary>Not supported: an export is read only.</summary>
        public Task AppendAsync(IReadOnlyList<LedgerEntry> entries, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("A JSON Lines export is read only.");

        /// <summary>Not supported: an export is read only.</summary>
        public Task<bool> RedactPayloadAsync(Guid id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("A JSON Lines export is read only.");
    }
}
