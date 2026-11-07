"""Ledger verification (docs/SPEC.md, section 5). Same checks, in the same order, as the .NET and Java verifiers."""

from __future__ import annotations

import enum
from dataclasses import asdict, dataclass
from typing import Any, Iterable, Protocol

from .format import (
    FORMAT_VERSION,
    compute_checkpoint_digest,
    compute_entry_hash,
    derive_genesis_hash,
    is_lower_hex,
    sha256_hex,
)
from .models import LedgerCheckpoint, LedgerEntry
from .records import REDACTION_EVENT, RETENTION_EVENT, parse_redaction, parse_retention
from .signing import SignatureCheck, SignatureVerifier


class VerificationFailure(enum.Enum):
    NONE = "none"
    SEQUENCE_GAP = "sequence_gap"
    """A sequence number is missing, duplicated or out of order (deleted or inserted entry)."""
    BROKEN_LINK = "broken_link"
    """``previousHash`` does not match the hash of the previous entry."""
    HASH_MISMATCH = "hash_mismatch"
    """A signed field was modified."""
    PAYLOAD_HASH_MISMATCH = "payload_hash_mismatch"
    """The payload does not match ``payloadHash``."""
    INVALID_SIGNATURE = "invalid_signature"
    UNKNOWN_KEY = "unknown_key"
    LEDGER_ID_MISMATCH = "ledger_id_mismatch"
    UNSUPPORTED_FORMAT = "unsupported_format"
    CHECKPOINT_MISMATCH = "checkpoint_mismatch"
    """The trusted checkpoint is invalid, or the ledger does not contain the entry it describes."""
    TRUNCATED = "truncated"
    """The ledger ends before the sequence of the trusted checkpoint."""
    UNRECORDED_REDACTION = "unrecorded_redaction"
    """A payload is missing without a redaction record: it was erased in the database, not through the library."""


@dataclass(frozen=True)
class VerificationResult:
    valid: bool
    failure: VerificationFailure
    message: str
    failed_at_sequence: int | None
    failed_entry_id: str | None
    entries_verified: int
    redacted_payloads: int
    last_sequence: int
    last_hash: str | None
    first_sequence: int = 0
    """First sequence read: 1, or later when older entries were removed by retention (0 when none)."""

    def to_dict(self) -> dict[str, Any]:
        d = asdict(self)
        d["failure"] = self.failure.value
        return d


class LedgerTamperedError(Exception):
    """Raised by :meth:`LedgerVerifier.verify` when ``raise_on_failure`` is set and verification fails."""

    def __init__(self, result: VerificationResult):
        super().__init__(result.message)
        self.result = result


class EntrySource(Protocol):
    """Anything that yields the entries of one ledger with ``sequence >= from_sequence`` in ascending order."""

    def read(self, from_sequence: int = 1) -> Iterable[LedgerEntry]: ...


class LedgerVerifier:
    """Verifies a ledger read from an :class:`EntrySource` (a database reader, a JSON Lines export, a list)."""

    def __init__(self, source: EntrySource, verifier: SignatureVerifier, ledger_id: str, genesis_hash: str | None = None):
        if not ledger_id:
            raise ValueError("ledger_id is required")
        self.source = source
        self.signatures = verifier
        self.ledger_id = ledger_id
        self.genesis_hash = genesis_hash or derive_genesis_hash(ledger_id)

    def verify(
        self,
        checkpoint: LedgerCheckpoint | None = None,
        start_from_checkpoint: bool = False,
        raise_on_failure: bool = False,
        accept_unrecorded_redactions: bool = False,
    ) -> VerificationResult:
        """Verifies the chain.

        With a ``checkpoint`` obtained from trusted storage, the ledger must contain that exact entry: this is what
        detects removed entries at the end and full rewrites. ``start_from_checkpoint`` verifies only from the
        checkpoint onward (incremental verification).

        A missing payload must be authorized by a redaction record (written by :meth:`ProvanceLedger.redact_payload`
        since 0.2). ``accept_unrecorded_redactions`` accepts payloads erased with 0.1, which wrote no such record; with
        it, a payload deleted directly in the database is not detected.
        """
        result = self._verify(checkpoint, start_from_checkpoint, accept_unrecorded_redactions)
        if raise_on_failure and not result.valid:
            raise LedgerTamperedError(result)
        return result

    def check_checkpoint(self, checkpoint: LedgerCheckpoint) -> str | None:
        """Checks the format, ledger id and signature of a checkpoint. Returns an error message, or ``None``."""
        if checkpoint.format_version != FORMAT_VERSION:
            return f"Checkpoint format version {checkpoint.format_version} is not supported."
        if checkpoint.ledger_id != self.ledger_id:
            return f"Checkpoint belongs to ledger '{checkpoint.ledger_id}', expected '{self.ledger_id}'."
        if checkpoint.sequence < 1 or not is_lower_hex(checkpoint.hash, 64):
            return "Checkpoint sequence or hash is malformed."
        check = self._check_signature(checkpoint.key_id, compute_checkpoint_digest(checkpoint), checkpoint.signature)
        if check is SignatureCheck.VALID:
            return None
        if check is SignatureCheck.UNKNOWN_KEY:
            return f"Checkpoint is signed with unknown key '{checkpoint.key_id}'."
        return "Checkpoint signature is not valid."

    def _verify(self, checkpoint: LedgerCheckpoint | None, start_from_checkpoint: bool,
                accept_unrecorded_redactions: bool = False) -> VerificationResult:
        if checkpoint is not None:
            problem = self.check_checkpoint(checkpoint)
            if problem is not None:
                return _fail(VerificationFailure.CHECKPOINT_MISMATCH, problem, None, 0, 0, 0, None)

        from_checkpoint = checkpoint is not None and start_from_checkpoint
        expected_sequence = checkpoint.sequence if from_checkpoint else 1
        expected_previous = None if from_checkpoint else self.genesis_hash
        verified = redacted = last_sequence = 0
        last_hash: str | None = None
        checkpoint_seen = False
        # Retention (SPEC section 9): a ledger starting after sequence 1 needs a retention record for its first entry.
        first_sequence = 0
        first_hash: str | None = None
        start_needs_record = start_vouched = False
        # Redaction (SPEC section 6): every erased payload needs a redaction record naming it.
        erased: dict[int, str] = {}
        authorized: set[tuple[int, str]] = set()

        def fail(failure: VerificationFailure, message: str, entry: LedgerEntry | None, at: int | None = None) -> VerificationResult:
            result = _fail(failure, message, entry, verified, redacted, last_sequence, last_hash, at)
            return VerificationResult(**{**result.__dict__, "first_sequence": first_sequence})

        for entry in self.source.read(expected_sequence):
            if verified == 0 and not from_checkpoint and entry.sequence > expected_sequence:
                expected_sequence = entry.sequence
                expected_previous = None
                start_needs_record = True

            if entry.sequence != expected_sequence:
                what = "is missing" if entry.sequence > expected_sequence else "appears more than once or out of order"
                return fail(VerificationFailure.SEQUENCE_GAP,
                            f"Entry with sequence {expected_sequence} {what} (found {entry.sequence}).", entry, expected_sequence)

            if entry.format_version != FORMAT_VERSION:
                return fail(VerificationFailure.UNSUPPORTED_FORMAT, f"Format version {entry.format_version} is not supported.", entry)

            if entry.ledger_id != self.ledger_id:
                return fail(VerificationFailure.LEDGER_ID_MISMATCH,
                            f"Entry belongs to ledger '{entry.ledger_id}', expected '{self.ledger_id}'.", entry)

            if expected_previous is not None and entry.previous_hash != expected_previous:
                return fail(VerificationFailure.BROKEN_LINK,
                            f"PreviousHash is {entry.previous_hash}, expected {expected_previous}.", entry)

            if entry.payload_json is None:
                redacted += 1
                if not accept_unrecorded_redactions and (entry.sequence, entry.id) not in authorized:
                    erased[entry.sequence] = entry.id
            elif sha256_hex(entry.payload_json) != entry.payload_hash:
                return fail(VerificationFailure.PAYLOAD_HASH_MISMATCH, "Payload does not match PayloadHash.", entry)

            try:
                recomputed = compute_entry_hash(entry)
            except (ValueError, TypeError) as e:
                return fail(VerificationFailure.HASH_MISMATCH, f"Entry cannot be canonicalized: {e}", entry)

            if recomputed != entry.hash:
                return fail(VerificationFailure.HASH_MISMATCH,
                            f"Recomputed hash {recomputed} differs from stored hash {entry.hash}.", entry)

            check = self._check_signature(entry.key_id, entry.hash, entry.signature)
            if check is SignatureCheck.UNKNOWN_KEY:
                return fail(VerificationFailure.UNKNOWN_KEY, f"Entry is signed with unknown key '{entry.key_id}'.", entry)
            if check is not SignatureCheck.VALID:
                return fail(VerificationFailure.INVALID_SIGNATURE, "Signature is not valid.", entry)

            if verified == 0:
                first_sequence, first_hash = entry.sequence, entry.hash

            if entry.event_type == REDACTION_EVENT:
                named = parse_redaction(entry.payload_json)
                if named is not None:
                    authorized.add(named)
                    if erased.get(named[0]) == named[1]:
                        del erased[named[0]]

            if start_needs_record and not start_vouched and entry.event_type == RETENTION_EVENT:
                kept = parse_retention(entry.payload_json)
                if kept is not None and kept == (first_sequence, first_hash):
                    start_vouched = True

            if checkpoint is not None and entry.sequence == checkpoint.sequence:
                if entry.hash != checkpoint.hash:
                    return fail(VerificationFailure.CHECKPOINT_MISMATCH,
                                f"Entry {entry.sequence} has hash {entry.hash} but the trusted checkpoint says "
                                f"{checkpoint.hash}: history was rewritten.", entry)
                checkpoint_seen = True

            verified += 1
            last_sequence = entry.sequence
            last_hash = entry.hash
            expected_previous = entry.hash
            expected_sequence += 1

        if start_needs_record and not start_vouched:
            return fail(VerificationFailure.SEQUENCE_GAP,
                        f"Entries 1 to {first_sequence - 1} are missing, and no retention record in the ledger accounts "
                        "for their removal.", None, 1)

        if erased:
            sequence = min(erased)
            others = f" {len(erased) - 1} other entries are in the same case." if len(erased) > 1 else ""
            result = fail(VerificationFailure.UNRECORDED_REDACTION,
                          f"Entry {sequence} ({erased[sequence]}): the payload was removed without a redaction record, "
                          "so it was erased directly in the database, not through redact_payload." + others, None, sequence)
            return VerificationResult(**{**result.__dict__, "failed_entry_id": erased[sequence]})

        if checkpoint is not None and not checkpoint_seen and verified > 0 and checkpoint.sequence < first_sequence:
            return fail(VerificationFailure.CHECKPOINT_MISMATCH,
                        f"The trusted checkpoint references sequence {checkpoint.sequence}, which was removed by "
                        f"retention (the ledger now starts at {first_sequence}). Use a newer checkpoint.", None,
                        checkpoint.sequence)

        if checkpoint is not None and not checkpoint_seen:
            return fail(VerificationFailure.TRUNCATED,
                        f"The ledger ends at sequence {last_sequence} but the trusted checkpoint references sequence "
                        f"{checkpoint.sequence}.", None, last_sequence + 1)

        if verified == 0:
            message = "Ledger is empty."
        elif first_sequence > 1:
            message = f"{verified} entries verified, from sequence {first_sequence} to {last_sequence}."
        else:
            message = f"{verified} entries verified up to sequence {last_sequence}."
        if start_needs_record:
            message += f" Entries before {first_sequence} were removed by retention."
        if checkpoint is None and verified > 0:
            message += " Without a trusted checkpoint, removal of the last entries cannot be detected."
        return VerificationResult(True, VerificationFailure.NONE, message, None, None, verified, redacted, last_sequence,
                                  last_hash, first_sequence)

    def _check_signature(self, key_id: str, hex_digest: str, hex_signature: str) -> SignatureCheck:
        if not is_lower_hex(hex_signature) or not is_lower_hex(hex_digest, 64):
            return SignatureCheck.INVALID
        return self.signatures.verify(key_id, bytes.fromhex(hex_digest), bytes.fromhex(hex_signature))


def _fail(failure: VerificationFailure, message: str, entry: LedgerEntry | None, verified: int, redacted: int,
          last_sequence: int, last_hash: str | None, at: int | None = None) -> VerificationResult:
    failed_at = at if at is not None else (entry.sequence if entry else None)
    text = message if entry is None else f"Entry {entry.sequence} ({entry.id}): {message}"
    return VerificationResult(False, failure, text, failed_at, entry.id if entry else None,
                              verified, redacted, last_sequence, last_hash)
