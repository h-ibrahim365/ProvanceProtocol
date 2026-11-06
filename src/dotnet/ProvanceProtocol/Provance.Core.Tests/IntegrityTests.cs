using Provance.Core.Cryptography;
using Provance.Core.Data;
using Provance.Core.Exceptions;
using Provance.Core.Services;
using Provance.Core.Signing;
using Provance.Core.Tests.TestSupport;
using Provance.Core.Verification;

namespace Provance.Core.Tests
{
    /// <summary>What an attacker with write access to the database can and cannot do unnoticed.</summary>
    public class IntegrityTests
    {
        private static async Task<(LedgerHarness Harness, InMemoryLedgerStore Store)> StartWithEntriesAsync(int count)
        {
            var store = new InMemoryLedgerStore();
            var harness = await LedgerHarness.StartAsync(store);
            for (int i = 1; i <= count; i++)
            {
                await harness.Ledger.AddEntryAsync("EVENT", new AuditedPayload { ActorId = $"user-{i}", Description = $"#{i}" });
            }
            return (harness, store);
        }

        [Fact]
        public async Task Valid_chain_verifies()
        {
            var (harness, _) = await StartWithEntriesAsync(10);
            await using var _h = harness;

            var result = await harness.Ledger.VerifyAsync();

            Assert.True(result.IsValid, result.Message);
            Assert.Equal(10, result.EntriesVerified);
            Assert.Equal(10, result.LastSequence);
        }

        [Fact]
        public async Task Empty_ledger_is_valid()
        {
            await using var harness = await LedgerHarness.StartAsync();
            var result = await harness.Ledger.VerifyAsync();
            Assert.True(result.IsValid);
            Assert.Equal(0, result.EntriesVerified);
        }

        [Fact]
        public async Task First_entry_links_to_the_derived_genesis_hash()
        {
            var (harness, _) = await StartWithEntriesAsync(1);
            await using var _h = harness;
            var head = await harness.Ledger.GetHeadAsync();
            Assert.Equal(LedgerHashing.DeriveGenesisHash(LedgerHarness.LedgerId), head!.PreviousHash);
            Assert.Equal(1, head.Sequence);
        }

        [Fact]
        public async Task Fields_of_derived_payload_types_are_signed()
        {
            // Regression: 0.0.3 serialized the declared type AuditedPayload and silently dropped derived fields.
            var store = new InMemoryLedgerStore();
            await using var harness = await LedgerHarness.StartAsync(store);
            AuditedPayload payload = new HttpLikePayload { ActorId = "u1", Path = "/api/pay", ClientIp = "10.0.0.1" };
            var entry = await harness.Ledger.AddEntryAsync("HTTP", payload);

            Assert.Contains("\"path\":\"/api/pay\"", entry.PayloadJson);
            Assert.Contains("\"clientIp\":\"10.0.0.1\"", entry.PayloadJson);

            store.Tamper(entry.Sequence, e => e with { PayloadJson = e.PayloadJson!.Replace("/api/pay", "/api/refund") });
            var result = await harness.Ledger.VerifyAsync();

            Assert.False(result.IsValid);
            Assert.Equal(VerificationFailure.PayloadHashMismatch, result.Failure);
            Assert.Equal(entry.Sequence, result.FailedAtSequence);
        }

        [Fact]
        public async Task Modified_event_type_is_detected()
        {
            var (harness, store) = await StartWithEntriesAsync(5);
            await using var _h = harness;
            store.Tamper(3, e => e with { EventType = "SOMETHING_ELSE" });

            var result = await harness.Ledger.VerifyAsync();

            Assert.Equal(VerificationFailure.HashMismatch, result.Failure);
            Assert.Equal(3, result.FailedAtSequence);
            Assert.Equal(2, result.EntriesVerified);
        }

        [Fact]
        public async Task Payload_rewritten_with_a_matching_payload_hash_is_detected()
        {
            var (harness, store) = await StartWithEntriesAsync(3);
            await using var _h = harness;
            const string forged = "{\"actorId\":\"admin\"}";
            store.Tamper(2, e => e with { PayloadJson = forged, PayloadHash = LedgerHashing.Sha256Hex(forged) });

            var result = await harness.Ledger.VerifyAsync();

            Assert.Equal(VerificationFailure.HashMismatch, result.Failure);
        }

        [Fact]
        public async Task Recomputing_hashes_without_the_key_is_detected_by_the_signature()
        {
            var (harness, store) = await StartWithEntriesAsync(1);
            await using var _h = harness;
            store.Tamper(1, e =>
            {
                var forged = e with { EventType = "FORGED" };
                return forged with { Hash = LedgerHashing.ComputeEntryHash(forged) };
            });

            var result = await harness.Ledger.VerifyAsync();

            Assert.Equal(VerificationFailure.InvalidSignature, result.Failure);
        }

        [Fact]
        public async Task Deleted_entry_in_the_middle_is_detected()
        {
            var (harness, store) = await StartWithEntriesAsync(5);
            await using var _h = harness;
            store.Delete(e => e.Sequence == 3);

            var result = await harness.Ledger.VerifyAsync();

            Assert.Equal(VerificationFailure.SequenceGap, result.Failure);
            Assert.Equal(3, result.FailedAtSequence);
        }

        [Fact]
        public async Task Renumbered_entry_is_detected()
        {
            var (harness, store) = await StartWithEntriesAsync(4);
            await using var _h = harness;
            store.Delete(e => e.Sequence == 4);
            store.Tamper(3, e => e with { Sequence = 4 });

            var result = await harness.Ledger.VerifyAsync();

            Assert.False(result.IsValid);
            Assert.Equal(VerificationFailure.SequenceGap, result.Failure);
        }

        [Fact]
        public async Task Truncation_is_only_detected_with_a_checkpoint()
        {
            var (harness, store) = await StartWithEntriesAsync(5);
            await using var _h = harness;
            var checkpoint = await harness.Ledger.CreateCheckpointAsync();
            store.Delete(e => e.Sequence >= 4);

            var withoutCheckpoint = await harness.Ledger.VerifyAsync();
            var withCheckpoint = await harness.Ledger.VerifyAsync(new LedgerVerificationOptions { TrustedCheckpoint = checkpoint });

            Assert.True(withoutCheckpoint.IsValid); // documented limit
            Assert.Equal(3, withoutCheckpoint.LastSequence);
            Assert.Equal(VerificationFailure.Truncated, withCheckpoint.Failure);
        }

        [Fact]
        public async Task Full_rewrite_by_someone_holding_the_key_is_detected_with_a_checkpoint()
        {
            var (harness, _) = await StartWithEntriesAsync(3);
            LedgerCheckpoint checkpoint;
            await using (harness)
            {
                checkpoint = (await harness.Ledger.CreateCheckpointAsync())!;
            }

            // The insider rebuilds a different history with the same key in a fresh store.
            var rewritten = new InMemoryLedgerStore();
            await using var insider = await LedgerHarness.StartAsync(rewritten);
            for (int i = 0; i < 3; i++)
            {
                await insider.Ledger.AddEntryAsync("EVENT", new AuditedPayload { Description = "rewritten" });
            }

            Assert.True((await insider.Ledger.VerifyAsync()).IsValid);
            var result = await insider.Ledger.VerifyAsync(new LedgerVerificationOptions { TrustedCheckpoint = checkpoint });
            Assert.Equal(VerificationFailure.CheckpointMismatch, result.Failure);
        }

        [Fact]
        public async Task Forged_checkpoint_is_rejected()
        {
            var (harness, _) = await StartWithEntriesAsync(2);
            await using var _h = harness;
            var checkpoint = (await harness.Ledger.CreateCheckpointAsync())! with { Sequence = 1 };

            var result = await harness.Ledger.VerifyAsync(new LedgerVerificationOptions { TrustedCheckpoint = checkpoint });

            Assert.Equal(VerificationFailure.CheckpointMismatch, result.Failure);
            Assert.Contains("signature", result.Message);
        }

        [Fact]
        public async Task Incremental_verification_starts_at_the_checkpoint()
        {
            var (harness, store) = await StartWithEntriesAsync(3);
            await using var _h = harness;
            var checkpoint = await harness.Ledger.CreateCheckpointAsync();
            for (int i = 0; i < 2; i++)
            {
                await harness.Ledger.AddEntryAsync("EVENT", new AuditedPayload());
            }

            var result = await harness.Ledger.VerifyAsync(new LedgerVerificationOptions { TrustedCheckpoint = checkpoint, StartFromCheckpoint = true });

            Assert.True(result.IsValid, result.Message);
            Assert.Equal(3, result.EntriesVerified); // checkpoint entry + 2 new ones
            Assert.Equal(5, result.LastSequence);

            store.Tamper(5, e => e with { EventType = "X" });
            var tampered = await harness.Ledger.VerifyAsync(new LedgerVerificationOptions { TrustedCheckpoint = checkpoint, StartFromCheckpoint = true });
            Assert.Equal(VerificationFailure.HashMismatch, tampered.Failure);
        }

        [Fact]
        public async Task Entry_from_another_ledger_is_detected()
        {
            var store = new InMemoryLedgerStore();
            await using (var other = await LedgerHarness.StartAsync(store, o => o.LedgerId = "another-ledger"))
            {
                await other.Ledger.AddEntryAsync("EVENT", new AuditedPayload());
            }

            var verifier = new LedgerVerifier(store, new HmacLedgerSigner("k1", LedgerHarness.Secret), LedgerHarness.LedgerId);
            var result = await verifier.VerifyAsync();

            Assert.Equal(VerificationFailure.LedgerIdMismatch, result.Failure);
        }

        [Fact]
        public async Task Redacted_payload_keeps_the_chain_valid()
        {
            var (harness, _) = await StartWithEntriesAsync(3);
            await using var _h = harness;
            var last = await harness.Ledger.GetHeadAsync();
            Assert.True(await harness.Ledger.RedactPayloadAsync(last!.Id));

            var redacted = await harness.Ledger.GetEntryAsync(last.Id);
            var result = await harness.Ledger.VerifyAsync();

            Assert.True(redacted!.IsRedacted);
            Assert.Null(redacted.GetPayload<AuditedPayload>());
            Assert.True(result.IsValid, result.Message);
            Assert.Equal(1, result.RedactedPayloads);
        }

        [Fact]
        public async Task Rotated_keys_verify_with_retired_keys_only()
        {
            const string newSecret = "second-unit-test-secret-key-abcdef0123456789";
            var store = new InMemoryLedgerStore();
            await using (var before = await LedgerHarness.StartAsync(store))
            {
                await before.Ledger.AddEntryAsync("EVENT", new AuditedPayload { Description = "old key" });
            }

            await using var after = await LedgerHarness.StartAsync(store, o =>
            {
                o.SigningKeyId = "k2";
                o.SecretKey = newSecret;
                o.RetiredKeys["k1"] = LedgerHarness.Secret;
            });
            var entry = await after.Ledger.AddEntryAsync("EVENT", new AuditedPayload { Description = "new key" });

            Assert.Equal("k2", entry.KeyId);
            Assert.True((await after.Ledger.VerifyAsync()).IsValid);

            var withoutRetiredKey = new LedgerVerifier(store, new HmacLedgerSigner("k2", newSecret), LedgerHarness.LedgerId);
            var result = await withoutRetiredKey.VerifyAsync();
            Assert.Equal(VerificationFailure.UnknownKey, result.Failure);
            Assert.Equal(1, result.FailedAtSequence);
        }

        [Fact]
        public async Task Throw_on_failure_raises_LedgerTamperedException()
        {
            var (harness, store) = await StartWithEntriesAsync(2);
            await using var _h = harness;
            store.Tamper(1, e => e with { EventType = "X" });

            var ex = await Assert.ThrowsAsync<LedgerTamperedException>(
                () => harness.Ledger.VerifyAsync(new LedgerVerificationOptions { ThrowOnFailure = true }));
            Assert.Equal(VerificationFailure.HashMismatch, ex.Result.Failure);
        }

        [Fact]
        public async Task Entries_are_immutable_records_with_typed_payload_access()
        {
            var (harness, _) = await StartWithEntriesAsync(1);
            await using var _h = harness;
            var head = (await harness.Ledger.GetHeadAsync())!;

            var payload = head.GetPayload<AuditedPayload>();
            Assert.Equal("user-1", payload!.ActorId);
            Assert.Equal(head.Timestamp, LedgerHashing.NormalizeTimestamp(head.Timestamp));
            Assert.Equal(TimeSpan.Zero, head.Timestamp.Offset);
        }

        private sealed record HttpLikePayload : AuditedPayload
        {
            public string? Path { get; init; }
            public string? ClientIp { get; init; }
        }
    }
}
