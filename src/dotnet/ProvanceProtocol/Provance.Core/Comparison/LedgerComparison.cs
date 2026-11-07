using System.Text;

namespace Provance.Core.Comparison
{
    /// <summary>What differs between the two copies of an entry.</summary>
    public enum EntryDifferenceKind
    {
        /// <summary>Both copies have the entry, with different content.</summary>
        Changed,

        /// <summary>The archive has the entry, the primary store does not: it was deleted from the primary store.</summary>
        MissingFromPrimary,

        /// <summary>
        /// The primary store has the entry and the archive does not, although the archive already holds later entries:
        /// it was deleted from the archive, or inserted into the primary store.
        /// </summary>
        MissingFromArchive,

        /// <summary>
        /// The payload was erased in one copy only. Not tampering: the entry still verifies. Erase it in the other copy
        /// too if the erasure was a legal request.
        /// </summary>
        RedactedInOneCopy
    }

    /// <summary>Which copy of a changed entry is the original, judged by checking each copy on its own.</summary>
    public enum OriginalCopy
    {
        /// <summary>The archive copy verifies and the primary copy does not: the primary store was modified.</summary>
        Archive,

        /// <summary>The primary copy verifies and the archive copy does not: the archive was modified.</summary>
        Primary,

        /// <summary>Both copies verify on their own: whoever changed one of them holds the signing key.</summary>
        BothVerify,

        /// <summary>Neither copy verifies on its own: both were modified.</summary>
        NeitherVerifies
    }

    /// <summary>One field that differs between the two copies of an entry.</summary>
    /// <param name="Field">
    /// Envelope field name (<c>eventType</c>, <c>timestamp</c>, ...) or a path in the payload, such as
    /// <c>payload.data.amount</c> or <c>payload.items[2]</c>.
    /// </param>
    /// <param name="ArchiveValue">Value in the archive, as JSON for payload fields; <c>null</c> when the field is absent.</param>
    /// <param name="PrimaryValue">Value in the primary store; <c>null</c> when the field is absent.</param>
    public sealed record FieldDifference(string Field, string? ArchiveValue, string? PrimaryValue);

    /// <summary>A difference found at one sequence (or a range of sequences).</summary>
    public sealed record EntryDifference
    {
        /// <summary>Sequence of the entry, or the first sequence of a range.</summary>
        public required long Sequence { get; init; }

        /// <summary>Last sequence of a range of missing entries; <c>null</c> for a single entry.</summary>
        public long? ThroughSequence { get; init; }

        /// <summary>Kind of difference.</summary>
        public required EntryDifferenceKind Kind { get; init; }

        /// <summary>Which copy is the original, for changed entries; <c>null</c> for the other kinds.</summary>
        public OriginalCopy? Original { get; init; }

        /// <summary>Event type of the entry, from the copy that verifies when there is one.</summary>
        public string? EventType { get; init; }

        /// <summary>The fields that differ (changed entries only).</summary>
        public IReadOnlyList<FieldDifference> Fields { get; init; } = [];

        /// <summary><c>false</c> only for <see cref="EntryDifferenceKind.RedactedInOneCopy"/>.</summary>
        public bool IsTampering => Kind != EntryDifferenceKind.RedactedInOneCopy;

        /// <summary>One line summary.</summary>
        public string Summary => Kind switch
        {
            EntryDifferenceKind.Changed => $"Entry {Sequence}{Event} was changed {Where}.",
            EntryDifferenceKind.MissingFromPrimary when ThroughSequence is long last && last != Sequence =>
                $"Entries {Sequence} to {last} are in the archive but missing from the primary store.",
            EntryDifferenceKind.MissingFromPrimary => $"Entry {Sequence}{Event} is in the archive but missing from the primary store.",
            EntryDifferenceKind.MissingFromArchive => $"Entry {Sequence}{Event} is in the primary store but missing from the archive.",
            _ => $"Entry {Sequence}{Event}: the payload was erased in one copy only."
        };

        private string Event => EventType is null ? "" : $" ({EventType})";

        private string Where => Original switch
        {
            OriginalCopy.Archive => "in the primary store (the archive copy verifies, the primary copy does not)",
            OriginalCopy.Primary => "in the archive (the primary copy verifies, the archive copy does not)",
            OriginalCopy.BothVerify => "(both copies verify on their own: whoever changed it holds the signing key)",
            _ => "(neither copy verifies on its own: both were modified)"
        };
    }

    /// <summary>Result of <see cref="LedgerComparer.CompareAsync"/>.</summary>
    public sealed record LedgerComparison
    {
        /// <summary><c>true</c> when no entry was changed, deleted or inserted in either copy.</summary>
        public bool IsIdentical => !Differences.Any(d => d.IsTampering);

        /// <summary>Differences, in sequence order. Capped at the limit given to the comparison.</summary>
        public required IReadOnlyList<EntryDifference> Differences { get; init; }

        /// <summary><c>true</c> when more differences exist than <see cref="Differences"/> holds.</summary>
        public bool DifferencesTruncated { get; init; }

        /// <summary>Number of sequences present in both copies.</summary>
        public long Compared { get; init; }

        /// <summary>Archive entries older than the first entry of the primary store, removed from it by retention.</summary>
        public long RemovedByRetention { get; init; }

        /// <summary>Entries of the primary store newer than the last archived entry: not copied yet.</summary>
        public long NotYetCopied { get; init; }

        /// <summary>Last sequence in the primary store (0 when empty).</summary>
        public long PrimaryLastSequence { get; init; }

        /// <summary>Last sequence in the archive (0 when empty).</summary>
        public long ArchiveLastSequence { get; init; }

        /// <summary>A plain text report, one difference per paragraph.</summary>
        public string Describe()
        {
            var text = new StringBuilder();
            text.Append(IsIdentical
                ? $"The two copies match: {Compared} entries compared."
                : $"{Differences.Count(d => d.IsTampering)}{(DifferencesTruncated ? "+" : "")} difference(s) in {Compared} entries compared.");
            if (RemovedByRetention > 0)
            {
                text.Append($" {RemovedByRetention} older entries are only in the archive (retention).");
            }
            if (NotYetCopied > 0)
            {
                text.Append($" {NotYetCopied} recent entries are not copied to the archive yet.");
            }
            text.AppendLine();
            if (Differences.Any(d => d.Fields.Count > 0))
            {
                text.AppendLine("Changed fields are shown as: archive value -> primary value.");
            }

            foreach (var difference in Differences)
            {
                text.AppendLine().AppendLine(difference.Summary);
                foreach (var field in difference.Fields)
                {
                    text.AppendLine($"  {field.Field}: {field.ArchiveValue ?? "(absent)"} -> {field.PrimaryValue ?? "(absent)"}");
                }
            }
            if (DifferencesTruncated)
            {
                text.AppendLine().AppendLine("More differences exist; only the first ones are listed.");
            }
            return text.ToString();
        }
    }
}
