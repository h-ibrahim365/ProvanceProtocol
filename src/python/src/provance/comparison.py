"""Field-level comparison between two copies of a ledger (the primary store and its archive, or an export).

Verification tells which entry was changed; it cannot tell the original value, because the ledger only keeps its hash.
With two copies, the copy whose entry still verifies on its own is the original, and the comparison shows the original
and the current value of every changed field. Same algorithm and same report as the .NET ``LedgerComparer``.
"""

from __future__ import annotations

import enum
import json
from dataclasses import dataclass, field
from typing import Any, Iterable, Iterator, Protocol

from .format import FORMAT_VERSION, compute_entry_hash, format_timestamp, is_lower_hex, sha256_hex
from .models import LedgerEntry
from .records import RETENTION_EVENT, parse_retention
from .signing import SignatureCheck, SignatureVerifier


class EntrySource(Protocol):
    def read(self, from_sequence: int = 1) -> Iterable[LedgerEntry]: ...


class DifferenceKind(enum.Enum):
    CHANGED = "changed"
    """Both copies have the entry, with different content."""
    MISSING_FROM_PRIMARY = "missing_from_primary"
    """The archive has the entry, the primary store does not."""
    MISSING_FROM_ARCHIVE = "missing_from_archive"
    """The primary store has the entry and the archive does not, although the archive holds later entries."""
    REDACTED_IN_ONE_COPY = "redacted_in_one_copy"
    """The payload was erased in one copy only. Not tampering."""


class OriginalCopy(enum.Enum):
    ARCHIVE = "archive"
    """The archive copy verifies and the primary copy does not: the primary store was modified."""
    PRIMARY = "primary"
    """The primary copy verifies and the archive copy does not: the archive was modified."""
    BOTH_VERIFY = "both_verify"
    """Both copies verify on their own: whoever changed one of them holds the signing key."""
    NEITHER_VERIFIES = "neither_verifies"
    """Neither copy verifies on its own: both were modified."""


@dataclass(frozen=True)
class FieldDifference:
    field: str
    """Envelope field (``eventType``, ``timestamp``, ...) or payload path (``payload.data.amount``)."""
    archive_value: str | None
    primary_value: str | None


@dataclass(frozen=True)
class EntryDifference:
    sequence: int
    kind: DifferenceKind
    through_sequence: int | None = None
    original: OriginalCopy | None = None
    event_type: str | None = None
    fields: tuple[FieldDifference, ...] = ()

    @property
    def is_tampering(self) -> bool:
        return self.kind is not DifferenceKind.REDACTED_IN_ONE_COPY

    @property
    def summary(self) -> str:
        event = f" ({self.event_type})" if self.event_type else ""
        if self.kind is DifferenceKind.CHANGED:
            where = {
                OriginalCopy.ARCHIVE: "in the primary store (the archive copy verifies, the primary copy does not)",
                OriginalCopy.PRIMARY: "in the archive (the primary copy verifies, the archive copy does not)",
                OriginalCopy.BOTH_VERIFY: "(both copies verify on their own: whoever changed it holds the signing key)",
            }.get(self.original, "(neither copy verifies on its own: both were modified)")
            return f"Entry {self.sequence}{event} was changed {where}."
        if self.kind is DifferenceKind.MISSING_FROM_PRIMARY:
            if self.through_sequence is not None and self.through_sequence != self.sequence:
                return f"Entries {self.sequence} to {self.through_sequence} are in the archive but missing from the primary store."
            return f"Entry {self.sequence}{event} is in the archive but missing from the primary store."
        if self.kind is DifferenceKind.MISSING_FROM_ARCHIVE:
            return f"Entry {self.sequence}{event} is in the primary store but missing from the archive."
        return f"Entry {self.sequence}{event}: the payload was erased in one copy only."


@dataclass(frozen=True)
class LedgerComparison:
    differences: tuple[EntryDifference, ...]
    differences_truncated: bool = False
    compared: int = 0
    removed_by_retention: int = 0
    not_yet_copied: int = 0
    primary_last_sequence: int = 0
    archive_last_sequence: int = 0

    @property
    def identical(self) -> bool:
        """``True`` when no entry was changed, deleted or inserted in either copy."""
        return not any(d.is_tampering for d in self.differences)

    def describe(self) -> str:
        """A plain text report, one difference per paragraph."""
        tampering = sum(1 for d in self.differences if d.is_tampering)
        lines = [f"The two copies match: {self.compared} entries compared." if self.identical
                 else f"{tampering}{'+' if self.differences_truncated else ''} difference(s) in {self.compared} entries compared."]
        if self.removed_by_retention:
            lines[0] += f" {self.removed_by_retention} older entries are only in the archive (retention)."
        if self.not_yet_copied:
            lines[0] += f" {self.not_yet_copied} recent entries are not copied to the archive yet."
        if any(d.fields for d in self.differences):
            lines.append("Changed fields are shown as: archive value -> primary value.")
        for d in self.differences:
            lines.append("")
            lines.append(d.summary)
            for f in d.fields:
                lines.append(f"  {f.field}: {_show(f.archive_value)} -> {_show(f.primary_value)}")
        if self.differences_truncated:
            lines += ["", "More differences exist; only the first ones are listed."]
        return "\n".join(lines) + "\n"


def _show(value: str | None) -> str:
    return "(absent)" if value is None else value


class LedgerComparer:
    """Compares two copies of a ledger, reading both in sequence order at the same time."""

    def __init__(self, verifier: SignatureVerifier, ledger_id: str):
        self.signatures = verifier
        self.ledger_id = ledger_id

    def compare(self, primary: EntrySource, archive: EntrySource, max_differences: int = 1000) -> LedgerComparison:
        if max_differences < 1:
            raise ValueError("max_differences must be at least 1.")
        differences: list[EntryDifference] = []
        truncated = False
        compared = not_yet_copied = primary_last = archive_last = 0
        primary_first = 0
        primary_first_hash: str | None = None
        only_in_archive_at_start = 0
        start_vouched = False
        last_p = last_a = 0

        def add(d: EntryDifference) -> None:
            nonlocal truncated
            if len(differences) < max_differences:
                differences.append(d)
            else:
                truncated = True

        def check_record(entry: LedgerEntry) -> None:
            nonlocal start_vouched
            if not start_vouched and primary_first > 1 and entry.event_type == RETENTION_EVENT:
                if parse_retention(entry.payload_json) == (primary_first, primary_first_hash):
                    start_vouched = True

        p: Iterator[LedgerEntry] = iter(primary.read(1))
        a: Iterator[LedgerEntry] = iter(archive.read(1))
        pe, ae = next(p, None), next(a, None)
        while pe is not None or ae is not None:
            if pe is not None and pe.sequence <= last_p:
                add(EntryDifference(pe.sequence, DifferenceKind.MISSING_FROM_ARCHIVE, event_type=pe.event_type,
                                    fields=(FieldDifference("sequence", None, f"{pe.sequence} (out of order, after {last_p})"),)))
                pe = next(p, None)
                continue
            if ae is not None and ae.sequence <= last_a:
                add(EntryDifference(ae.sequence, DifferenceKind.MISSING_FROM_PRIMARY, event_type=ae.event_type,
                                    fields=(FieldDifference("sequence", f"{ae.sequence} (out of order, after {last_a})", None),)))
                archive_last = max(archive_last, ae.sequence)
                ae = next(a, None)
                continue
            if pe is not None and primary_first == 0:
                primary_first, primary_first_hash = pe.sequence, pe.hash

            if pe is not None and ae is not None and pe.sequence == ae.sequence:
                compared += 1
                primary_last = archive_last = pe.sequence
                check_record(pe)
                difference = self.compare_entry(ae, pe)
                if difference is not None:
                    add(difference)
                last_p, last_a = pe.sequence, ae.sequence
                pe, ae = next(p, None), next(a, None)
            elif pe is not None and (ae is None or pe.sequence < ae.sequence):
                primary_last = pe.sequence
                check_record(pe)
                if ae is None:
                    not_yet_copied += 1  # the archive ends before this entry
                else:
                    add(EntryDifference(pe.sequence, DifferenceKind.MISSING_FROM_ARCHIVE, event_type=pe.event_type))
                last_p = pe.sequence
                pe = next(p, None)
            else:
                assert ae is not None
                archive_last = ae.sequence
                if last_p == 0:
                    only_in_archive_at_start += 1  # the primary store starts later, or is empty: decided at the end
                else:
                    add(EntryDifference(ae.sequence, DifferenceKind.MISSING_FROM_PRIMARY, event_type=ae.event_type))
                last_a = ae.sequence
                ae = next(a, None)

        removed_by_retention = 0
        if only_in_archive_at_start:
            if start_vouched:
                removed_by_retention = only_in_archive_at_start
            else:
                through = primary_first - 1 if primary_first > 0 else archive_last
                differences.insert(0, EntryDifference(1, DifferenceKind.MISSING_FROM_PRIMARY, through_sequence=through))
                if len(differences) > max_differences:
                    differences.pop()
                    truncated = True

        return LedgerComparison(tuple(differences), truncated, compared, removed_by_retention, not_yet_copied,
                                primary_last, archive_last)

    def compare_entry(self, archive: LedgerEntry, primary: LedgerEntry) -> EntryDifference | None:
        """Compares two copies of the entry at one sequence. ``None`` when they are identical."""
        fields: list[FieldDifference] = []

        def field_(name: str, before: str | None, after: str | None) -> None:
            if before != after:
                fields.append(FieldDifference(name, before, after))

        field_("id", archive.id, primary.id)
        field_("eventType", archive.event_type, primary.event_type)
        field_("timestamp", format_timestamp(archive.timestamp), format_timestamp(primary.timestamp))
        field_("ledgerId", archive.ledger_id, primary.ledger_id)
        field_("v", str(archive.format_version), str(primary.format_version))
        field_("keyId", archive.key_id, primary.key_id)

        redacted_in_one = (archive.payload_json is None) != (primary.payload_json is None)
        if archive.payload_json is not None and primary.payload_json is not None:
            _compare_payloads(archive.payload_json, primary.payload_json, fields)
        elif redacted_in_one and archive.payload_hash != primary.payload_hash:
            field_("payload", archive.payload_json, primary.payload_json)

        field_("payloadHash", archive.payload_hash, primary.payload_hash)
        field_("previousHash", archive.previous_hash, primary.previous_hash)
        field_("hash", archive.hash, primary.hash)
        field_("signature", archive.signature, primary.signature)

        if not fields:
            return (EntryDifference(primary.sequence, DifferenceKind.REDACTED_IN_ONE_COPY, event_type=primary.event_type)
                    if redacted_in_one else None)

        archive_ok, primary_ok = self.verifies_alone(archive), self.verifies_alone(primary)
        original = {(True, False): OriginalCopy.ARCHIVE, (False, True): OriginalCopy.PRIMARY,
                    (True, True): OriginalCopy.BOTH_VERIFY}.get((archive_ok, primary_ok), OriginalCopy.NEITHER_VERIFIES)
        return EntryDifference(primary.sequence, DifferenceKind.CHANGED, original=original,
                               event_type=primary.event_type if original is OriginalCopy.PRIMARY else archive.event_type,
                               fields=tuple(fields))

    def verifies_alone(self, entry: LedgerEntry) -> bool:
        """Checks one entry without its neighbors: payload hash, envelope hash and signature."""
        if entry.format_version != FORMAT_VERSION or entry.ledger_id != self.ledger_id:
            return False
        if entry.payload_json is not None and sha256_hex(entry.payload_json) != entry.payload_hash:
            return False
        try:
            if compute_entry_hash(entry) != entry.hash:
                return False
        except (ValueError, TypeError):
            return False
        if not is_lower_hex(entry.signature) or not is_lower_hex(entry.hash, 64):
            return False
        return self.signatures.verify(entry.key_id, bytes.fromhex(entry.hash), bytes.fromhex(entry.signature)) is SignatureCheck.VALID


def _compare_payloads(archive_json: str, primary_json: str, fields: list[FieldDifference]) -> None:
    if archive_json == primary_json:
        return
    before, after = _flatten(archive_json), _flatten(primary_json)
    if before is None or after is None:
        fields.append(FieldDifference("payload", archive_json, primary_json))
        return
    for path in sorted(set(before) | set(after)):
        if before.get(path) != after.get(path):
            fields.append(FieldDifference(path, before.get(path), after.get(path)))


def _flatten(text: str) -> dict[str, str] | None:
    try:
        root = json.loads(text)
    except ValueError:
        return None
    out: dict[str, str] = {}

    def walk(value: Any, path: str) -> None:
        if isinstance(value, dict) and value:
            for key, item in value.items():
                walk(item, f"{path}.{key}")
        elif isinstance(value, list) and value:
            for i, item in enumerate(value):
                walk(item, f"{path}[{i}]")
        else:
            out[path] = json.dumps(value, ensure_ascii=False, separators=(",", ":"))

    walk(root, "payload")
    return out
