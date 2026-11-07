// State of the guided demo: an in-memory ledger that the visitor writes, tampers with and verifies.

import { type LedgerComparison, compare, verifiesAlone } from "../provance/compare";
import { type Checkpoint, type Json, type LedgerEntry, HmacKeys, canonicalize, entryHash, genesisHash, sha256Hex } from "../provance/format";
import { type VerificationResult, REDACTION_EVENT, RETENTION_EVENT, createCheckpoint, redactionPayload, retentionPayload, seal,
  verify } from "../provance/ledger";

export const LEDGER_ID = "orders-api-demo";
export const KEY_ID = "k1";
export const SECRET = "demo-key-everyone-can-see-0123456789";
const keys = new HmacKeys({ [KEY_ID]: SECRET });

export interface Preset {
  eventType: string;
  payload: Json;
}

export const PRESETS: Preset[] = [
  { eventType: "ORDER_REFUNDED", payload: { actorId: "support-7", orderId: "A-1001", amount: "49.90", currency: "EUR" } },
  { eventType: "ROLE_GRANTED", payload: { actorId: "admin-2", user: "alice@example.com", role: "billing-reader" } },
  { eventType: "INVOICE_APPROVED", payload: { actorId: "cfo", invoiceId: "INV-2291", amount: "12800.00", currency: "EUR" } },
  { eventType: "EXPORT_DOWNLOADED", payload: { actorId: "analyst-4", dataset: "customers", rows: 18342 } },
  { eventType: "PASSWORD_RESET", payload: { actorId: "alice@example.com", method: "email-link" } },
  { eventType: "API_KEY_CREATED", payload: { actorId: "dev-11", keyName: "ci-deploy", scope: "write" } },
];

export type Edit = "payload" | "event" | "moved" | "resealed" | "rewritten" | "redacted" | "nulled";

const DAY = 24 * 3600 * 1000;

export interface Verification {
  result: VerificationResult;
  /** Number of rows already shown as checked by the animation. */
  shown: number;
  done: boolean;
  withCheckpoint: boolean;
}

export class Demo {
  entries = $state<LedgerEntry[]>([]);
  /** What the visitor changed directly in the table, by entry id. */
  edits = $state<Record<string, Edit[]>>({});
  removed = $state<number[]>([]);
  selected = $state<number>(0);
  checkpoint = $state<Checkpoint | null>(null);
  verification = $state<Verification | null>(null);
  /** 0.2 behavior: erasures are recorded, and verification requires the record. */
  recordsRedactions = $state(false);
  /** Second database, or null when no archive is configured. */
  archive = $state<LedgerEntry[] | null>(null);
  archiveEdits = $state<Record<string, Edit[]>>({});
  archiveProblem = $state<string | null>(null);
  comparison = $state<LedgerComparison | null>(null);
  /** First sequence still in the table after retention. */
  retainedFrom = $state(1);
  retentionReport = $state<{ ok: boolean; text: string } | null>(null);
  private archiveOriginal: LedgerEntry[] = [];
  private timer: ReturnType<typeof setInterval> | null = null;
  private next = 0;
  /** The table as written by the application, to undo tampering. */
  private original: LedgerEntry[] = [];

  readonly keyId = KEY_ID;
  readonly secret = SECRET;
  readonly ledgerId = LEDGER_ID;

  get head(): LedgerEntry | null {
    return this.entries.at(-1) ?? null;
  }

  get tampered(): boolean {
    return Object.keys(this.edits).some((id) => this.edits[id]!.some((e) => e !== "redacted"))
      || this.removed.length > 0 || Object.keys(this.archiveEdits).length > 0;
  }

  nextPreset(): Preset {
    return PRESETS[this.next % PRESETS.length]!;
  }

  // --- what the application does ---------------------------------------------------------------------------

  record(eventType: string, payload: Json, at: Date = new Date()): LedgerEntry {
    const entry = seal(this.head, LEDGER_ID, KEY_ID, keys, eventType, payload, at);
    this.entries.push(entry);
    this.original.push(entry);
    if (!eventType.startsWith("provance.")) this.next++;
    this.selected = this.entries.length - 1;
    this.invalidate();
    this.copyToArchive();
    return entry;
  }

  recordNext(count = 1): void {
    for (let i = 0; i < count; i++) {
      const p = this.nextPreset();
      this.record(p.eventType, p.payload);
    }
  }

  ensure(count: number): void {
    if (this.entries.length < count) this.recordNext(count - this.entries.length);
  }

  saveCheckpoint(): void {
    if (this.head) this.checkpoint = createCheckpoint(this.head, KEY_ID, keys);
  }

  /** Erasure through the library. From 0.2 it first appends a signed record naming the entry. */
  redact(index: number): void {
    const e = this.entries[index];
    if (!e || e.payloadJson === null) return;
    if (this.recordsRedactions) this.record(REDACTION_EVENT, redactionPayload(e));
    const erase = (list: LedgerEntry[]) => list.map((o) => (o.id === e.id ? { ...o, payloadJson: null } : o));
    this.entries = erase(this.entries);
    this.original = erase(this.original);
    if (this.archive) {
      this.archive = erase(this.archive);
      this.archiveOriginal = erase(this.archiveOriginal);
    }
    this.mark(e.id, "redacted", false);
    this.selected = index;
    this.invalidate();
  }

  // --- archive copy (0.2) ------------------------------------------------------------------------------------

  enableArchive(): void {
    if (this.archive) return;
    this.archive = [];
    this.archiveOriginal = [];
    this.copyToArchive();
  }

  /**
   * What the background copy does: each entry must continue the archive's chain and verify on its own, otherwise
   * copying stops there.
   */
  copyToArchive(): void {
    if (!this.archive) return;
    const head = this.archive.at(-1);
    let next = head ? head.sequence + 1 : 1;
    let previous = head ? head.hash : genesisHash(LEDGER_ID);
    for (const entry of this.entries) {
      if (entry.sequence < next) continue;
      let problem: string | null = null;
      if (entry.sequence !== next) problem = `Entry ${next} is missing from the main table but was never copied.`;
      else if (entry.previousHash !== previous) problem = `Entry ${entry.sequence} does not link to the archived entry ${next - 1}.`;
      else if (!verifiesAlone(entry, LEDGER_ID, keys)) problem = `Entry ${entry.sequence} does not verify: it was changed before it was copied.`;
      if (problem) {
        this.archiveProblem = problem;
        return;
      }
      this.archive.push(entry);
      this.archiveOriginal.push(entry);
      previous = entry.hash;
      next++;
    }
    this.archiveProblem = null;
  }

  /** The archive row an attack on the archive changes: the second application event that still has a payload. */
  get archiveTarget(): number {
    const rows = this.archive ?? [];
    return rows.findIndex((e, i) => i > 0 && e.payloadJson !== null && !e.eventType.startsWith("provance."));
  }

  /** Someone changes a row of the archive database. */
  tamperArchive(index = this.archiveTarget): void {
    const e = this.archive?.[index];
    if (!this.archive || !e?.payloadJson) return;
    this.archive[index] = { ...e, payloadJson: mutated(e.payloadJson) };
    this.archiveEdits[e.id] = ["payload"];
    this.invalidate();
  }

  compareCopies(): void {
    if (!this.archive) return;
    this.stop();
    this.verification = null;
    this.comparison = compare(this.entries, this.archive, LEDGER_ID, keys);
  }

  // --- retention (0.2) ---------------------------------------------------------------------------------------

  /** A ledger with an archive, four events recorded twelve days ago and three today. */
  prepareRetention(): void {
    const v2 = this.recordsRedactions;
    this.reset();
    this.recordsRedactions = v2;
    this.enableArchive();
    const start = Date.now() - 12 * DAY;
    for (let i = 0; i < 7; i++) {
      const p = this.nextPreset();
      this.record(p.eventType, p.payload, i < 4 ? new Date(start + i * 7 * 60_000) : new Date(Date.now() - (7 - i) * 60_000));
    }
    this.selected = 0;
  }

  /** What one retention run does (docs/RETENTION.md). Nothing is removed unless every check passes. */
  applyRetention(keepDays: number): void {
    this.stop();
    this.verification = null;
    this.comparison = null;
    const refuse = (text: string): void => {
      this.retentionReport = { ok: false, text };
    };
    if (!verify(this.entries, LEDGER_ID, keys).valid) return refuse("Nothing removed: the main table does not verify, so the evidence stays where it is.");
    const cutoff = Date.now() - keepDays * DAY;
    const old = this.entries.filter((e) => Date.parse(e.timestamp) < cutoff);
    const boundary = old.at(-1);
    if (!boundary || boundary === this.entries[0]) return refuse(`Nothing removed: no entry is older than ${keepDays} days, apart from the first one kept.`);
    if (!this.archive) return refuse("Nothing removed: there is no archive holding a copy.");
    this.copyToArchive();
    const before = this.entries.filter((e) => e.sequence < boundary.sequence);
    const same = before.every((e) => JSON.stringify(this.archive!.find((a) => a.sequence === e.sequence)) === JSON.stringify(e));
    if (!same) return refuse("Nothing removed: the archive does not hold an identical copy of the old entries.");

    this.record(RETENTION_EVENT, retentionPayload(boundary));
    this.entries = this.entries.filter((e) => e.sequence >= boundary.sequence);
    this.original = this.original.filter((e) => e.sequence >= boundary.sequence);
    this.retainedFrom = boundary.sequence;
    this.saveCheckpoint();
    this.selected = 0;
    this.retentionReport = {
      ok: true,
      text: `Removed ${before.length} entries (1 to ${boundary.sequence - 1}). Entry ${boundary.sequence} stays as the first one, `
        + `the retention record ${this.head!.sequence} names it, and a new checkpoint was saved.`,
    };
    this.invalidate();
  }

  // --- what someone with database access does ------------------------------------------------------------------

  editPayload(index: number, json: string): string | null {
    const e = this.entries[index];
    if (!e) return null;
    let canonical: string;
    try {
      canonical = canonicalize(JSON.parse(json) as Json);
    } catch (err) {
      return `Not valid JSON: ${(err as Error).message}`;
    }
    if (canonical === e.payloadJson) return null;
    this.entries[index] = { ...e, payloadJson: canonical };
    this.mark(e.id, "payload");
    return null;
  }

  editEventType(index: number, eventType: string): void {
    const e = this.entries[index];
    if (!e || !eventType.trim() || eventType === e.eventType) return;
    this.entries[index] = { ...e, eventType: eventType.trim() };
    this.mark(e.id, "event");
  }

  /** Changes the first amount (or the first field) of a payload, as a quick attack. */
  changeAmount(index: number): void {
    const e = this.entries[index];
    if (!e?.payloadJson) return;
    this.editPayload(index, mutated(e.payloadJson));
  }

  /** UPDATE ... SET payload = NULL: looks like an erasure, but without the record the library writes. */
  nullPayload(index: number): void {
    const e = this.entries[index];
    if (!e || e.payloadJson === null) return;
    this.entries[index] = { ...e, payloadJson: null };
    this.mark(e.id, "nulled");
  }

  remove(index: number): void {
    const e = this.entries[index];
    if (!e) return;
    this.entries.splice(index, 1);
    this.removed.push(e.sequence);
    this.selected = Math.max(0, Math.min(this.selected, this.entries.length - 1));
    this.invalidate();
  }

  removeFirst(count: number): void {
    for (let i = 0; i < count && this.entries.length > 0; i++) this.remove(0);
  }

  removeLast(count: number): void {
    for (let i = 0; i < count && this.entries.length > 0; i++) this.remove(this.entries.length - 1);
  }

  swap(a: number, b: number): void {
    const ea = this.entries[a];
    const eb = this.entries[b];
    if (!ea || !eb) return;
    this.entries[a] = eb;
    this.entries[b] = ea;
    this.mark(ea.id, "moved");
    this.mark(eb.id, "moved");
  }

  /** Recomputes payloadHash and hash after an edit. The signature cannot be redone without the key. */
  coverTracks(index: number): void {
    const e = this.entries[index];
    if (!e) return;
    const payloadHash = e.payloadJson === null ? e.payloadHash : sha256Hex(e.payloadJson);
    const draft = { ...e, payloadHash };
    this.entries[index] = { ...draft, hash: entryHash(draft) };
    this.mark(e.id, "resealed");
  }

  /**
   * Recomputes every hash from `index` to the end so the chain links up again. The signatures stay the old ones:
   * without the key they cannot be redone, and that is what verification catches.
   */
  rechainWithoutKey(index: number): void {
    for (let i = index; i < this.entries.length; i++) {
      const e = this.entries[i]!;
      const previousHash = i === index ? e.previousHash : this.entries[i - 1]!.hash;
      const payloadHash = e.payloadJson === null ? e.payloadHash : sha256Hex(e.payloadJson);
      const draft = { ...e, payloadHash, previousHash };
      this.entries[i] = { ...draft, hash: entryHash(draft) };
      this.mark(e.id, "resealed");
    }
  }

  /** Someone holding the key rewrites the whole table with different content. Only a checkpoint catches it. */
  rewriteWithKey(): void {
    const rewritten: LedgerEntry[] = [];
    for (const [i, e] of this.entries.entries()) {
      let payload = (e.payloadJson === null ? { redacted: true } : JSON.parse(e.payloadJson)) as Record<string, Json>;
      if (i === 0) payload = { ...payload, amount: "4.90" };
      const at = new Date(e.timestamp);
      const entry = seal(rewritten.at(-1) ?? null, LEDGER_ID, KEY_ID, keys, e.eventType, payload, at);
      rewritten.push(entry);
    }
    this.entries = rewritten;
    for (const e of rewritten) this.mark(e.id, "rewritten");
  }

  /** Puts back the table as the application wrote it. */
  restore(): void {
    this.entries = this.original.map((e) => ({ ...e }));
    if (this.archive) this.archive = this.archiveOriginal.map((e) => ({ ...e }));
    this.archiveEdits = {};
    this.retentionReport = null;
    const kept: Record<string, Edit[]> = {};
    for (const [id, list] of Object.entries(this.edits)) if (list.includes("redacted")) kept[id] = ["redacted"];
    this.edits = kept;
    this.removed = [];
    this.selected = Math.min(this.selected, Math.max(0, this.entries.length - 1));
    this.invalidate();
  }

  reset(): void {
    this.stop();
    this.entries = [];
    this.original = [];
    this.edits = {};
    this.removed = [];
    this.checkpoint = null;
    this.verification = null;
    this.selected = 0;
    this.next = 0;
    this.archive = null;
    this.archiveOriginal = [];
    this.archiveEdits = {};
    this.archiveProblem = null;
    this.comparison = null;
    this.retainedFrom = 1;
    this.retentionReport = null;
  }

  // --- verification -------------------------------------------------------------------------------------------

  verify(withCheckpoint = false): void {
    this.stop();
    const result = verify(this.entries, LEDGER_ID, keys, {
      checkpoint: withCheckpoint ? this.checkpoint : null,
      acceptUnrecordedRedactions: !this.recordsRedactions,
    });
    this.comparison = null;
    const rows = result.reports.filter((r) => r.index >= 0).length;
    this.verification = { result, shown: 0, done: false, withCheckpoint };
    const reduced = typeof matchMedia === "function" && matchMedia("(prefers-reduced-motion: reduce)").matches;
    if (reduced || rows === 0) {
      this.verification = { ...this.verification, shown: rows, done: true };
      this.focusFailure();
      return;
    }
    this.timer = setInterval(() => {
      const v = this.verification;
      if (!v) return this.stop();
      const shown = v.shown + 1;
      this.verification = { ...v, shown, done: shown >= rows };
      if (shown >= rows) {
        this.stop();
        this.focusFailure();
      }
    }, 280);
  }

  private focusFailure(): void {
    const index = this.verification?.result.failedIndex;
    if (index !== null && index !== undefined) this.selected = index;
  }

  private stop(): void {
    if (this.timer) clearInterval(this.timer);
    this.timer = null;
  }

  private mark(id: string, edit: Edit, invalidate = true): void {
    const list = this.edits[id] ?? [];
    if (!list.includes(edit)) this.edits[id] = [...list, edit];
    if (invalidate) this.invalidate();
  }

  private invalidate(): void {
    this.stop();
    this.verification = null;
    this.comparison = null;
  }
}

/** The payload with its amount (or its actor) changed. */
function mutated(payloadJson: string): string {
  const payload = JSON.parse(payloadJson) as Record<string, Json>;
  if (typeof payload.amount === "string") payload.amount = payload.amount.startsWith("4.") ? "49.90" : "4.90";
  else payload.actorId = "someone-else";
  return JSON.stringify(payload);
}

/** Twelve well separated hues. */
const HUES = [4, 28, 48, 88, 140, 168, 192, 212, 236, 268, 298, 330];

/** Two hues derived from a hash, so the same hash always gets the same pair of colors. */
export function hashColors(hash: string): [number, number] {
  const a = parseInt(hash.slice(0, 2), 16) || 0;
  const b = parseInt(hash.slice(2, 4), 16) || 0;
  const first = HUES[a % HUES.length]!;
  let second = HUES[b % HUES.length]!;
  if (second === first) second = HUES[(b + 5) % HUES.length]!;
  return [first, second];
}
