// Plain language for verification results.

import type { Failure } from "../provance/ledger";
import type { Demo } from "./demo.svelte";

export const CHECK_LABELS: Record<string, string> = {
  sequence: "Sequence",
  format: "Ledger",
  link: "Link",
  payload: "Payload",
  hash: "Hash",
  signature: "Signature",
  checkpoint: "Checkpoint",
};

const FAILURES: Record<Failure, (n: number | null) => { title: string; detail: string }> = {
  sequence_gap: (n) => n === 1
    ? {
      title: "The first entries were deleted",
      detail: "The table starts after entry 1, and no signed retention record accounts for it. Only the library, holding the key, can write that record.",
    }
    : {
      title: `Entry ${n} is missing or out of place`,
      detail: "Sequences must follow each other: 1, 2, 3. A row was deleted, or rows were moved.",
    },
  broken_link: (n) => ({
    title: `Entry ${n} does not point to the entry before it`,
    detail: "Its previousHash is not the hash of the previous row. The chain was cut or reordered here.",
  }),
  payload_hash_mismatch: (n) => ({
    title: `The payload of entry ${n} was changed`,
    detail: "Hashing the stored payload no longer gives the payloadHash recorded when the entry was written.",
  }),
  hash_mismatch: (n) => ({
    title: `A field of entry ${n} was changed`,
    detail: "Recomputing the hash from the stored fields gives a different value than the one stored.",
  }),
  invalid_signature: (n) => ({
    title: `Entry ${n} was resealed without the key`,
    detail: "The hashes were recomputed to hide an edit, but the signature cannot be redone without the secret key.",
  }),
  unknown_key: (n) => ({
    title: `Entry ${n} is signed with an unknown key`,
    detail: "The verifier does not know the key id written on this entry.",
  }),
  ledger_id_mismatch: (n) => ({
    title: `Entry ${n} belongs to another ledger`,
    detail: "Entries cannot be moved from one ledger to another: the ledger id is part of the hash.",
  }),
  unsupported_format: (n) => ({
    title: `Entry ${n} has an unknown format version`,
    detail: "This verifier only reads format 1.",
  }),
  truncated: (n) => ({
    title: `Entries are missing at the end, from entry ${n}`,
    detail: "The table alone looks complete. The checkpoint, kept outside the database, says the ledger was longer.",
  }),
  unrecorded_redaction: (n) => ({
    title: `The payload of entry ${n} was removed in the database`,
    detail: "It looks like an erasure, but erasures through the library leave a signed redaction record naming the entry, and there is none. Someone deleted the content to hide it.",
  }),
  checkpoint_mismatch: (n) => ({
    title: n ? `History was rewritten up to entry ${n}` : "The checkpoint is not valid",
    detail: n
      ? "Every hash and signature checks out, because whoever rewrote it had the key. But this entry differs from the checkpoint kept outside the database."
      : "The checkpoint signature or ledger id does not match.",
  }),
};

export function explainFailure(failure: Failure, sequence: number | null) {
  return FAILURES[failure](sequence);
}

/** A valid result can still hide removed rows at the end when no checkpoint was used. */
export function hiddenTruncation(demo: Demo): number[] {
  const last = demo.head?.sequence ?? 0;
  return demo.removed.filter((s) => s > last).sort((a, b) => a - b);
}
