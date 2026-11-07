import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

import { type Checkpoint, type Json, type LedgerEntry, HmacKeys, canonicalCheckpoint, canonicalEnvelope,
  canonicalize, genesisHash, sha256Hex } from "./format";
import { compare } from "./compare";
import { REDACTION_EVENT, RETENTION_EVENT, createCheckpoint, redactionPayload, retentionPayload, seal, verify } from "./ledger";

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
    const result = verify(entries, "cross-language-test", keys, { checkpoint, acceptUnrecordedRedactions: true });
    expect(result.message).toBe("6 entries verified up to sequence 6.");
    expect(result.valid).toBe(true);
    expect(result.redacted).toBe(1);
  });

  it("detects tampering and truncation", () => {
    const edited = entries.map((e) => (e.sequence === 3 ? { ...e, eventType: "OTHER" } : e));
    expect(verify(edited, "cross-language-test", keys).failure).toBe("hash_mismatch");
    expect(verify(entries.slice(0, 4), "cross-language-test", keys, { checkpoint, acceptUnrecordedRedactions: true }).failure)
      .toBe("truncated");
    expect(verify(entries, "cross-language-test", new HmacKeys({ k1: "provance-test-vector-key-0123456789abcdef" }))
      .failure).toBe("unknown_key");
  });

  it("reports its erasure, made by 0.1 without a record, unless told to accept it", () => {
    expect(verify(entries, "cross-language-test", keys)).toMatchObject({ failure: "unrecorded_redaction", failedAtSequence: 4 });
  });
});

for (const [language, ledgerId, count] of [["python", "cross-language-python", 7], ["dotnet", "cross-language-dotnet", 7]] as const) {
  describe(`ledger written by the ${language} library`, () => {
    const dir = `${vectors}ledger-${language}/`;
    const entries: LedgerEntry[] = readFileSync(`${dir}entries.jsonl`, "utf8").split("\n").filter(Boolean)
      .map((line) => JSON.parse(line));
    const checkpoint: Checkpoint = JSON.parse(readFileSync(`${dir}checkpoint.json`, "utf8"));
    const keys = new HmacKeys({ k1: "provance-test-vector-key-0123456789abcdef",
      k2: "provance-test-vector-key-2-abcdef0123456789" });

    it("verifies, with key rotation, redaction and checkpoint", () => {
      const result = verify(entries, ledgerId, keys, checkpoint);
      expect(result.message).toBe(`${count} entries verified up to sequence ${count}.`);
      expect(result.valid).toBe(true);
      expect(result.redacted).toBe(1);
    });
  });
}

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
    expect(verify(redacted, "demo", keys)).toMatchObject({ failure: "unrecorded_redaction", failedAtSequence: 3, failedIndex: 2 });
    expect(verify(redacted, "demo", keys, { acceptUnrecordedRedactions: true })).toMatchObject({ valid: true, redacted: 1 });
  });

  it("accepts an erasure named by a signed redaction record", () => {
    const base = write(3);
    base.push(seal(base.at(-1)!, "demo", "k1", keys, REDACTION_EVENT, redactionPayload(base[1]!)));
    const erased = base.map((e, i) => (i === 1 ? { ...e, payloadJson: null } : e));
    expect(verify(erased, "demo", keys)).toMatchObject({ valid: true, redacted: 1 });
    // the record names entry 2, not entry 3
    const wrong = erased.map((e, i) => (i === 2 ? { ...e, payloadJson: null } : e));
    expect(verify(wrong, "demo", keys).failure).toBe("unrecorded_redaction");
  });

  it("accepts a table shortened by retention, and only then", () => {
    const base = write(6);
    base.push(seal(base.at(-1)!, "demo", "k1", keys, RETENTION_EVENT, retentionPayload(base[3]!)));
    const kept = base.slice(3);
    const result = verify(kept, "demo", keys);
    expect(result).toMatchObject({ valid: true, firstSequence: 4, entriesVerified: 4 });
    expect(result.message).toContain("removed by retention");

    expect(verify(write(6).slice(2), "demo", keys)).toMatchObject({ failure: "sequence_gap", failedAtSequence: 1 });
    expect(verify(base.slice(2), "demo", keys)).toMatchObject({ failure: "sequence_gap", failedAtSequence: 1 });

    const old = createCheckpoint(base[1]!, "k1", keys);
    expect(verify(kept, "demo", keys, old).failure).toBe("checkpoint_mismatch");
    expect(verify(kept, "demo", keys, createCheckpoint(base.at(-1)!, "k1", keys)).valid).toBe(true);
  });

  it("compares two copies field by field", () => {
    const archive = write(4);
    const edited = archive.map((e, i) => (i === 1 ? { ...e, payloadJson: '{"n":99,"s":"é"}' } : e));
    const comparison = compare(edited, archive, "demo", keys);
    expect(comparison.identical).toBe(false);
    expect(comparison.differences).toEqual([{
      sequence: 2, kind: "changed", original: "archive", eventType: "EVENT",
      fields: [{ field: "payload.n", archiveValue: "1", primaryValue: "99" }],
    }]);

    expect(compare(archive.slice(0, 3), archive, "demo", keys).differences[0]).toMatchObject({ sequence: 4, kind: "missing_from_primary" });
    expect(compare(archive, archive.slice(0, 2), "demo", keys)).toMatchObject({ identical: true, notYetCopied: 2 });

    const full = [...archive, seal(archive.at(-1)!, "demo", "k1", keys, RETENTION_EVENT, retentionPayload(archive[2]!))];
    expect(compare(full.slice(2), full, "demo", keys)).toMatchObject({ identical: true, removedByRetention: 2 });
    expect(compare(archive.slice(2), archive, "demo", keys).differences[0]).toMatchObject({
      sequence: 1, kind: "missing_from_primary", throughSequence: 2 });
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
