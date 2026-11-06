using System.Text.Json;
using Provance.Core.Canonicalization;
using Provance.Core.Cryptography;
using Provance.Core.Data;
using Provance.Core.Signing;

namespace Provance.Core.Tests
{
    /// <summary>
    /// Checks the published test vectors (docs/test-vectors/v1.json). If this test fails after a code change,
    /// the change broke the ledger format: existing ledgers would no longer verify.
    /// </summary>
    public class TestVectorTests
    {
        private static JsonDocument Load()
            => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestVectors", "v1.json")));

        [Fact]
        public void Genesis_hash_matches()
        {
            using var doc = Load();
            var genesis = doc.RootElement.GetProperty("genesis");
            Assert.Equal(genesis.GetProperty("hash").GetString(), LedgerHashing.DeriveGenesisHash(genesis.GetProperty("ledgerId").GetString()!));
        }

        [Fact]
        public async Task Entries_match()
        {
            using var doc = Load();
            var hmac = doc.RootElement.GetProperty("hmac");
            var signer = new HmacLedgerSigner(hmac.GetProperty("keyId").GetString()!, hmac.GetProperty("secretUtf8").GetString());
            string previous = doc.RootElement.GetProperty("genesis").GetProperty("hash").GetString()!;
            int count = 0;

            foreach (var vector in doc.RootElement.GetProperty("entries").EnumerateArray())
            {
                var input = vector.GetProperty("input");
                var expected = vector.GetProperty("expected");

                string canonicalPayload = JsonCanonicalizer.Canonicalize(input.GetProperty("payload").GetRawText());
                Assert.Equal(expected.GetProperty("canonicalPayload").GetString(), canonicalPayload);

                string payloadHash = LedgerHashing.Sha256Hex(canonicalPayload);
                Assert.Equal(expected.GetProperty("payloadHash").GetString(), payloadHash);
                Assert.Equal(previous, input.GetProperty("previousHash").GetString());

                var entry = new LedgerEntry
                {
                    FormatVersion = doc.RootElement.GetProperty("formatVersion").GetInt32(),
                    LedgerId = input.GetProperty("ledgerId").GetString()!,
                    Sequence = input.GetProperty("sequence").GetInt64(),
                    Id = input.GetProperty("id").GetGuid(),
                    Timestamp = DateTimeOffset.Parse(input.GetProperty("timestamp").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                    EventType = input.GetProperty("eventType").GetString()!,
                    PayloadJson = canonicalPayload,
                    PayloadHash = payloadHash,
                    PreviousHash = input.GetProperty("previousHash").GetString()!,
                    KeyId = input.GetProperty("keyId").GetString()!,
                    Hash = string.Empty,
                    Signature = string.Empty
                };

                Assert.Equal(expected.GetProperty("canonicalEnvelope").GetString(), LedgerHashing.CanonicalEnvelope(entry));
                string hash = LedgerHashing.ComputeEntryHash(entry);
                Assert.Equal(expected.GetProperty("hash").GetString(), hash);

                byte[] signature = await signer.SignAsync(LedgerHashing.HashToBytes(hash));
                Assert.Equal(expected.GetProperty("signature").GetString(), Convert.ToHexString(signature).ToLowerInvariant());

                previous = hash;
                count++;
            }

            Assert.Equal(3, count);
        }

        [Fact]
        public async Task Checkpoint_matches()
        {
            using var doc = Load();
            var hmac = doc.RootElement.GetProperty("hmac");
            var signer = new HmacLedgerSigner(hmac.GetProperty("keyId").GetString()!, hmac.GetProperty("secretUtf8").GetString());
            var input = doc.RootElement.GetProperty("checkpoint").GetProperty("input");
            var expected = doc.RootElement.GetProperty("checkpoint").GetProperty("expected");

            var checkpoint = new LedgerCheckpoint
            {
                FormatVersion = 1,
                LedgerId = input.GetProperty("ledgerId").GetString()!,
                Sequence = input.GetProperty("sequence").GetInt64(),
                Hash = input.GetProperty("hash").GetString()!,
                CreatedAt = DateTimeOffset.Parse(input.GetProperty("createdAt").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                KeyId = input.GetProperty("keyId").GetString()!,
                Signature = string.Empty
            };

            Assert.Equal(expected.GetProperty("canonicalBody").GetString(), LedgerHashing.CanonicalCheckpoint(checkpoint));
            string digest = LedgerHashing.ComputeCheckpointHash(checkpoint);
            Assert.Equal(expected.GetProperty("digest").GetString(), digest);
            byte[] signature = await signer.SignAsync(LedgerHashing.HashToBytes(digest));
            Assert.Equal(expected.GetProperty("signature").GetString(), Convert.ToHexString(signature).ToLowerInvariant());
        }
    }
}
