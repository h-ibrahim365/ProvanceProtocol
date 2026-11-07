using Provance.Core.Comparison;
using Provance.Core.Data;
using Provance.Core.Export;
using Provance.Core.Signing;
using Provance.Core.Verification;

namespace Provance.Core.Tests
{
    /// <summary>
    /// Ledgers written by the Java, Python and .NET libraries (docs/test-vectors/ledger-*) must verify here. If one of
    /// these tests fails, the implementations no longer agree on the format.
    /// </summary>
    public class CrossLanguageTests
    {
        private const string K1 = "provance-test-vector-key-0123456789abcdef";
        private const string K2 = "provance-test-vector-key-2-abcdef0123456789";

        private static HmacLedgerSigner Keys() => new("k2", K2, new Dictionary<string, string> { ["k1"] = K1 });

        private static string Folder(string language) => Path.Combine(AppContext.BaseDirectory, "TestVectors", "ledger-" + language);

        private static async Task<(JsonLinesLedgerFile File, LedgerCheckpoint Checkpoint, string LedgerId)> LoadAsync(string language)
        {
            var file = await LedgerJsonLines.OpenAsync(Path.Combine(Folder(language), "entries.jsonl"));
            var checkpoint = LedgerCheckpoint.FromJson(await File.ReadAllTextAsync(Path.Combine(Folder(language), "checkpoint.json")));
            return (file, checkpoint, file.Entries[0].LedgerId);
        }

        /// <summary>Entries in each fixture, and whether its erasure has a redaction record (written by 0.2 and later).</summary>
        private static (int Entries, bool Recorded) Shape(string language) => language == "dotnet" ? (7, true) : (6, false);

        [Theory]
        [InlineData("java")]
        [InlineData("python")]
        [InlineData("dotnet")]
        public async Task Ledger_written_by_each_library_verifies(string language)
        {
            var (file, checkpoint, ledgerId) = await LoadAsync(language);
            var (entries, recorded) = Shape(language);

            var result = await new LedgerVerifier(file, Keys(), ledgerId).VerifyAsync(new LedgerVerificationOptions
            {
                TrustedCheckpoint = checkpoint,
                AcceptUnrecordedRedactions = !recorded
            });

            Assert.True(result.IsValid, result.Message);
            Assert.Equal(entries, result.EntriesVerified);
            Assert.Equal(1, result.RedactedPayloads);
            Assert.Equal(checkpoint.Hash, result.LastHash);
        }

        [Theory]
        [InlineData("java")]
        [InlineData("python")]
        public async Task An_erasure_made_by_a_0_1_library_has_no_record_and_is_reported_by_default(string language)
        {
            var (file, _, ledgerId) = await LoadAsync(language);

            var result = await new LedgerVerifier(file, Keys(), ledgerId).VerifyAsync();

            Assert.Equal(VerificationFailure.UnrecordedRedaction, result.Failure);
            Assert.Equal(4, result.FailedAtSequence);
        }

        [Theory]
        [InlineData("java")]
        [InlineData("python")]
        [InlineData("dotnet")]
        public async Task Entries_signed_with_a_retired_key_need_that_key(string language)
        {
            var (file, _, ledgerId) = await LoadAsync(language);

            var result = await new LedgerVerifier(file, new HmacLedgerSigner("k2", K2), ledgerId)
                .VerifyAsync(new LedgerVerificationOptions { AcceptUnrecordedRedactions = true });

            Assert.Equal(VerificationFailure.UnknownKey, result.Failure);
            Assert.Equal(1, result.FailedAtSequence);
        }

        [Theory]
        [InlineData("java")]
        [InlineData("python")]
        [InlineData("dotnet")]
        public async Task An_edited_entry_is_detected_and_the_comparison_shows_the_original(string language)
        {
            var (original, _, ledgerId) = await LoadAsync(language);
            var entries = original.Entries.ToList();
            entries[2] = entries[2] with { EventType = "SOMETHING_ELSE" };
            var edited = new JsonLinesLedgerFile(entries);

            var result = await new LedgerVerifier(edited, Keys(), ledgerId).VerifyAsync(new LedgerVerificationOptions { AcceptUnrecordedRedactions = true });
            Assert.Equal(VerificationFailure.HashMismatch, result.Failure);
            Assert.Equal(3, result.FailedAtSequence);

            var comparison = await new LedgerComparer(Keys(), ledgerId).CompareAsync(edited, original);
            var difference = Assert.Single(comparison.Differences);
            Assert.Equal(EntryDifferenceKind.Changed, difference.Kind);
            Assert.Equal(OriginalCopy.Archive, difference.Original);
            var field = Assert.Single(difference.Fields);
            Assert.Equal("eventType", field.Field);
            Assert.Equal(original.Entries[2].EventType, field.ArchiveValue);
            Assert.Equal("SOMETHING_ELSE", field.PrimaryValue);
        }

        [Fact]
        public void Checkpoint_files_use_the_shared_time_format_and_old_ones_still_parse()
        {
            const string written01 = "{\"v\":1,\"ledgerId\":\"x\",\"sequence\":6,\"hash\":\"" + "ab" + "\",\"createdAt\":\"2026-11-07T09:00:06.789+00:00\",\"keyId\":\"k2\",\"signature\":\"cd\"}";

            var checkpoint = LedgerCheckpoint.FromJson(written01);

            Assert.Equal(new DateTimeOffset(2026, 11, 7, 9, 0, 6, 789, TimeSpan.Zero), checkpoint.CreatedAt);
            Assert.Contains("\"createdAt\":\"2026-11-07T09:00:06.789Z\"", checkpoint.ToJson());
            Assert.Equal(checkpoint, LedgerCheckpoint.FromJson(checkpoint.ToJson()));
        }

        [Theory]
        [InlineData("java")]
        [InlineData("python")]
        [InlineData("dotnet")]
        public async Task Export_round_trips_every_ledger(string language)
        {
            var (file, _, _) = await LoadAsync(language);

            int count = Shape(language).Entries;
            using var buffer = new MemoryStream();
            Assert.Equal(count, await LedgerJsonLines.WriteAsync(file, buffer));
            string text = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
            Assert.Equal(count + 1, text.Split('\n').Length); // one line per entry and a final line break
            Assert.DoesNotContain(((char)0x2028).ToString(), text);

            buffer.Position = 0;
            var copy = new List<LedgerEntry>();
            await foreach (var entry in LedgerJsonLines.ReadAsync(buffer))
            {
                copy.Add(entry);
            }
            Assert.Equal(file.Entries, copy);
        }
    }
}
