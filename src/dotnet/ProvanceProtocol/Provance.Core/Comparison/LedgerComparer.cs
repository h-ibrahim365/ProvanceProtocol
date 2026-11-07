using System.Text.Json;
using Provance.Core.Cryptography;
using Provance.Core.Data;
using Provance.Core.Retention;
using Provance.Core.Services.Interfaces;
using Provance.Core.Signing;

namespace Provance.Core.Comparison
{
    /// <summary>
    /// Compares the primary store of a ledger with a second copy (the archive written by the mirror, or an export)
    /// and lists, field by field, what differs.
    /// </summary>
    /// <remarks>
    /// Verification tells which entry was changed; it cannot tell the original value, because the ledger only keeps
    /// its hash. With two copies, the copy whose entry still verifies on its own is the original, and the comparison
    /// shows the original and the current value of every changed field.
    /// </remarks>
    public sealed class LedgerComparer
    {
        private readonly ILedgerSigner _signer;
        private readonly string _ledgerId;

        /// <summary>Creates a comparer. The signer must know every key used in the ledger, retired ones included.</summary>
        public LedgerComparer(ILedgerSigner signer, string ledgerId)
        {
            ArgumentNullException.ThrowIfNull(signer);
            ArgumentException.ThrowIfNullOrWhiteSpace(ledgerId);
            _signer = signer;
            _ledgerId = ledgerId;
        }

        /// <summary>Compares two copies of the ledger, reading both in sequence order.</summary>
        /// <param name="primary">The store the application writes to.</param>
        /// <param name="archive">The second copy: the mirror's archive, or an export file.</param>
        /// <param name="maxDifferences">Stop listing differences after this many. Default 1000.</param>
        /// <param name="cancellationToken">Cancels the comparison.</param>
        public async Task<LedgerComparison> CompareAsync(ILedgerStore primary, ILedgerStore archive, int maxDifferences = 1000, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(primary);
            ArgumentNullException.ThrowIfNull(archive);
            ArgumentOutOfRangeException.ThrowIfLessThan(maxDifferences, 1);

            long archiveLast = (await archive.GetHeadAsync(cancellationToken).ConfigureAwait(false))?.Sequence ?? 0;
            var differences = new List<EntryDifference>();
            bool truncated = false;
            long compared = 0, notYetCopied = 0, primaryLast = 0;

            // Archive entries before the first primary entry: removed by retention, or deleted from the primary store.
            long primaryFirst = 0;
            string? primaryFirstHash = null;
            long onlyInArchiveAtStart = 0;
            bool startVouched = false;

            void Add(EntryDifference difference)
            {
                if (differences.Count < maxDifferences) differences.Add(difference);
                else truncated = true;
            }

            await using var p = primary.ReadAsync(1, cancellationToken).GetAsyncEnumerator(cancellationToken);
            await using var a = archive.ReadAsync(1, cancellationToken).GetAsyncEnumerator(cancellationToken);
            bool hasP = await p.MoveNextAsync().ConfigureAwait(false);
            bool hasA = await a.MoveNextAsync().ConfigureAwait(false);
            long lastP = 0, lastA = 0;

            while (hasP || hasA)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pe = hasP ? p.Current : null;
                var ae = hasA ? a.Current : null;

                // Rows out of order or repeated: reported as changed at their position, then skipped.
                if (pe is not null && pe.Sequence <= lastP)
                {
                    Add(new EntryDifference
                    {
                        Sequence = pe.Sequence,
                        Kind = EntryDifferenceKind.MissingFromArchive,
                        EventType = pe.EventType,
                        Fields = [new FieldDifference("sequence", null, $"{pe.Sequence} (out of order, after {lastP})")]
                    });
                    hasP = await p.MoveNextAsync().ConfigureAwait(false);
                    continue;
                }
                if (ae is not null && ae.Sequence <= lastA)
                {
                    Add(new EntryDifference
                    {
                        Sequence = ae.Sequence,
                        Kind = EntryDifferenceKind.MissingFromPrimary,
                        EventType = ae.EventType,
                        Fields = [new FieldDifference("sequence", $"{ae.Sequence} (out of order, after {lastA})", null)]
                    });
                    hasA = await a.MoveNextAsync().ConfigureAwait(false);
                    continue;
                }

                if (pe is not null && primaryFirst == 0)
                {
                    primaryFirst = pe.Sequence;
                    primaryFirstHash = pe.Hash;
                }

                if (pe is not null && ae is not null && pe.Sequence == ae.Sequence)
                {
                    compared++;
                    primaryLast = pe.Sequence;
                    CheckRetentionRecord(pe);
                    var difference = await CompareEntryAsync(ae, pe, cancellationToken).ConfigureAwait(false);
                    if (difference is not null) Add(difference);
                    lastP = pe.Sequence;
                    lastA = ae.Sequence;
                    hasP = await p.MoveNextAsync().ConfigureAwait(false);
                    hasA = await a.MoveNextAsync().ConfigureAwait(false);
                }
                else if (pe is not null && (ae is null || pe.Sequence < ae.Sequence))
                {
                    primaryLast = pe.Sequence;
                    CheckRetentionRecord(pe);
                    if (pe.Sequence > archiveLast)
                    {
                        notYetCopied++;
                    }
                    else
                    {
                        Add(new EntryDifference { Sequence = pe.Sequence, Kind = EntryDifferenceKind.MissingFromArchive, EventType = pe.EventType });
                    }
                    lastP = pe.Sequence;
                    hasP = await p.MoveNextAsync().ConfigureAwait(false);
                }
                else
                {
                    if (lastP == 0)
                    {
                        // The primary store starts later, or is empty: decide at the end (retention or deletion).
                        onlyInArchiveAtStart++;
                    }
                    else
                    {
                        Add(new EntryDifference { Sequence = ae!.Sequence, Kind = EntryDifferenceKind.MissingFromPrimary, EventType = ae.EventType });
                    }
                    lastA = ae!.Sequence;
                    hasA = await a.MoveNextAsync().ConfigureAwait(false);
                }
            }

            long removedByRetention = 0;
            if (onlyInArchiveAtStart > 0)
            {
                if (startVouched)
                {
                    removedByRetention = onlyInArchiveAtStart;
                }
                else
                {
                    long first = 1;
                    long through = primaryFirst > 0 ? primaryFirst - 1 : archiveLast;
                    differences.Insert(0, new EntryDifference { Sequence = first, ThroughSequence = through, Kind = EntryDifferenceKind.MissingFromPrimary });
                    if (differences.Count > maxDifferences)
                    {
                        differences.RemoveAt(differences.Count - 1);
                        truncated = true;
                    }
                }
            }

            return new LedgerComparison
            {
                Differences = differences,
                DifferencesTruncated = truncated,
                Compared = compared,
                RemovedByRetention = removedByRetention,
                NotYetCopied = notYetCopied,
                PrimaryLastSequence = primaryLast,
                ArchiveLastSequence = archiveLast
            };

            void CheckRetentionRecord(LedgerEntry entry)
            {
                if (!startVouched && primaryFirst > 1
                    && string.Equals(entry.EventType, LedgerRetentionRecord.EventType, StringComparison.Ordinal)
                    && LedgerRetentionRecord.TryParse(entry.PayloadJson, out long keptFrom, out string keptHash)
                    && keptFrom == primaryFirst
                    && string.Equals(keptHash, primaryFirstHash, StringComparison.Ordinal))
                {
                    startVouched = true;
                }
            }
        }

        /// <summary>Compares two copies of the entry at one sequence. Returns <c>null</c> when they are identical.</summary>
        public async Task<EntryDifference?> CompareEntryAsync(LedgerEntry archive, LedgerEntry primary, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(archive);
            ArgumentNullException.ThrowIfNull(primary);

            var fields = new List<FieldDifference>();
            void Field(string name, string? archiveValue, string? primaryValue)
            {
                if (!string.Equals(archiveValue, primaryValue, StringComparison.Ordinal))
                    fields.Add(new FieldDifference(name, archiveValue, primaryValue));
            }

            Field("id", archive.Id.ToString("D"), primary.Id.ToString("D"));
            Field("eventType", archive.EventType, primary.EventType);
            Field("timestamp", LedgerHashing.FormatTimestamp(archive.Timestamp), LedgerHashing.FormatTimestamp(primary.Timestamp));
            Field("ledgerId", archive.LedgerId, primary.LedgerId);
            Field("v", archive.FormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), primary.FormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Field("keyId", archive.KeyId, primary.KeyId);

            bool redactedInOne = (archive.PayloadJson is null) != (primary.PayloadJson is null);
            if (archive.PayloadJson is not null && primary.PayloadJson is not null)
            {
                ComparePayloads(archive.PayloadJson, primary.PayloadJson, fields);
            }
            else if (redactedInOne && !string.Equals(archive.PayloadHash, primary.PayloadHash, StringComparison.Ordinal))
            {
                Field("payload", archive.PayloadJson, primary.PayloadJson);
            }

            Field("payloadHash", archive.PayloadHash, primary.PayloadHash);
            Field("previousHash", archive.PreviousHash, primary.PreviousHash);
            Field("hash", archive.Hash, primary.Hash);
            Field("signature", archive.Signature, primary.Signature);

            if (fields.Count == 0)
            {
                return redactedInOne
                    ? new EntryDifference { Sequence = primary.Sequence, Kind = EntryDifferenceKind.RedactedInOneCopy, EventType = primary.EventType }
                    : null;
            }

            bool archiveVerifies = await VerifiesAloneAsync(archive, cancellationToken).ConfigureAwait(false);
            bool primaryVerifies = await VerifiesAloneAsync(primary, cancellationToken).ConfigureAwait(false);
            var original = (archiveVerifies, primaryVerifies) switch
            {
                (true, false) => OriginalCopy.Archive,
                (false, true) => OriginalCopy.Primary,
                (true, true) => OriginalCopy.BothVerify,
                _ => OriginalCopy.NeitherVerifies
            };

            return new EntryDifference
            {
                Sequence = primary.Sequence,
                Kind = EntryDifferenceKind.Changed,
                Original = original,
                EventType = original == OriginalCopy.Primary ? primary.EventType : archive.EventType,
                Fields = fields
            };
        }

        /// <summary>
        /// Checks one entry without its neighbors: payload hash, envelope hash and signature. A copy that passes was
        /// written by the key holder; a copy that fails was modified afterwards.
        /// </summary>
        public async Task<bool> VerifiesAloneAsync(LedgerEntry entry, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (entry.FormatVersion != ProvanceProtocol.FormatVersion || !string.Equals(entry.LedgerId, _ledgerId, StringComparison.Ordinal))
                return false;
            if (entry.PayloadJson is not null && !string.Equals(LedgerHashing.Sha256Hex(entry.PayloadJson), entry.PayloadHash, StringComparison.Ordinal))
                return false;

            string hash;
            try
            {
                hash = LedgerHashing.ComputeEntryHash(entry);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                return false;
            }
            if (!string.Equals(hash, entry.Hash, StringComparison.Ordinal) || !LedgerHashing.IsLowerHex(entry.Signature))
                return false;

            var check = await _signer.VerifyAsync(entry.KeyId, LedgerHashing.HashToBytes(hash), Convert.FromHexString(entry.Signature), cancellationToken).ConfigureAwait(false);
            return check == SignatureCheck.Valid;
        }

        private static void ComparePayloads(string archiveJson, string primaryJson, List<FieldDifference> fields)
        {
            if (string.Equals(archiveJson, primaryJson, StringComparison.Ordinal))
            {
                return;
            }

            Dictionary<string, string>? archiveFields = Flatten(archiveJson);
            Dictionary<string, string>? primaryFields = Flatten(primaryJson);
            if (archiveFields is null || primaryFields is null)
            {
                fields.Add(new FieldDifference("payload", archiveJson, primaryJson));
                return;
            }

            foreach (string path in archiveFields.Keys.Union(primaryFields.Keys).Order(StringComparer.Ordinal))
            {
                archiveFields.TryGetValue(path, out string? before);
                primaryFields.TryGetValue(path, out string? after);
                if (!string.Equals(before, after, StringComparison.Ordinal))
                {
                    fields.Add(new FieldDifference(path, before, after));
                }
            }
        }

        /// <summary>Flattens a JSON document into path to raw JSON value. Returns <c>null</c> when it does not parse.</summary>
        private static Dictionary<string, string>? Flatten(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var result = new Dictionary<string, string>(StringComparer.Ordinal);
                Walk(document.RootElement, "payload", result);
                return result;
            }
            catch (JsonException)
            {
                return null;
            }

            static void Walk(JsonElement element, string path, Dictionary<string, string> result)
            {
                switch (element.ValueKind)
                {
                    case JsonValueKind.Object when element.EnumerateObject().Any():
                        foreach (var property in element.EnumerateObject())
                            Walk(property.Value, $"{path}.{property.Name}", result);
                        break;
                    case JsonValueKind.Array when element.GetArrayLength() > 0:
                        int i = 0;
                        foreach (var item in element.EnumerateArray())
                            Walk(item, $"{path}[{i++}]", result);
                        break;
                    default:
                        result[path] = element.GetRawText();
                        break;
                }
            }
        }
    }
}
