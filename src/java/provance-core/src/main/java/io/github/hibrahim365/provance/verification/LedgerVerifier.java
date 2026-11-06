package io.github.hibrahim365.provance.verification;

import io.github.hibrahim365.provance.LedgerCheckpoint;
import io.github.hibrahim365.provance.LedgerEntry;
import io.github.hibrahim365.provance.LedgerTamperedException;
import io.github.hibrahim365.provance.ProvanceProtocol;
import io.github.hibrahim365.provance.crypto.LedgerHashing;
import io.github.hibrahim365.provance.signing.LedgerSigner;
import io.github.hibrahim365.provance.signing.SignatureCheck;
import io.github.hibrahim365.provance.store.LedgerStore;

import java.util.Iterator;
import java.util.Objects;
import java.util.UUID;
import java.util.stream.Stream;

/**
 * Verifies a ledger. Usable on its own (for example in an audit tool) with only a store and a signer that knows
 * the verification keys. The checks and their order follow section 5 of {@code docs/SPEC.md}.
 */
public final class LedgerVerifier {

    private final LedgerStore store;
    private final LedgerSigner signer;
    private final String ledgerId;
    private final String genesisHash;

    /** Creates a verifier using the default genesis hash of {@code ledgerId}. */
    public LedgerVerifier(LedgerStore store, LedgerSigner signer, String ledgerId) {
        this(store, signer, ledgerId, null);
    }

    /** Creates a verifier with an explicit genesis hash ({@code null}: derived from {@code ledgerId}). */
    public LedgerVerifier(LedgerStore store, LedgerSigner signer, String ledgerId, String genesisHash) {
        this.store = Objects.requireNonNull(store, "store");
        this.signer = Objects.requireNonNull(signer, "signer");
        if (ledgerId == null || ledgerId.isBlank()) {
            throw new IllegalArgumentException("ledgerId is required");
        }
        this.ledgerId = ledgerId;
        this.genesisHash = genesisHash == null || genesisHash.isEmpty() ? LedgerHashing.deriveGenesisHash(ledgerId) : genesisHash;
    }

    /** Verifies the whole ledger without checkpoint. */
    public LedgerVerificationResult verify() {
        return verify(LedgerVerificationOptions.defaults());
    }

    /**
     * Verifies the ledger, streaming entries from the store.
     *
     * @throws LedgerTamperedException verification failed and {@code throwOnFailure} is set
     */
    public LedgerVerificationResult verify(LedgerVerificationOptions options) {
        LedgerVerificationOptions o = options == null ? LedgerVerificationOptions.defaults() : options;
        LedgerVerificationResult result = verifyCore(o);
        if (!result.valid() && o.throwOnFailure()) {
            throw new LedgerTamperedException(result);
        }
        return result;
    }

    private LedgerVerificationResult verifyCore(LedgerVerificationOptions options) {
        LedgerCheckpoint checkpoint = options.trustedCheckpoint();
        if (checkpoint != null) {
            String problem = checkCheckpoint(checkpoint);
            if (problem != null) {
                return fail(VerificationFailure.CHECKPOINT_MISMATCH, problem, null, 0, 0, 0, null, null);
            }
        }

        boolean fromCheckpoint = checkpoint != null && options.startFromCheckpoint();
        long expectedSequence = fromCheckpoint ? checkpoint.sequence() : 1;
        String expectedPrevious = fromCheckpoint ? null : genesisHash; // null: link of the first entry is trusted through the checkpoint
        long verified = 0;
        long redacted = 0;
        long lastSequence = 0;
        String lastHash = null;
        boolean checkpointSeen = false;

        try (Stream<LedgerEntry> entries = store.read(expectedSequence)) {
            Iterator<LedgerEntry> it = entries.iterator();
            while (it.hasNext()) {
                LedgerEntry entry = it.next();

                if (entry.sequence() != expectedSequence) {
                    String what = entry.sequence() > expectedSequence ? "is missing" : "appears more than once or out of order";
                    return fail(VerificationFailure.SEQUENCE_GAP,
                            "Entry with sequence " + expectedSequence + " " + what + " (found " + entry.sequence() + ").",
                            entry, verified, redacted, lastSequence, lastHash, expectedSequence);
                }

                if (entry.formatVersion() != ProvanceProtocol.FORMAT_VERSION) {
                    return fail(VerificationFailure.UNSUPPORTED_FORMAT, "Format version " + entry.formatVersion() + " is not supported.",
                            entry, verified, redacted, lastSequence, lastHash, null);
                }

                if (!ledgerId.equals(entry.ledgerId())) {
                    return fail(VerificationFailure.LEDGER_ID_MISMATCH,
                            "Entry belongs to ledger '" + entry.ledgerId() + "', expected '" + ledgerId + "'.",
                            entry, verified, redacted, lastSequence, lastHash, null);
                }

                if (expectedPrevious != null && !expectedPrevious.equals(entry.previousHash())) {
                    return fail(VerificationFailure.BROKEN_LINK,
                            "PreviousHash is " + entry.previousHash() + ", expected " + expectedPrevious + ".",
                            entry, verified, redacted, lastSequence, lastHash, null);
                }

                if (entry.payloadJson() == null) {
                    redacted++;
                } else if (!LedgerHashing.sha256Hex(entry.payloadJson()).equals(entry.payloadHash())) {
                    return fail(VerificationFailure.PAYLOAD_HASH_MISMATCH, "Payload does not match PayloadHash.",
                            entry, verified, redacted, lastSequence, lastHash, null);
                }

                String recomputed;
                try {
                    recomputed = LedgerHashing.computeEntryHash(entry);
                } catch (IllegalArgumentException e) {
                    return fail(VerificationFailure.HASH_MISMATCH, "Entry cannot be canonicalized: " + e.getMessage(),
                            entry, verified, redacted, lastSequence, lastHash, null);
                }

                if (!recomputed.equals(entry.hash())) {
                    return fail(VerificationFailure.HASH_MISMATCH,
                            "Recomputed hash " + recomputed + " differs from stored hash " + entry.hash() + ".",
                            entry, verified, redacted, lastSequence, lastHash, null);
                }

                SignatureCheck signatureCheck = checkSignature(entry.keyId(), entry.hash(), entry.signature());
                if (signatureCheck != SignatureCheck.VALID) {
                    return signatureCheck == SignatureCheck.UNKNOWN_KEY
                            ? fail(VerificationFailure.UNKNOWN_KEY, "Entry is signed with unknown key '" + entry.keyId() + "'.",
                                    entry, verified, redacted, lastSequence, lastHash, null)
                            : fail(VerificationFailure.INVALID_SIGNATURE, "Signature is not valid.",
                                    entry, verified, redacted, lastSequence, lastHash, null);
                }

                if (checkpoint != null && entry.sequence() == checkpoint.sequence()) {
                    if (!entry.hash().equals(checkpoint.hash())) {
                        return fail(VerificationFailure.CHECKPOINT_MISMATCH,
                                "Entry " + entry.sequence() + " has hash " + entry.hash() + " but the trusted checkpoint says "
                                        + checkpoint.hash() + ": history was rewritten.",
                                entry, verified, redacted, lastSequence, lastHash, null);
                    }
                    checkpointSeen = true;
                }

                verified++;
                lastSequence = entry.sequence();
                lastHash = entry.hash();
                expectedPrevious = entry.hash();
                expectedSequence++;
            }
        }

        if (checkpoint != null && !checkpointSeen) {
            return fail(VerificationFailure.TRUNCATED,
                    "The ledger ends at sequence " + lastSequence + " but the trusted checkpoint references sequence "
                            + checkpoint.sequence() + ".",
                    null, verified, redacted, lastSequence, lastHash, lastSequence + 1);
        }

        String message = verified == 0 ? "Ledger is empty." : verified + " entries verified up to sequence " + lastSequence + ".";
        if (checkpoint == null && verified > 0) {
            message += " Without a trusted checkpoint, removal of the last entries cannot be detected.";
        }
        return new LedgerVerificationResult(true, VerificationFailure.NONE, message, null, null, verified, redacted, lastSequence, lastHash);
    }

    /** Checks the signature and ledger id of a checkpoint. Returns an error message, or {@code null} when valid. */
    public String checkCheckpoint(LedgerCheckpoint checkpoint) {
        Objects.requireNonNull(checkpoint, "checkpoint");
        if (checkpoint.formatVersion() != ProvanceProtocol.FORMAT_VERSION) {
            return "Checkpoint format version " + checkpoint.formatVersion() + " is not supported.";
        }
        if (!ledgerId.equals(checkpoint.ledgerId())) {
            return "Checkpoint belongs to ledger '" + checkpoint.ledgerId() + "', expected '" + ledgerId + "'.";
        }
        if (checkpoint.sequence() < 1 || !LedgerHashing.isLowerHex(checkpoint.hash(), 64)) {
            return "Checkpoint sequence or hash is malformed.";
        }
        String digest = LedgerHashing.computeCheckpointHash(checkpoint);
        switch (checkSignature(checkpoint.keyId(), digest, checkpoint.signature())) {
            case VALID:
                return null;
            case UNKNOWN_KEY:
                return "Checkpoint is signed with unknown key '" + checkpoint.keyId() + "'.";
            default:
                return "Checkpoint signature is not valid.";
        }
    }

    private SignatureCheck checkSignature(String keyId, String hexDigest, String hexSignature) {
        if (!LedgerHashing.isLowerHex(hexSignature) || !LedgerHashing.isLowerHex(hexDigest, 64)) {
            return SignatureCheck.INVALID;
        }
        return signer.verify(keyId, LedgerHashing.hashToBytes(hexDigest), LedgerHashing.fromHex(hexSignature));
    }

    private static LedgerVerificationResult fail(VerificationFailure failure, String message, LedgerEntry entry,
                                                 long verified, long redacted, long lastSequence, String lastHash,
                                                 Long failedAtSequence) {
        UUID id = entry == null ? null : entry.id();
        Long at = failedAtSequence != null ? failedAtSequence : (entry == null ? null : entry.sequence());
        String text = entry == null ? message : "Entry " + entry.sequence() + " (" + entry.id() + "): " + message;
        return new LedgerVerificationResult(false, failure, text, at, id, verified, redacted, lastSequence, lastHash);
    }
}
