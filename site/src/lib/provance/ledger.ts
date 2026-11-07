// Writing and verifying a ledger held in memory, as the libraries do against a database.

import {
  type Checkpoint,
  type Json,
  type LedgerEntry,
  FORMAT_VERSION,
  HmacKeys,
  canonicalCheckpoint,
  canonicalize,
  entryHash,
  genesisHash,
  newId,
  sha256Hex,
  timestamp,
} from "./format";

export type Failure =
  | "sequence_gap"
  | "unsupported_format"
  | "ledger_id_mismatch"
  | "broken_link"
  | "payload_hash_mismatch"
  | "hash_mismatch"
  | "unknown_key"
  | "invalid_signature"
  | "checkpoint_mismatch"
  | "truncated"
  | "unrecorded_redaction";

/** Event types the libraries write themselves (docs/SPEC.md, sections 6 and 9). */
export const RETENTION_EVENT = "provance.retention";
export const REDACTION_EVENT = "provance.redaction";

export interface VerifyOptions {
  /** Trusted checkpoint, kept outside the database. */
  checkpoint?: Checkpoint | null;
  /** Accept payloads erased without a redaction record, as 0.1 wrote them. */
  acceptUnrecordedRedactions?: boolean;
}

/** One check made on one entry, in the order of docs/SPEC.md section 5. */
export interface Check {
  name: "sequence" | "format" | "link" | "payload" | "hash" | "signature" | "checkpoint";
  ok: boolean;
  skipped?: boolean;
  expected?: string;
  actual?: string;
}

export interface EntryReport {
  /** Index in the list that was verified, or -1 for the checkpoint itself. */
  index: number;
  sequence: number;
  checks: Check[];
  ok: boolean;
}

export interface VerificationResult {
  valid: boolean;
  failure: Failure | null;
  message: string;
  failedAtSequence: number | null;
  failedIndex: number | null;
  entriesVerified: number;
  redacted: number;
  lastSequence: number;
  lastHash: string | null;
  /** First sequence of the table: above 1 after retention. */
  firstSequence: number;
  reports: EntryReport[];
}

export function seal(
  previous: LedgerEntry | null,
  ledgerId: string,
  keyId: string,
  keys: HmacKeys,
  eventType: string,
  payload: Json,
  at: Date = new Date(),
): LedgerEntry {
  const payloadJson = canonicalize(payload);
  const draft = {
    v: FORMAT_VERSION,
    ledgerId,
    sequence: previous ? previous.sequence + 1 : 1,
    id: newId(),
    timestamp: timestamp(at),
    eventType,
    payloadHash: sha256Hex(payloadJson),
    previousHash: previous ? previous.hash : genesisHash(ledgerId),
    keyId,
  };
  const hash = entryHash(draft);
  return { ...draft, payloadJson, hash, signature: keys.sign(keyId, hash) };
}

export function createCheckpoint(head: LedgerEntry, keyId: string, keys: HmacKeys, at: Date = new Date()): Checkpoint {
  const body = { v: FORMAT_VERSION, ledgerId: head.ledgerId, sequence: head.sequence, hash: head.hash,
    createdAt: timestamp(at), keyId };
  return { ...body, signature: keys.sign(keyId, sha256Hex(canonicalCheckpoint(body))) };
}

/** Payload of the record appended before an erasure. */
export function redactionPayload(entry: LedgerEntry): Json {
  return { entryId: entry.id, sequence: entry.sequence };
}

/** Payload of the record appended before entries are removed by retention. */
export function retentionPayload(first: LedgerEntry): Json {
  return { firstHash: first.hash, firstSequence: first.sequence };
}

function object(payloadJson: string | null): Record<string, unknown> | null {
  if (payloadJson === null) return null;
  try {
    const value: unknown = JSON.parse(payloadJson);
    return value && typeof value === "object" && !Array.isArray(value) ? (value as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

/** The entry a redaction record names, as "sequence:id". */
function namedByRedaction(payloadJson: string | null): string | null {
  const o = object(payloadJson);
  if (!o || !Number.isInteger(o.sequence) || (o.sequence as number) < 1 || typeof o.entryId !== "string") return null;
  return `${o.sequence}:${o.entryId.toLowerCase()}`;
}

/** The first entry a retention record keeps, as "sequence:hash". */
function namedByRetention(payloadJson: string | null): string | null {
  const o = object(payloadJson);
  if (!o || !Number.isInteger(o.firstSequence) || (o.firstSequence as number) < 2 || typeof o.firstHash !== "string") return null;
  return `${o.firstSequence}:${o.firstHash}`;
}

/** Same checks, order and failure kinds as LedgerVerifier in .NET, Java and Python (0.2 rules). */
export function verify(
  entries: readonly LedgerEntry[],
  ledgerId: string,
  keys: HmacKeys,
  options: VerifyOptions | Checkpoint | null = null,
): VerificationResult {
  const opts: VerifyOptions = options && "signature" in options ? { checkpoint: options as Checkpoint } : ((options as VerifyOptions | null) ?? {});
  const checkpoint = opts.checkpoint ?? null;
  const reports: EntryReport[] = [];
  let verified = 0;
  let redacted = 0;
  let lastSequence = 0;
  let lastHash: string | null = null;
  let firstSequence = 0;
  let firstHash: string | null = null;
  let startNeedsRecord = false;
  let startVouched = false;
  const erased = new Map<number, string>();
  const authorized = new Set<string>();

  const result = (failure: Failure | null, message: string, sequence: number | null, index: number | null) => ({
    valid: failure === null, failure, message, failedAtSequence: sequence, failedIndex: index,
    entriesVerified: verified, redacted, lastSequence, lastHash, firstSequence, reports,
  });

  if (checkpoint) {
    const digest = sha256Hex(canonicalCheckpoint(checkpoint));
    const check = keys.verify(checkpoint.keyId, digest, checkpoint.signature);
    if (checkpoint.ledgerId !== ledgerId || check !== "valid") {
      reports.push({ index: -1, sequence: checkpoint.sequence, ok: false,
        checks: [{ name: "signature", ok: false }] });
      return result("checkpoint_mismatch", "The checkpoint itself is not valid (signature or ledger).", null, null);
    }
  }

  let expectedSequence = 1;
  let expectedPrevious: string | null = genesisHash(ledgerId);
  let checkpointSeen = false;

  for (const [index, entry] of entries.entries()) {
    const checks: Check[] = [];
    const report: EntryReport = { index, sequence: entry.sequence, checks, ok: false };
    reports.push(report);
    const fail = (failure: Failure, message: string, at = entry.sequence) => result(failure, message, at, index);

    // Retention (SPEC section 9): the table may start after 1, if a later retention record names this first entry.
    if (verified === 0 && entry.sequence > expectedSequence) {
      expectedSequence = entry.sequence;
      expectedPrevious = null;
      startNeedsRecord = true;
    }

    const sequenceOk = entry.sequence === expectedSequence;
    checks.push({ name: "sequence", ok: sequenceOk, expected: String(expectedSequence), actual: String(entry.sequence) });
    if (!sequenceOk) {
      const what = entry.sequence > expectedSequence ? "is missing" : "appears twice or out of order";
      return fail("sequence_gap", `Entry ${expectedSequence} ${what} (found ${entry.sequence}).`, expectedSequence);
    }

    const formatOk = entry.v === FORMAT_VERSION && entry.ledgerId === ledgerId;
    checks.push({ name: "format", ok: formatOk });
    if (entry.v !== FORMAT_VERSION) return fail("unsupported_format", `Format version ${entry.v} is not supported.`);
    if (entry.ledgerId !== ledgerId) return fail("ledger_id_mismatch", `Entry belongs to ledger '${entry.ledgerId}'.`);

    if (expectedPrevious === null) {
      checks.push({ name: "link", ok: true, skipped: true });
    } else {
      const linkOk = entry.previousHash === expectedPrevious;
      checks.push({ name: "link", ok: linkOk, expected: expectedPrevious, actual: entry.previousHash });
      if (!linkOk) return fail("broken_link", "previousHash does not match the hash of the entry before it.");
    }

    if (entry.payloadJson === null) {
      redacted++;
      if (!opts.acceptUnrecordedRedactions && !authorized.has(`${entry.sequence}:${entry.id.toLowerCase()}`)) {
        erased.set(entry.sequence, entry.id);
      }
      checks.push({ name: "payload", ok: true, skipped: true });
    } else {
      const actual = sha256Hex(entry.payloadJson);
      const payloadOk = actual === entry.payloadHash;
      checks.push({ name: "payload", ok: payloadOk, expected: entry.payloadHash, actual });
      if (!payloadOk) return fail("payload_hash_mismatch", "The payload does not match its payloadHash.");
    }

    let recomputed: string;
    try {
      recomputed = entryHash(entry);
    } catch (e) {
      checks.push({ name: "hash", ok: false });
      return fail("hash_mismatch", `Entry cannot be canonicalized: ${(e as Error).message}`);
    }
    const hashOk = recomputed === entry.hash;
    checks.push({ name: "hash", ok: hashOk, expected: recomputed, actual: entry.hash });
    if (!hashOk) return fail("hash_mismatch", "Recomputing the hash from the stored fields gives a different value.");

    const signature = keys.verify(entry.keyId, entry.hash, entry.signature);
    checks.push({ name: "signature", ok: signature === "valid" });
    if (signature === "unknown-key") return fail("unknown_key", `Entry is signed with unknown key '${entry.keyId}'.`);
    if (signature !== "valid") return fail("invalid_signature", "The signature does not match the hash.");

    if (verified === 0) {
      firstSequence = entry.sequence;
      firstHash = entry.hash;
    }

    if (entry.eventType === REDACTION_EVENT) {
      const named = namedByRedaction(entry.payloadJson);
      if (named) {
        authorized.add(named);
        const [seq, id] = named.split(":");
        if (erased.get(Number(seq))?.toLowerCase() === id) erased.delete(Number(seq));
      }
    }

    if (startNeedsRecord && !startVouched && entry.eventType === RETENTION_EVENT
        && namedByRetention(entry.payloadJson) === `${firstSequence}:${firstHash}`) {
      startVouched = true;
    }

    if (checkpoint && entry.sequence === checkpoint.sequence) {
      const same = entry.hash === checkpoint.hash;
      checks.push({ name: "checkpoint", ok: same, expected: checkpoint.hash, actual: entry.hash });
      if (!same) return fail("checkpoint_mismatch", "This entry differs from the trusted checkpoint: history was rewritten.");
      checkpointSeen = true;
    }

    report.ok = true;
    verified++;
    lastSequence = entry.sequence;
    lastHash = entry.hash;
    expectedPrevious = entry.hash;
    expectedSequence++;
  }

  /** Marks a check of an entry that passed on its own but fails once the whole table is read. */
  const blame = (index: number, name: Check["name"]) => {
    const report = reports.find((r) => r.index === index);
    if (!report) return;
    report.ok = false;
    const check = report.checks.find((c) => c.name === name);
    if (check) {
      check.ok = false;
      check.skipped = false;
    }
  };

  if (startNeedsRecord && !startVouched) {
    blame(0, "sequence");
    return result("sequence_gap",
      `Entries 1 to ${firstSequence - 1} are missing, and no retention record in the ledger accounts for their removal.`, 1, 0);
  }

  if (erased.size > 0) {
    const sequence = Math.min(...erased.keys());
    const index = entries.findIndex((e) => e.sequence === sequence);
    blame(index, "payload");
    return result("unrecorded_redaction",
      `Entry ${sequence}: the payload was removed without a redaction record, so it was erased directly in the database.`,
      sequence, index >= 0 ? index : null);
  }

  if (checkpoint && !checkpointSeen && verified > 0 && checkpoint.sequence < firstSequence) {
    return result("checkpoint_mismatch",
      `The checkpoint references entry ${checkpoint.sequence}, which was removed by retention. Use a newer checkpoint.`,
      checkpoint.sequence, null);
  }

  if (checkpoint && !checkpointSeen) {
    return result("truncated", `The ledger ends at ${lastSequence}, but the trusted checkpoint was taken at ${checkpoint.sequence}.`,
      lastSequence + 1, null);
  }
  let message = verified === 0 ? "The ledger is empty."
    : firstSequence > 1 ? `${verified} entries verified, from sequence ${firstSequence} to ${lastSequence}.`
    : `${verified} entries verified up to sequence ${lastSequence}.`;
  if (startNeedsRecord) message += ` Entries before ${firstSequence} were removed by retention.`;
  return result(null, message, null, null);
}
