package io.github.hibrahim365.provance.compare;

import java.util.List;

/** Result of {@link LedgerComparer#compare}. */
public record LedgerComparison(List<EntryDifference> differences, boolean differencesTruncated, long compared,
                               long removedByRetention, long notYetCopied, long primaryLastSequence,
                               long archiveLastSequence) {

    public LedgerComparison {
        differences = List.copyOf(differences);
    }

    /** {@code true} when no entry was changed, deleted or inserted in either copy. */
    public boolean identical() {
        return differences.stream().noneMatch(EntryDifference::isTampering);
    }

    /** A plain text report, one difference per paragraph. */
    public String describe() {
        StringBuilder sb = new StringBuilder();
        long tampering = differences.stream().filter(EntryDifference::isTampering).count();
        sb.append(identical() ? "The two copies match: " + compared + " entries compared."
                : tampering + (differencesTruncated ? "+" : "") + " difference(s) in " + compared + " entries compared.");
        if (removedByRetention > 0) {
            sb.append(' ').append(removedByRetention).append(" older entries are only in the archive (retention).");
        }
        if (notYetCopied > 0) {
            sb.append(' ').append(notYetCopied).append(" recent entries are not copied to the archive yet.");
        }
        sb.append('\n');
        if (differences.stream().anyMatch(d -> !d.fields().isEmpty())) {
            sb.append("Changed fields are shown as: archive value -> primary value.\n");
        }
        for (EntryDifference d : differences) {
            sb.append('\n').append(d.summary()).append('\n');
            for (FieldDifference f : d.fields()) {
                sb.append("  ").append(f.field()).append(": ").append(show(f.archiveValue())).append(" -> ")
                        .append(show(f.primaryValue())).append('\n');
            }
        }
        if (differencesTruncated) {
            sb.append("\nMore differences exist; only the first ones are listed.\n");
        }
        return sb.toString();
    }

    private static String show(String value) {
        return value == null ? "(absent)" : value;
    }
}
