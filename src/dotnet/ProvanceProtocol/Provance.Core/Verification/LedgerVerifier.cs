using Microsoft.Extensions.Options;
using Provance.Core.Cryptography;
using Provance.Core.Data;
using Provance.Core.Exceptions;
using Provance.Core.Options;
using Provance.Core.Retention;
using Provance.Core.Services.Interfaces;
using Provance.Core.Signing;

namespace Provance.Core.Verification
{
    /// <summary>
    /// Verifies a ledger. Usable on its own (for example in an audit tool) with only a store and a signer
    /// that knows the verification keys.
    /// </summary>
    public sealed class LedgerVerifier
    {
        private readonly ILedgerStore _store;
        private readonly ILedgerSigner _signer;
        private readonly string _ledgerId;
        private readonly string _genesisHash;

        /// <summary>Creates a verifier from DI registered services.</summary>
        public LedgerVerifier(ILedgerStore store, ILedgerSigner signer, IOptions<ProvanceOptions> options)
            : this(store, signer, options.Value.LedgerId, options.Value.EffectiveGenesisHash)
        {
        }

        /// <summary>Creates a standalone verifier.</summary>
        public LedgerVerifier(ILedgerStore store, ILedgerSigner signer, string ledgerId, string? genesisHash = null)
        {
            ArgumentNullException.ThrowIfNull(store);
            ArgumentNullException.ThrowIfNull(signer);
            ArgumentException.ThrowIfNullOrWhiteSpace(ledgerId);
            _store = store;
            _signer = signer;
            _ledgerId = ledgerId;
            _genesisHash = string.IsNullOrEmpty(genesisHash) ? LedgerHashing.DeriveGenesisHash(ledgerId) : genesisHash;
        }

        /// <summary>Verifies the ledger, streaming entries from the store.</summary>
        public async Task<LedgerVerificationResult> VerifyAsync(LedgerVerificationOptions? options = null, CancellationToken cancellationToken = default)
        {
            options ??= new LedgerVerificationOptions();
            var result = await VerifyCoreAsync(options, cancellationToken).ConfigureAwait(false);
            if (!result.IsValid && options.ThrowOnFailure)
            {
                throw new LedgerTamperedException(result);
            }
            return result;
        }

        private async Task<LedgerVerificationResult> VerifyCoreAsync(LedgerVerificationOptions options, CancellationToken ct)
        {
            var checkpoint = options.TrustedCheckpoint;
            if (checkpoint is not null)
            {
                var checkpointProblem = await CheckCheckpointAsync(checkpoint, ct).ConfigureAwait(false);
                if (checkpointProblem is not null)
                {
                    return Fail(VerificationFailure.CheckpointMismatch, checkpointProblem, null, 0, 0, 0, null);
                }
            }

            bool fromCheckpoint = checkpoint is not null && options.StartFromCheckpoint;
            long expectedSequence = fromCheckpoint ? checkpoint!.Sequence : 1;
            string? expectedPrevious = fromCheckpoint ? null : _genesisHash; // null: link of the first entry is trusted through the checkpoint
            long verified = 0, redacted = 0, lastSequence = 0;
            string? lastHash = null;
            bool checkpointSeen = false;

            // Retention: the oldest entries may have been removed. The first remaining entry cannot be linked to
            // its predecessor, so it must be vouched for by a retention record later in the chain.
            long firstSequence = 0;
            string? firstHash = null;
            bool startNeedsRecord = false;
            bool startVouched = false;

            // Erased payloads must each be authorized by a redaction record (written after the entry).
            var erased = new Dictionary<long, Guid>();
            var authorized = new HashSet<(long, Guid)>();

            await foreach (var entry in _store.ReadAsync(expectedSequence, ct).ConfigureAwait(false))
            {
                if (verified == 0 && !fromCheckpoint && entry.Sequence > expectedSequence)
                {
                    expectedSequence = entry.Sequence;
                    expectedPrevious = null;
                    startNeedsRecord = true;
                }

                if (entry.Sequence != expectedSequence)
                {
                    string what = entry.Sequence > expectedSequence ? "is missing" : "appears more than once or out of order";
                    return Fail(VerificationFailure.SequenceGap, $"Entry with sequence {expectedSequence} {what} (found {entry.Sequence}).",
                        entry, verified, redacted, lastSequence, lastHash, expectedSequence);
                }

                if (entry.FormatVersion != ProvanceProtocol.FormatVersion)
                {
                    return Fail(VerificationFailure.UnsupportedFormat, $"Format version {entry.FormatVersion} is not supported.",
                        entry, verified, redacted, lastSequence, lastHash);
                }

                if (!string.Equals(entry.LedgerId, _ledgerId, StringComparison.Ordinal))
                {
                    return Fail(VerificationFailure.LedgerIdMismatch, $"Entry belongs to ledger '{entry.LedgerId}', expected '{_ledgerId}'.",
                        entry, verified, redacted, lastSequence, lastHash);
                }

                if (expectedPrevious is not null && !string.Equals(entry.PreviousHash, expectedPrevious, StringComparison.Ordinal))
                {
                    return Fail(VerificationFailure.BrokenLink, $"PreviousHash is {entry.PreviousHash}, expected {expectedPrevious}.",
                        entry, verified, redacted, lastSequence, lastHash);
                }

                if (entry.PayloadJson is null)
                {
                    redacted++;
                    if (!options.AcceptUnrecordedRedactions && !authorized.Contains((entry.Sequence, entry.Id)))
                    {
                        erased[entry.Sequence] = entry.Id;
                    }
                }
                else if (!string.Equals(LedgerHashing.Sha256Hex(entry.PayloadJson), entry.PayloadHash, StringComparison.Ordinal))
                {
                    return Fail(VerificationFailure.PayloadHashMismatch, "Payload does not match PayloadHash.",
                        entry, verified, redacted, lastSequence, lastHash);
                }

                string recomputed;
                try
                {
                    recomputed = LedgerHashing.ComputeEntryHash(entry);
                }
                catch (Exception ex) when (ex is FormatException or ArgumentException)
                {
                    return Fail(VerificationFailure.HashMismatch, $"Entry cannot be canonicalized: {ex.Message}",
                        entry, verified, redacted, lastSequence, lastHash);
                }

                if (!string.Equals(recomputed, entry.Hash, StringComparison.Ordinal))
                {
                    return Fail(VerificationFailure.HashMismatch, $"Recomputed hash {recomputed} differs from stored hash {entry.Hash}.",
                        entry, verified, redacted, lastSequence, lastHash);
                }

                var signatureCheck = await CheckSignatureAsync(entry.KeyId, entry.Hash, entry.Signature, ct).ConfigureAwait(false);
                if (signatureCheck != SignatureCheck.Valid)
                {
                    return signatureCheck == SignatureCheck.UnknownKey
                        ? Fail(VerificationFailure.UnknownKey, $"Entry is signed with unknown key '{entry.KeyId}'.", entry, verified, redacted, lastSequence, lastHash)
                        : Fail(VerificationFailure.InvalidSignature, "Signature is not valid.", entry, verified, redacted, lastSequence, lastHash);
                }

                if (verified == 0)
                {
                    firstSequence = entry.Sequence;
                    firstHash = entry.Hash;
                }

                if (string.Equals(entry.EventType, LedgerRedactionRecord.EventType, StringComparison.Ordinal)
                    && LedgerRedactionRecord.TryParse(entry.PayloadJson, out long erasedSequence, out Guid erasedId))
                {
                    authorized.Add((erasedSequence, erasedId));
                    if (erased.TryGetValue(erasedSequence, out Guid pending) && pending == erasedId)
                    {
                        erased.Remove(erasedSequence);
                    }
                }

                if (startNeedsRecord && !startVouched
                    && string.Equals(entry.EventType, LedgerRetentionRecord.EventType, StringComparison.Ordinal)
                    && LedgerRetentionRecord.TryParse(entry.PayloadJson, out long keptFrom, out string keptHash)
                    && keptFrom == firstSequence
                    && string.Equals(keptHash, firstHash, StringComparison.Ordinal))
                {
                    startVouched = true;
                }

                if (checkpoint is not null && entry.Sequence == checkpoint.Sequence)
                {
                    if (!string.Equals(entry.Hash, checkpoint.Hash, StringComparison.Ordinal))
                    {
                        return Fail(VerificationFailure.CheckpointMismatch,
                            $"Entry {entry.Sequence} has hash {entry.Hash} but the trusted checkpoint says {checkpoint.Hash}: history was rewritten.",
                            entry, verified, redacted, lastSequence, lastHash);
                    }
                    checkpointSeen = true;
                }

                verified++;
                lastSequence = entry.Sequence;
                lastHash = entry.Hash;
                expectedPrevious = entry.Hash;
                expectedSequence++;
            }

            if (startNeedsRecord && !startVouched)
            {
                return Fail(VerificationFailure.SequenceGap,
                    $"Entries 1 to {firstSequence - 1} are missing, and no retention record in the ledger accounts for their removal.",
                    null, verified, redacted, lastSequence, lastHash, 1, firstSequence);
            }

            if (erased.Count > 0)
            {
                var (sequence, id) = erased.MinBy(e => e.Key);
                return new LedgerVerificationResult
                {
                    IsValid = false,
                    Failure = VerificationFailure.UnrecordedRedaction,
                    Message = $"Entry {sequence} ({id}): the payload was removed without a redaction record, so it was erased directly in the database, not through RedactPayloadAsync."
                        + (erased.Count > 1 ? $" {erased.Count - 1} other entries are in the same case." : ""),
                    FailedAtSequence = sequence,
                    FailedEntryId = id,
                    EntriesVerified = verified,
                    RedactedPayloads = redacted,
                    LastSequence = lastSequence,
                    LastHash = lastHash,
                    FirstSequence = firstSequence
                };
            }

            if (checkpoint is not null && !checkpointSeen && verified > 0 && checkpoint.Sequence < firstSequence)
            {
                return Fail(VerificationFailure.CheckpointMismatch,
                    $"The trusted checkpoint references sequence {checkpoint.Sequence}, which was removed by retention (the ledger now starts at {firstSequence}). Use a newer checkpoint.",
                    null, verified, redacted, lastSequence, lastHash, checkpoint.Sequence, firstSequence);
            }

            if (checkpoint is not null && !checkpointSeen)
            {
                return Fail(VerificationFailure.Truncated,
                    $"The ledger ends at sequence {lastSequence} but the trusted checkpoint references sequence {checkpoint.Sequence}.",
                    null, verified, redacted, lastSequence, lastHash, lastSequence + 1, firstSequence);
            }

            string message = verified == 0
                ? "Ledger is empty."
                : firstSequence > 1
                    ? $"{verified} entries verified, from sequence {firstSequence} to {lastSequence}."
                    : $"{verified} entries verified up to sequence {lastSequence}.";
            if (startNeedsRecord)
            {
                message += $" Entries before {firstSequence} were removed by retention.";
            }
            if (checkpoint is null && verified > 0)
            {
                message += " Without a trusted checkpoint, removal of the last entries cannot be detected.";
            }

            return new LedgerVerificationResult
            {
                IsValid = true,
                Failure = VerificationFailure.None,
                Message = message,
                EntriesVerified = verified,
                RedactedPayloads = redacted,
                LastSequence = lastSequence,
                LastHash = lastHash,
                FirstSequence = firstSequence
            };
        }

        /// <summary>Checks the signature and ledger id of a checkpoint. Returns an error message, or <c>null</c> when valid.</summary>
        public async Task<string?> CheckCheckpointAsync(LedgerCheckpoint checkpoint, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(checkpoint);
            if (checkpoint.FormatVersion != ProvanceProtocol.FormatVersion)
                return $"Checkpoint format version {checkpoint.FormatVersion} is not supported.";
            if (!string.Equals(checkpoint.LedgerId, _ledgerId, StringComparison.Ordinal))
                return $"Checkpoint belongs to ledger '{checkpoint.LedgerId}', expected '{_ledgerId}'.";
            if (checkpoint.Sequence < 1 || !LedgerHashing.IsLowerHex(checkpoint.Hash, 64))
                return "Checkpoint sequence or hash is malformed.";

            string digest = LedgerHashing.ComputeCheckpointHash(checkpoint);
            var check = await CheckSignatureAsync(checkpoint.KeyId, digest, checkpoint.Signature, cancellationToken).ConfigureAwait(false);
            return check switch
            {
                SignatureCheck.Valid => null,
                SignatureCheck.UnknownKey => $"Checkpoint is signed with unknown key '{checkpoint.KeyId}'.",
                _ => "Checkpoint signature is not valid."
            };
        }

        private async ValueTask<SignatureCheck> CheckSignatureAsync(string keyId, string hexDigest, string hexSignature, CancellationToken ct)
        {
            if (!LedgerHashing.IsLowerHex(hexSignature))
            {
                return SignatureCheck.Invalid;
            }
            byte[] digest = LedgerHashing.HashToBytes(hexDigest);
            byte[] signature = Convert.FromHexString(hexSignature);
            return await _signer.VerifyAsync(keyId, digest, signature, ct).ConfigureAwait(false);
        }

        private static LedgerVerificationResult Fail(
            VerificationFailure failure, string message, LedgerEntry? entry,
            long verified, long redacted, long lastSequence, string? lastHash, long? failedAtSequence = null, long firstSequence = 0)
            => new()
            {
                IsValid = false,
                Failure = failure,
                Message = entry is null ? message : $"Entry {entry.Sequence} ({entry.Id}): {message}",
                FailedAtSequence = failedAtSequence ?? entry?.Sequence,
                FailedEntryId = entry?.Id,
                EntriesVerified = verified,
                RedactedPayloads = redacted,
                LastSequence = lastSequence,
                LastHash = lastHash,
                FirstSequence = firstSequence
            };
    }
}
