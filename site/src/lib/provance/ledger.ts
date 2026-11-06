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
  | "truncated";

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

/** Same checks, order and failure kinds as LedgerVerifier in .NET, Java and Python. */
export function verify(
  entries: readonly LedgerEntry[],
  ledgerId: string,
  keys: HmacKeys,
  checkpoint: Checkpoint | null = null,
): VerificationResult {
  const reports: EntryReport[] = [];
  let verified = 0;
  let redacted = 0;
  let lastSequence = 0;
  let lastHash: string | null = null;

  const result = (failure: Failure | null, message: string, sequence: number | null, index: number | null) => ({
    valid: failure === null, failure, message, failedAtSequence: sequence, failedIndex: index,
    entriesVerified: verified, redacted, lastSequence, lastHash, reports,
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
  let expectedPrevious = genesisHash(ledgerId);
  let checkpointSeen = false;

  for (const [index, entry] of entries.entries()) {
    const checks: Check[] = [];
    const report: EntryReport = { index, sequence: entry.sequence, checks, ok: false };
    reports.push(report);
    const fail = (failure: Failure, message: string, at = entry.sequence) => result(failure, message, at, index);

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

    const linkOk = entry.previousHash === expectedPrevious;
    checks.push({ name: "link", ok: linkOk, expected: expectedPrevious, actual: entry.previousHash });
    if (!linkOk) return fail("broken_link", "previousHash does not match the hash of the entry before it.");

    if (entry.payloadJson === null) {
      redacted++;
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

  if (checkpoint && !checkpointSeen) {
    return result("truncated", `The ledger ends at ${lastSequence}, but the trusted checkpoint was taken at ${checkpoint.sequence}.`,
      lastSequence + 1, null);
  }
  const message = verified === 0 ? "The ledger is empty." : `${verified} entries verified up to sequence ${lastSequence}.`;
  return result(null, message, null, null);
}
