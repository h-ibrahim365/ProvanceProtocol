// The guided tour. Each step prepares what it needs, so visitors can jump to any step.

import type { Demo } from "./demo.svelte";

export type Focus = "payload" | "link" | "seal" | "verify" | "checkpoint" | "redact" | "archive" | "retention" | "code";

export interface Action {
  label: string;
  variant?: "primary" | "quiet" | "danger";
  run: (demo: Demo) => void;
  disabled?: (demo: Demo) => boolean;
}

export interface Step {
  id: string;
  title: string;
  body: string[];
  note?: string;
  focus: Focus;
  /** Snippet shown with the step, from the code samples. */
  code?: CodeSample;
  enter?: (demo: Demo) => void;
  actions: Action[];
  /** Tamper tools on each row of the table. */
  rowTools?: boolean;
}

export type CodeSample = "record" | "verify" | "checkpoint" | "redact" | "setup" | "archive" | "retention";

export const STEPS_V1: Step[] = [
  {
    id: "record",
    title: "Record an event",
    focus: "payload",
    code: "record",
    body: [
      "Your application records what happened: who did what, and when.",
      "PROVANCE writes the event as canonical JSON (keys sorted, one exact spelling for every value) and hashes it with SHA-256. That hash is the payloadHash.",
    ],
    actions: [{ label: "Record an event", run: (d) => d.recordNext() }],
    enter: (d) => d.restore(),
  },
  {
    id: "chain",
    title: "Chain the entries",
    focus: "link",
    body: [
      "Each new entry stores the hash of the entry before it, in previousHash. The first one points to a genesis hash derived from the ledger id.",
      "The colors show the links: an entry's hash has the same color as the previousHash of the next row. Change one entry, and its link to the next one breaks.",
    ],
    actions: [{ label: "Record three more events", run: (d) => d.recordNext(3) }],
    enter: (d) => {
      d.restore();
      d.ensure(1);
    },
  },
  {
    id: "seal",
    title: "Seal every entry",
    focus: "seal",
    code: "setup",
    body: [
      "The entry hash covers every field: sequence, time, event type, payloadHash and previousHash.",
      "The application then signs that hash with a secret key, using HMAC-SHA256. Without the key, nobody can produce a matching signature.",
    ],
    note: "Here the key is shown so you can follow the computation. In production it lives in a secret store or a KMS, never in the database.",
    actions: [],
    enter: (d) => {
      d.restore();
      d.ensure(4);
    },
  },
  {
    id: "tamper",
    title: "Tamper with the table",
    focus: "payload",
    rowTools: true,
    body: [
      "You now have direct access to the database, like an administrator or an intruder. Nothing stops you from changing it.",
      "Pick an attack, or edit a row yourself with the tools on each line. Then go to the next step to see what verification finds.",
    ],
    actions: [
      { label: "Lower the refund on #1", variant: "danger", run: (d) => d.changeAmount(0) },
      { label: "Delete row #3", variant: "danger", run: (d) => d.remove(2), disabled: (d) => d.entries.length < 3 },
      { label: "Swap rows #2 and #3", variant: "danger", run: (d) => d.swap(1, 2), disabled: (d) => d.entries.length < 3 },
      {
        label: "Edit #1 and recompute every hash after it",
        variant: "danger",
        run: (d) => {
          d.changeAmount(0);
          d.rechainWithoutKey(0);
        },
      },
      { label: "Undo my changes", variant: "quiet", run: (d) => d.restore(), disabled: (d) => !d.tampered },
    ],
    enter: (d) => d.ensure(4),
  },
  {
    id: "verify",
    title: "Verify",
    focus: "verify",
    code: "verify",
    rowTools: true,
    body: [
      "The verifier reads the table from the first entry. For each one it checks the sequence, the link to the previous entry, the payload, the hash and the signature.",
      "It stops at the first entry that fails and says what is wrong with it.",
    ],
    actions: [
      { label: "Run verification", run: (d) => d.verify() },
      { label: "Undo my changes", variant: "quiet", run: (d) => d.restore(), disabled: (d) => !d.tampered },
    ],
    enter: (d) => d.ensure(4),
  },
  {
    id: "checkpoint",
    title: "Cut off the end",
    focus: "checkpoint",
    code: "checkpoint",
    body: [
      "Delete the last entries and what remains is a shorter chain that is still perfectly valid. The same goes for someone who holds the key and rewrites everything.",
      "A checkpoint closes that gap: a signed note saying the ledger was at entry N with hash H. Keep it where the database administrators cannot write, then verify against it.",
    ],
    actions: [
      { label: "Save a checkpoint", run: (d) => d.saveCheckpoint(), disabled: (d) => d.entries.length === 0 },
      { label: "Delete the last two rows", variant: "danger", run: (d) => d.removeLast(2), disabled: (d) => d.entries.length < 3 },
      { label: "Rewrite everything with the key", variant: "danger", run: (d) => d.rewriteWithKey() },
      { label: "Verify without the checkpoint", variant: "quiet", run: (d) => d.verify(false) },
      { label: "Verify with the checkpoint", run: (d) => d.verify(true), disabled: (d) => !d.checkpoint },
      { label: "Undo my changes", variant: "quiet", run: (d) => d.restore(), disabled: (d) => !d.tampered },
    ],
    enter: (d) => {
      d.restore();
      d.checkpoint = null;
      d.ensure(5);
    },
  },
  {
    id: "redact",
    title: "Erase personal data",
    focus: "redact",
    code: "redact",
    body: [
      "A ledger only grows, but people can ask for their personal data to be erased (GDPR).",
      "Only the payloadHash is part of the chain, so the payload itself can be removed. The entry, its type and its time remain, and the ledger still verifies.",
    ],
    actions: [
      {
        label: "Erase the payload of #2",
        run: (d) => d.redact(1),
        disabled: (d) => !d.entries[1] || d.entries[1].payloadJson === null,
      },
      { label: "Run verification", variant: "quiet", run: (d) => d.verify() },
    ],
    enter: (d) => {
      d.restore();
      d.ensure(3);
    },
  },
  {
    id: "use",
    title: "Use it in your application",
    focus: "code",
    code: "record",
    body: [
      "That is the whole idea. The libraries do the same thing against your own database, with several application instances writing to one ledger, retries, and automatic checkpoints.",
      "Same format in .NET, Java and Python: a ledger written in one can be verified in the others.",
    ],
    actions: [],
    enter: (d) => d.ensure(1),
  },
];

const v1 = (id: string): Step => STEPS_V1.find((s) => s.id === id)!;

/** 0.2: the same first steps, then recorded erasures, the archive copy with its comparison, and retention. */
export const STEPS_V2: Step[] = [
  v1("record"),
  v1("chain"),
  v1("seal"),
  v1("tamper"),
  v1("verify"),
  v1("checkpoint"),
  {
    id: "redact",
    title: "Erase personal data",
    focus: "redact",
    code: "redact",
    body: [
      "People can ask for their personal data to be erased (GDPR). Only the payloadHash is part of the chain, so the payload itself can be removed and the ledger still verifies.",
      "From 0.2, the library first appends a signed provance.redaction record naming the erased entry. Someone who sets a payload to NULL directly in the database writes no such record, and verification reports it.",
    ],
    actions: [
      {
        label: "Erase the payload of #2",
        run: (d) => d.redact(1),
        disabled: (d) => !d.entries[1] || d.entries[1].payloadJson === null,
      },
      {
        label: "Set the payload of #3 to NULL in the database",
        variant: "danger",
        run: (d) => d.nullPayload(2),
        disabled: (d) => !d.entries[2] || d.entries[2].payloadJson === null,
      },
      { label: "Run verification", variant: "quiet", run: (d) => d.verify() },
      { label: "Undo my changes", variant: "quiet", run: (d) => d.restore(), disabled: (d) => !d.tampered },
    ],
    enter: (d) => {
      d.restore();
      d.ensure(3);
    },
  },
  {
    id: "archive",
    title: "Keep a copy elsewhere",
    focus: "archive",
    code: "archive",
    rowTools: true,
    body: [
      "Verification says that an entry was changed, not what it said before: the ledger only keeps a hash of it.",
      "From 0.2, every entry is also copied, in the background, to a second database where the application can only insert. Comparing the two copies gives, field by field, the original value and the current one.",
    ],
    note: "Each entry is checked before it is copied, so a row changed in the main table before its copy is not propagated.",
    actions: [
      { label: "Lower the refund on #1", variant: "danger", run: (d) => d.changeAmount(0), disabled: (d) => !d.entries[0]?.payloadJson },
      { label: "Change an entry in the archive", variant: "danger", run: (d) => d.tamperArchive(), disabled: (d) => d.archiveTarget < 0 },
      { label: "Compare the two copies", run: (d) => d.compareCopies() },
      { label: "Run verification", variant: "quiet", run: (d) => d.verify() },
      { label: "Record an event", variant: "quiet", run: (d) => d.recordNext() },
      { label: "Undo my changes", variant: "quiet", run: (d) => d.restore(), disabled: (d) => !d.tampered },
    ],
    enter: (d) => {
      d.restore();
      d.ensure(4);
      d.enableArchive();
    },
  },
  {
    id: "retention",
    title: "Keep the main table small",
    focus: "retention",
    code: "retention",
    body: [
      "A ledger only grows. Retention removes old entries from the main table once the archive holds an identical copy of them, and the ledger still verifies.",
      "Before deleting, the library appends a signed provance.retention record naming the first entry it keeps. Deleting the first rows without that record, and so without the key, is still detected.",
    ],
    note: "Here the first four events were recorded twelve days ago, and the setting keeps seven days.",
    actions: [
      { label: "Remove entries older than 7 days", run: (d) => d.applyRetention(7), disabled: (d) => d.retainedFrom > 1 },
      { label: "Run verification", variant: "quiet", run: (d) => d.verify() },
      { label: "Compare the two copies", variant: "quiet", run: (d) => d.compareCopies() },
      {
        label: "Instead, delete #1 and #2 directly",
        variant: "danger",
        run: (d) => d.removeFirst(2),
        disabled: (d) => d.retainedFrom > 1 || d.entries[0]?.sequence !== 1,
      },
      { label: "Start again", variant: "quiet", run: (d) => d.prepareRetention() },
    ],
    enter: (d) => d.prepareRetention(),
  },
  {
    id: "use",
    title: "Use it in your application",
    focus: "code",
    code: "setup",
    body: [
      "The libraries do all of this against your own database: several application instances writing to one ledger, retries, automatic checkpoints, the archive copy and retention.",
      "One application can also hold several ledgers, for example one per tenant, or one for audit and one for billing, each with its own key, table and archive. Same format in .NET, Java and Python.",
    ],
    actions: [],
    enter: (d) => d.ensure(1),
  },
];
