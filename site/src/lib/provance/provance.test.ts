import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

import { type Checkpoint, type Json, type LedgerEntry, HmacKeys, canonicalCheckpoint, canonicalEnvelope,
  canonicalize, genesisHash, sha256Hex } from "./format";
import { createCheckpoint, seal, verify } from "./ledger";

const vectors = fileURLToPath(new URL("../../../../docs/test-vectors/", import.meta.url));
const v1 = JSON.parse(readFileSync(`${vectors}v1.json`, "utf8"));

describe("docs/test-vectors/v1.json", () => {
  const keys = new HmacKeys({ [v1.hmac.keyId]: v1.hmac.secretUtf8 });

  it("derives the genesis hash", () => {
    expect(genesisHash(v1.genesis.ledgerId)).toBe(v1.genesis.hash);
  });

  for (const vector of v1.entries) {
    it(vector.name, () => {
      const input = vector.input;
      const payloadJson = canonicalize(input.payload as Json);
      expect(payloadJson).toBe(vector.expected.canonicalPayload);
      const payloadHash = sha256Hex(payloadJson);
      expect(payloadHash).toBe(vector.expected.payloadHash);
      const envelope = { v: 1, ledgerId: input.ledgerId, sequence: input.sequence, id: input.id,
        timestamp: input.timestamp, eventType: input.eventType, payloadHash, previousHash: input.previousHash,
        keyId: input.keyId };
      expect(canonicalEnvelope(envelope)).toBe(vector.expected.canonicalEnvelope);
      const hash = sha256Hex(canonicalEnvelope(envelope));
      expect(hash).toBe(vector.expected.hash);
      expect(keys.sign(input.keyId, hash)).toBe(vector.expected.signature);
    });
  }

  it("signs the checkpoint", () => {
    const body = { v: 1, ...v1.checkpoint.input };
    expect(canonicalCheckpoint(body)).toBe(v1.checkpoint.expected.canonicalBody);
    const digest = sha256Hex(canonicalCheckpoint(body));
    expect(digest).toBe(v1.checkpoint.expected.digest);
    expect(keys.sign(body.keyId, digest)).toBe(v1.checkpoint.expected.signature);
  });
});

describe("ledger written by the Java library", () => {
  const dir = `${vectors}ledger-java/`;
  const entries: LedgerEntry[] = readFileSync(`${dir}entries.jsonl`, "utf8").split("\n").filter(Boolean)
    .map((line) => JSON.parse(line));
  const checkpoint: Checkpoint = JSON.parse(readFileSync(`${dir}checkpoint.json`, "utf8"));
  const keys = new HmacKeys({ k1: "provance-test-vector-key-0123456789abcdef",
    k2: "provance-test-vector-key-2-abcdef0123456789" });

  it("verifies, with key rotation, redaction and checkpoint", () => {
    const result = verify(entries, "cross-language-test", keys, checkpoint);
    expect(result.message).toBe("6 entries verified up to sequence 6.");
    expect(result.valid).toBe(true);
    expect(result.redacted).toBe(1);
  });

  it("detects tampering and truncation", () => {
    const edited = entries.map((e) => (e.sequence === 3 ? { ...e, eventType: "OTHER" } : e));
    expect(verify(edited, "cross-language-test", keys).failure).toBe("hash_mismatch");
    expect(verify(entries.slice(0, 4), "cross-language-test", keys, checkpoint).failure).toBe("truncated");
    expect(verify(entries, "cross-language-test", new HmacKeys({ k1: "provance-test-vector-key-0123456789abcdef" }))
      .failure).toBe("unknown_key");
  });
});

describe("demo ledger", () => {
  const keys = new HmacKeys({ k1: "demo-secret-key-0123456789abcdefghij" });
  const write = (n: number) => {
    const out: LedgerEntry[] = [];
    for (let i = 0; i < n; i++) out.push(seal(out.at(-1) ?? null, "demo", "k1", keys, "EVENT", { n: i, s: "é" }));
    return out;
  };

  it("round trips and reports every check", () => {
    const result = verify(write(4), "demo", keys);
    expect(result.valid).toBe(true);
    expect(result.reports.map((r) => r.checks.map((c) => c.name).join(","))).toEqual(
      Array(4).fill("sequence,format,link,payload,hash,signature"));
  });

  it("names each kind of tampering", () => {
    const base = write(4);
    const edited = base.map((e, i) => (i === 1 ? { ...e, payloadJson: '{"n":99,"s":"é"}' } : e));
    expect(verify(edited, "demo", keys)).toMatchObject({ failure: "payload_hash_mismatch", failedAtSequence: 2 });

    const resealed = edited.map((e, i) => (i === 1 ? { ...e, payloadHash: sha256Hex(e.payloadJson!) } : e));
    expect(verify(resealed, "demo", keys).failure).toBe("hash_mismatch");

    const coverUp = resealed.map((e, i) => {
      if (i !== 1) return e;
      const hash = sha256Hex(canonicalEnvelope(e));
      return { ...e, hash };
    });
    expect(verify(coverUp, "demo", keys).failure).toBe("invalid_signature");

    // Rewriting every hash after the edit so all the links match again: the first signature still gives it away.
    const rechained: LedgerEntry[] = [];
    for (const [i, e] of edited.entries()) {
      if (i === 0) {
        rechained.push(e);
        continue;
      }
      const draft = { ...e, previousHash: rechained[i - 1]!.hash,
        payloadHash: e.payloadJson === null ? e.payloadHash : sha256Hex(e.payloadJson) };
      rechained.push({ ...draft, hash: sha256Hex(canonicalEnvelope(draft)) });
    }
    expect(verify(rechained, "demo", keys)).toMatchObject({ failure: "invalid_signature", failedAtSequence: 2 });

    expect(verify([base[0]!, base[2]!, base[3]!], "demo", keys).failure).toBe("sequence_gap");
    const swapped = [base[0]!, base[2]!, base[1]!, base[3]!];
    expect(verify(swapped, "demo", keys).failure).toBe("sequence_gap");

    const redacted = base.map((e, i) => (i === 2 ? { ...e, payloadJson: null } : e));
    expect(verify(redacted, "demo", keys)).toMatchObject({ valid: true, redacted: 1 });
  });

  it("needs a checkpoint to see removed entries at the end", () => {
    const base = write(5);
    const checkpoint = createCheckpoint(base[4]!, "k1", keys);
    expect(verify(base.slice(0, 3), "demo", keys).valid).toBe(true);
    expect(verify(base.slice(0, 3), "demo", keys, checkpoint).failure).toBe("truncated");

    const rewritten = write(5); // the whole ledger rewritten by someone holding the key
    expect(verify(rewritten, "demo", keys).valid).toBe(true);
    expect(verify(rewritten, "demo", keys, checkpoint).failure).toBe("checkpoint_mismatch");
  });

  it("rejects values that are not valid JSON for the format", () => {
    expect(() => canonicalize(Number.NaN)).toThrow();
    expect(() => canonicalize("\ud800")).toThrow();
    expect(canonicalize({ b: [1, -0, 1e21, 0.1], a: " " })).toBe('{"a":" ","b":[1,0,1e+21,0.1]}');
  });
});
