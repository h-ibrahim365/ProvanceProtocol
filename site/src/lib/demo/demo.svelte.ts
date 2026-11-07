// State of the guided demo: an in-memory ledger that the visitor writes, tampers with and verifies.

import { type Checkpoint, type Json, type LedgerEntry, HmacKeys, canonicalize, entryHash, sha256Hex } from "../provance/format";
import { type VerificationResult, createCheckpoint, seal, verify } from "../provance/ledger";

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

export type Edit = "payload" | "event" | "moved" | "resealed" | "rewritten" | "redacted";

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
    return Object.keys(this.edits).length > 0 || this.removed.length > 0;
  }

  nextPreset(): Preset {
    return PRESETS[this.next % PRESETS.length]!;
  }

  // --- what the application does ---------------------------------------------------------------------------

  record(eventType: string, payload: Json): void {
    const entry = seal(this.head, LEDGER_ID, KEY_ID, keys, eventType, payload);
    this.entries.push(entry);
    this.original.push(entry);
    this.next++;
    this.selected = this.entries.length - 1;
    this.invalidate();
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

  redact(index: number): void {
    const e = this.entries[index];
    if (!e || e.payloadJson === null) return;
    this.entries[index] = { ...e, payloadJson: null };
    this.original = this.original.map((o) => (o.id === e.id ? { ...o, payloadJson: null } : o));
    this.mark(e.id, "redacted", false);
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
    const payload = JSON.parse(e.payloadJson) as Record<string, Json>;
    if (typeof payload.amount === "string") payload.amount = payload.amount.startsWith("4.") ? "49.90" : "4.90";
    else payload.actorId = "someone-else";
    this.editPayload(index, JSON.stringify(payload));
  }

  remove(index: number): void {
    const e = this.entries[index];
    if (!e) return;
    this.entries.splice(index, 1);
    this.removed.push(e.sequence);
    this.selected = Math.max(0, Math.min(this.selected, this.entries.length - 1));
    this.invalidate();
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
  }

  // --- verification -------------------------------------------------------------------------------------------

  verify(withCheckpoint = false): void {
    this.stop();
    const result = verify(this.entries, LEDGER_ID, keys, withCheckpoint ? this.checkpoint : null);
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
  }
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
