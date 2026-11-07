// Field-level comparison of two copies of a ledger, as LedgerComparer in .NET, Java and Python (docs/ARCHIVE.md).

import { type LedgerEntry, FORMAT_VERSION, HmacKeys, canonicalTime, entryHash, sha256Hex } from "./format";
import { RETENTION_EVENT } from "./ledger";

export type DifferenceKind = "changed" | "missing_from_primary" | "missing_from_archive" | "redacted_in_one_copy";

/** Which copy still verifies on its own, for a changed entry. */
export type OriginalCopy = "archive" | "primary" | "both_verify" | "neither_verifies";

export interface FieldDifference {
  field: string;
  archiveValue: string | null;
  primaryValue: string | null;
}

export interface EntryDifference {
  sequence: number;
  kind: DifferenceKind;
  throughSequence?: number;
  original?: OriginalCopy;
  eventType?: string;
  fields: FieldDifference[];
}

export interface LedgerComparison {
  differences: EntryDifference[];
  compared: number;
  removedByRetention: number;
  notYetCopied: number;
  identical: boolean;
}

export function isTampering(d: EntryDifference): boolean {
  return d.kind !== "redacted_in_one_copy";
}

export function summary(d: EntryDifference): string {
  const event = d.eventType ? ` (${d.eventType})` : "";
  switch (d.kind) {
    case "changed": {
      const where = {
        archive: "in the main table (the archive copy verifies, the main copy does not)",
        primary: "in the archive (the main copy verifies, the archive copy does not)",
        both_verify: "(both copies verify on their own: whoever changed it holds the signing key)",
        neither_verifies: "(neither copy verifies on its own: both were modified)",
      }[d.original ?? "neither_verifies"];
      return `Entry ${d.sequence}${event} was changed ${where}.`;
    }
    case "missing_from_primary":
      return d.throughSequence !== undefined && d.throughSequence !== d.sequence
        ? `Entries ${d.sequence} to ${d.throughSequence} are in the archive but missing from the main table.`
        : `Entry ${d.sequence}${event} is in the archive but missing from the main table.`;
    case "missing_from_archive":
      return `Entry ${d.sequence}${event} is in the main table but missing from the archive.`;
    default:
      return `Entry ${d.sequence}${event}: the payload was erased in one copy only.`;
  }
}

/** One entry checked without its neighbors: payload hash, hash and signature. */
export function verifiesAlone(entry: LedgerEntry, ledgerId: string, keys: HmacKeys): boolean {
  if (entry.v !== FORMAT_VERSION || entry.ledgerId !== ledgerId) return false;
  if (entry.payloadJson !== null && sha256Hex(entry.payloadJson) !== entry.payloadHash) return false;
  try {
    if (entryHash(entry) !== entry.hash) return false;
  } catch {
    return false;
  }
  return keys.verify(entry.keyId, entry.hash, entry.signature) === "valid";
}

function flatten(text: string): Map<string, string> | null {
  let root: unknown;
  try {
    root = JSON.parse(text);
  } catch {
    return null;
  }
  const out = new Map<string, string>();
  const walk = (value: unknown, path: string) => {
    if (value && typeof value === "object" && !Array.isArray(value) && Object.keys(value).length > 0) {
      for (const [k, v] of Object.entries(value)) walk(v, `${path}.${k}`);
    } else if (Array.isArray(value) && value.length > 0) {
      value.forEach((v, i) => walk(v, `${path}[${i}]`));
    } else {
      out.set(path, JSON.stringify(value));
    }
  };
  walk(root, "payload");
  return out;
}

export function compareEntry(archive: LedgerEntry, primary: LedgerEntry, ledgerId: string, keys: HmacKeys): EntryDifference | null {
  const fields: FieldDifference[] = [];
  const field = (name: string, before: string | null, after: string | null) => {
    if (before !== after) fields.push({ field: name, archiveValue: before, primaryValue: after });
  };
  field("id", archive.id, primary.id);
  field("eventType", archive.eventType, primary.eventType);
  field("timestamp", canonicalTime(archive.timestamp), canonicalTime(primary.timestamp));
  field("ledgerId", archive.ledgerId, primary.ledgerId);
  field("v", String(archive.v), String(primary.v));
  field("keyId", archive.keyId, primary.keyId);

  const redactedInOne = (archive.payloadJson === null) !== (primary.payloadJson === null);
  if (archive.payloadJson !== null && primary.payloadJson !== null && archive.payloadJson !== primary.payloadJson) {
    const before = flatten(archive.payloadJson);
    const after = flatten(primary.payloadJson);
    if (!before || !after) {
      field("payload", archive.payloadJson, primary.payloadJson);
    } else {
      for (const path of [...new Set([...before.keys(), ...after.keys()])].sort()) {
        field(path, before.get(path) ?? null, after.get(path) ?? null);
      }
    }
  } else if (redactedInOne && archive.payloadHash !== primary.payloadHash) {
    field("payload", archive.payloadJson, primary.payloadJson);
  }
  field("payloadHash", archive.payloadHash, primary.payloadHash);
  field("previousHash", archive.previousHash, primary.previousHash);
  field("hash", archive.hash, primary.hash);
  field("signature", archive.signature, primary.signature);

  if (fields.length === 0) {
    return redactedInOne ? { sequence: primary.sequence, kind: "redacted_in_one_copy", eventType: primary.eventType, fields } : null;
  }
  const a = verifiesAlone(archive, ledgerId, keys);
  const p = verifiesAlone(primary, ledgerId, keys);
  const original: OriginalCopy = a && !p ? "archive" : p && !a ? "primary" : a && p ? "both_verify" : "neither_verifies";
  return {
    sequence: primary.sequence,
    kind: "changed",
    original,
    eventType: original === "primary" ? primary.eventType : archive.eventType,
    fields,
  };
}

function retentionNames(entry: LedgerEntry): string | null {
  if (entry.eventType !== RETENTION_EVENT || entry.payloadJson === null) return null;
  try {
    const o = JSON.parse(entry.payloadJson) as { firstSequence?: unknown; firstHash?: unknown };
    return `${o.firstSequence}:${o.firstHash}`;
  } catch {
    return null;
  }
}

/** Compares the main table with the archive, reading both in sequence order. */
export function compare(primary: readonly LedgerEntry[], archive: readonly LedgerEntry[], ledgerId: string, keys: HmacKeys): LedgerComparison {
  const differences: EntryDifference[] = [];
  let compared = 0;
  let notYetCopied = 0;
  let onlyInArchiveAtStart = 0;
  let archiveLast = 0;
  const first = primary[0];
  const startVouched = !!first && first.sequence > 1
    && primary.some((e) => retentionNames(e) === `${first.sequence}:${first.hash}`);

  let i = 0;
  let j = 0;
  let lastP = 0;
  let lastA = 0;
  while (i < primary.length || j < archive.length) {
    const pe = primary[i];
    const ae = archive[j];
    if (pe && pe.sequence <= lastP) {
      differences.push({ sequence: pe.sequence, kind: "missing_from_archive", eventType: pe.eventType,
        fields: [{ field: "sequence", archiveValue: null, primaryValue: `${pe.sequence} (out of order, after ${lastP})` }] });
      i++;
      continue;
    }
    if (ae && ae.sequence <= lastA) {
      differences.push({ sequence: ae.sequence, kind: "missing_from_primary", eventType: ae.eventType,
        fields: [{ field: "sequence", archiveValue: `${ae.sequence} (out of order, after ${lastA})`, primaryValue: null }] });
      j++;
      continue;
    }
    if (pe && ae && pe.sequence === ae.sequence) {
      compared++;
      const d = compareEntry(ae, pe, ledgerId, keys);
      if (d) differences.push(d);
      lastP = pe.sequence;
      lastA = archiveLast = ae.sequence;
      i++;
      j++;
    } else if (pe && (!ae || pe.sequence < ae.sequence)) {
      if (!ae) notYetCopied++;
      else differences.push({ sequence: pe.sequence, kind: "missing_from_archive", eventType: pe.eventType, fields: [] });
      lastP = pe.sequence;
      i++;
    } else if (ae) {
      archiveLast = ae.sequence;
      if (lastP === 0) onlyInArchiveAtStart++;
      else differences.push({ sequence: ae.sequence, kind: "missing_from_primary", eventType: ae.eventType, fields: [] });
      lastA = ae.sequence;
      j++;
    }
  }

  let removedByRetention = 0;
  if (onlyInArchiveAtStart > 0) {
    if (startVouched) removedByRetention = onlyInArchiveAtStart;
    else differences.unshift({ sequence: 1, kind: "missing_from_primary", throughSequence: first ? first.sequence - 1 : archiveLast, fields: [] });
  }
  return { differences, compared, removedByRetention, notYetCopied, identical: !differences.some(isTampering) };
}
