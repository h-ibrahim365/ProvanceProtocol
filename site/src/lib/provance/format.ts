// PROVANCE ledger format v1 (docs/SPEC.md), implemented for the browser.
// Same bytes as the .NET, Java and Python libraries: checked against docs/test-vectors in CI.

import { hmac } from "@noble/hashes/hmac.js";
import { sha256 } from "@noble/hashes/sha2.js";
import { bytesToHex, hexToBytes, utf8ToBytes } from "@noble/hashes/utils.js";

export const FORMAT_VERSION = 1;

export type Json = null | boolean | number | string | Json[] | { [key: string]: Json };

export interface LedgerEntry {
  v: number;
  ledgerId: string;
  sequence: number;
  id: string;
  timestamp: string;
  eventType: string;
  /** Canonical JSON of the payload, or null once redacted. */
  payloadJson: string | null;
  payloadHash: string;
  previousHash: string;
  keyId: string;
  hash: string;
  signature: string;
}

export interface Checkpoint {
  v: number;
  ledgerId: string;
  sequence: number;
  hash: string;
  createdAt: string;
  keyId: string;
  signature: string;
}

/** RFC 8785 (JCS). Numbers use the ECMAScript number to string conversion, which is what the RFC specifies. */
export function canonicalize(value: Json): string {
  if (value === null || typeof value === "boolean") return String(value);
  if (typeof value === "number") {
    if (!Number.isFinite(value)) throw new Error("NaN and Infinity are not valid JSON numbers.");
    return JSON.stringify(value); // -0 becomes "0"
  }
  if (typeof value === "string") {
    if (!value.isWellFormed()) throw new Error("Strings must be valid Unicode (lone surrogate).");
    return JSON.stringify(value);
  }
  if (Array.isArray(value)) return `[${value.map(canonicalize).join(",")}]`;
  const keys = Object.keys(value).sort(); // UTF-16 code unit order, as RFC 8785 requires
  return `{${keys.map((k) => `${canonicalize(k)}:${canonicalize(value[k] as Json)}`).join(",")}}`;
}

export function sha256Hex(text: string): string {
  return bytesToHex(sha256(utf8ToBytes(text)));
}

export function genesisHash(ledgerId: string): string {
  return sha256Hex(`provance:genesis:v1:${ledgerId}`);
}

type Envelope = Omit<LedgerEntry, "payloadJson" | "hash" | "signature">;

export function canonicalEnvelope(entry: Envelope): string {
  return canonicalize({
    v: entry.v,
    ledgerId: entry.ledgerId,
    sequence: entry.sequence,
    id: entry.id,
    timestamp: entry.timestamp,
    eventType: entry.eventType,
    payloadHash: entry.payloadHash,
    previousHash: entry.previousHash,
    keyId: entry.keyId,
  });
}

export function entryHash(entry: Envelope): string {
  return sha256Hex(canonicalEnvelope(entry));
}

export function canonicalCheckpoint(c: Omit<Checkpoint, "signature">): string {
  return canonicalize({
    v: c.v,
    type: "checkpoint",
    ledgerId: c.ledgerId,
    sequence: c.sequence,
    hash: c.hash,
    createdAt: c.createdAt,
    keyId: c.keyId,
  });
}

/** HMAC-SHA256 signer and verifier. Secrets are UTF-8 strings, as in the libraries' configuration. */
export class HmacKeys {
  private readonly keys: Map<string, Uint8Array>;

  constructor(keys: Record<string, string>) {
    this.keys = new Map(Object.entries(keys).map(([id, secret]) => [id, utf8ToBytes(secret)]));
  }

  has(keyId: string): boolean {
    return this.keys.has(keyId);
  }

  /** Signs the 32 raw bytes of a hex digest. */
  sign(keyId: string, hexDigest: string): string {
    const key = this.keys.get(keyId);
    if (!key) throw new Error(`Unknown key '${keyId}'.`);
    return bytesToHex(hmac(sha256, key, hexToBytes(hexDigest)));
  }

  verify(keyId: string, hexDigest: string, signature: string): "valid" | "invalid" | "unknown-key" {
    if (!this.keys.has(keyId)) return "unknown-key";
    if (!/^[0-9a-f]{64}$/.test(hexDigest) || !/^(?:[0-9a-f]{2})+$/.test(signature)) return "invalid";
    return constantTimeEqual(this.sign(keyId, hexDigest), signature) ? "valid" : "invalid";
  }
}

function constantTimeEqual(a: string, b: string): boolean {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}

/** Random lowercase UUID v4. Uses getRandomValues, which also works outside secure contexts. */
export function newId(): string {
  const b = crypto.getRandomValues(new Uint8Array(16));
  b[6] = ((b[6] ?? 0) & 0x0f) | 0x40;
  b[8] = ((b[8] ?? 0) & 0x3f) | 0x80;
  const h = bytesToHex(b);
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
}

/** UTC time with millisecond precision: yyyy-MM-ddTHH:mm:ss.fffZ. */
export function timestamp(date: Date = new Date()): string {
  return date.toISOString();
}
