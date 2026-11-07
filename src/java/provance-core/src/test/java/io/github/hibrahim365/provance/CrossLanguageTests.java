package io.github.hibrahim365.provance;

import io.github.hibrahim365.provance.compare.LedgerComparer;
import io.github.hibrahim365.provance.compare.LedgerComparison;
import io.github.hibrahim365.provance.compare.OriginalCopy;
import io.github.hibrahim365.provance.export.JsonLinesLedgerFile;
import io.github.hibrahim365.provance.export.LedgerJsonLines;
import io.github.hibrahim365.provance.signing.HmacLedgerSigner;
import io.github.hibrahim365.provance.verification.LedgerVerificationOptions;
import io.github.hibrahim365.provance.verification.LedgerVerificationResult;
import io.github.hibrahim365.provance.verification.LedgerVerifier;
import io.github.hibrahim365.provance.verification.VerificationFailure;
import org.junit.jupiter.api.Test;

import java.io.StringWriter;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

/**
 * Ledgers written by the Java, Python and .NET libraries ({@code docs/test-vectors/ledger-*}) must verify here. If one
 * of these tests fails, the implementations no longer agree on the format.
 */
class CrossLanguageTests {

    private static final Path VECTORS = Path.of("../../../docs/test-vectors");
    private static final String[] LANGUAGES = {"java", "python", "dotnet"};

    private static HmacLedgerSigner keys() {
        return new HmacLedgerSigner("k2", "provance-test-vector-key-2-abcdef0123456789",
                Map.of("k1", "provance-test-vector-key-0123456789abcdef"));
    }

    /** The Java fixture was written by 0.1, before redaction records existed. */
    private static boolean recorded(String language) {
        return !language.equals("java");
    }

    @Test
    void ledgerWrittenByEachLibraryVerifies() throws Exception {
        for (String language : LANGUAGES) {
            JsonLinesLedgerFile file = LedgerJsonLines.open(VECTORS.resolve("ledger-" + language + "/entries.jsonl"));
            LedgerCheckpoint checkpoint = LedgerCheckpoint.fromJson(
                    Files.readString(VECTORS.resolve("ledger-" + language + "/checkpoint.json"), StandardCharsets.UTF_8));
            String ledgerId = file.entries().get(0).ledgerId();
            LedgerVerifier verifier = new LedgerVerifier(file, keys(), ledgerId);

            LedgerVerificationOptions options = LedgerVerificationOptions.withCheckpoint(checkpoint);
            LedgerVerificationResult result = verifier.verify(recorded(language) ? options : options.acceptingUnrecordedRedactions());

            assertTrue(result.valid(), language + ": " + result.message());
            assertEquals(recorded(language) ? 7 : 6, result.entriesVerified(), language);
            assertEquals(1, result.redactedPayloads(), language);
            assertEquals(checkpoint.hash(), result.lastHash(), language);
            if (!recorded(language)) {
                assertEquals(VerificationFailure.UNRECORDED_REDACTION, verifier.verify().failure());
            }
        }
    }

    @Test
    void anEditedEntryShowsItsOriginalValueInTheComparison() throws Exception {
        for (String language : LANGUAGES) {
            JsonLinesLedgerFile original = LedgerJsonLines.open(VECTORS.resolve("ledger-" + language + "/entries.jsonl"));
            List<LedgerEntry> entries = new ArrayList<>(original.entries());
            entries.set(2, Edit.eventType(entries.get(2), "SOMETHING_ELSE"));
            String ledgerId = entries.get(0).ledgerId();

            LedgerComparison comparison = new LedgerComparer(keys(), ledgerId).compare(new JsonLinesLedgerFile(entries), original, 100);

            assertEquals(1, comparison.differences().size(), language);
            assertEquals(3, comparison.differences().get(0).sequence());
            assertEquals(OriginalCopy.ARCHIVE, comparison.differences().get(0).original());
            assertEquals("eventType", comparison.differences().get(0).fields().get(0).field());
            assertEquals("SOMETHING_ELSE", comparison.differences().get(0).fields().get(0).primaryValue());
        }
    }

    @Test
    void exportRoundTripsEveryLedger() throws Exception {
        for (String language : LANGUAGES) {
            JsonLinesLedgerFile file = LedgerJsonLines.open(VECTORS.resolve("ledger-" + language + "/entries.jsonl"));
            StringWriter out = new StringWriter();
            assertEquals(file.entries().size(), LedgerJsonLines.write(file, out));
            assertFalse(out.toString().contains(" "), language);
            List<LedgerEntry> copy = new ArrayList<>();
            for (String line : out.toString().split("\n")) {
                copy.add(LedgerJsonLines.parse(line));
            }
            assertEquals(file.entries(), copy, language);
        }
    }
}
