# PROVANCE ledger format, version 1

This document defines the bytes that PROVANCE hashes and signs. Any implementation that follows it
produces the same hashes as the .NET, Java and Python libraries. The file `docs/test-vectors/v1.json` contains reference
values, and `docs/test-vectors/verify.py` recomputes them without any .NET code.

The format version (`v = 1`) is independent from the package version. Package 0.1.0 is the first to write format 1.

## 1. Goals and limits

A ledger is an append only sequence of entries. Each entry commits to the previous one, so:

* changing a stored field of any entry is detected;
* deleting, inserting or reordering entries in the middle is detected;
* deleting entries at the end, or rewriting the whole ledger with the signing key, is detected **only**
  when the verifier holds a checkpoint stored outside the reach of whoever controls the ledger (section 7).

PROVANCE does not prevent deletion. It makes tampering visible.

## 2. Notation

* `SHA256(x)` is SHA-256 of the byte string `x`. `hex(x)` is lowercase hexadecimal.
* `UTF8(s)` is the UTF-8 encoding of the string `s`.
* `JCS(x)` is the JSON Canonicalization Scheme of RFC 8785 applied to the JSON value `x`.
* Every hash and signature in the format is lowercase hexadecimal.

## 3. Entry

| Field          | JSON type | Description |
| :------------- | :-------- | :---------- |
| `v`            | number    | Format version, `1`. |
| `ledgerId`     | string    | Identifier of the ledger. Letters, digits and `. _ : -`, 1 to 128 characters. |
| `sequence`     | number    | Position in the chain. Starts at 1, increments by 1, at most 2^53 - 1. |
| `id`           | string    | UUID, lowercase, `8-4-4-4-12` form. |
| `timestamp`    | string    | UTC time in the form `yyyy-MM-ddTHH:mm:ss.fffZ` (millisecond precision). |
| `eventType`    | string    | Application defined, 1 to 200 characters. |
| `payloadHash`  | string    | `hex(SHA256(UTF8(canonicalPayload)))`. |
| `previousHash` | string    | `hash` of the entry with `sequence - 1`, or the genesis hash for sequence 1. |
| `keyId`        | string    | Identifier of the key that produced `signature`. |
| `hash`         | string    | See 3.2. |
| `signature`    | string    | See 3.3. |

The payload itself is stored next to the entry as `canonicalPayload`, or removed (see section 6).

### 3.1 Canonical payload

`canonicalPayload = JCS(payload)`, where `payload` is any JSON value. Consequences of RFC 8785 worth knowing:

* numbers are IEEE 754 doubles: integers above 2^53 and decimals with many digits lose precision, so
  amounts and large identifiers should be strings;
* object member names must be unique;
* strings must be valid Unicode.

### 3.2 Envelope and hash

The envelope is the JSON object made of the fields `v`, `ledgerId`, `sequence`, `id`, `timestamp`,
`eventType`, `payloadHash`, `previousHash` and `keyId`, with the values described above.

```
canonicalEnvelope = JCS(envelope)
hash              = hex(SHA256(UTF8(canonicalEnvelope)))
```

Because JCS sorts members, the canonical envelope always has the shape

```
{"eventType":"…","id":"…","keyId":"…","ledgerId":"…","payloadHash":"…","previousHash":"…","sequence":1,"timestamp":"…","v":1}
```

### 3.3 Signature

```
signature = hex(Sign(keyId, SHA256(UTF8(canonicalEnvelope))))
```

The signed message is the 32 raw bytes of the hash, not its hex text. The reference signer is
HMAC-SHA256 with a secret of at least 32 bytes:

```
Sign(keyId, digest) = HMAC-SHA256(secret[keyId], digest)
```

Other algorithms (for example a key held in a KMS) can be used as long as the verifier knows how to check
signatures for each `keyId`.

## 4. Genesis hash

The `previousHash` of the entry with sequence 1 is the genesis hash. Unless the ledger is configured with
an explicit 64 character lowercase hex value, it is derived from the ledger id:

```
genesisHash = hex(SHA256(UTF8("provance:genesis:v1:" + ledgerId)))
```

## 5. Verification

Given the ledger id, the genesis hash and a way to check signatures, a verifier reads entries in ascending
sequence and, for each entry, checks in this order:

1. `sequence` equals the expected value (1, then previous + 1). Otherwise: gap.
2. `v` is 1 and `ledgerId` is the expected ledger.
3. `previousHash` equals the genesis hash (sequence 1) or the `hash` of the previous entry.
4. If the payload is present, `hex(SHA256(UTF8(canonicalPayload)))` equals `payloadHash`.
5. Recomputing the hash from the envelope gives the stored `hash`.
6. `signature` is valid for `keyId`.

The ledger is valid when every entry passes. The verifier reports the sequence and hash of the last entry.

## 6. Redaction

The payload may be deleted (for example to honor a GDPR erasure request) while every other field is kept.
A redacted entry still verifies, because the chain only depends on `payloadHash`. Verifiers count redacted
entries and skip check 4 for them.

Redaction removes the payload, not the fact that an event happened, its type, its time or its hash.

## 7. Checkpoints

A checkpoint is a signed statement about the head of the ledger:

| Field       | JSON type | Description |
| :---------- | :-------- | :---------- |
| `v`         | number    | `1`. |
| `type`      | string    | `"checkpoint"` (present in the signed body only). |
| `ledgerId`  | string    | Ledger the checkpoint refers to. |
| `sequence`  | number    | Sequence of the head entry. |
| `hash`      | string    | Hash of the head entry. |
| `createdAt` | string    | UTC time, same format as `timestamp`. |
| `keyId`     | string    | Key used for the signature. |
| `signature` | string    | `hex(Sign(keyId, SHA256(UTF8(JCS(body)))))`, where `body` holds every field above except `signature`. |

When a verifier is given a trusted checkpoint, it additionally requires that the checkpoint signature is
valid and that the ledger contains an entry at `sequence` whose `hash` equals the checkpoint hash.
This detects:

* truncation: the ledger ends before the checkpoint sequence;
* a full rewrite with the signing key: the entry at the checkpoint sequence has a different hash.

A checkpoint only helps if it is kept where the people able to modify the ledger cannot modify it:
object storage with retention lock, WORM storage, an RFC 3161 timestamping authority, a system owned by
another team. A checkpoint stored next to the ledger, with the same credentials, adds nothing.

## 8. Concurrency

Format 1 assumes a single linear chain. Writers obtain linearity from the store, which must reject an
entry whose `sequence` or `id` already exists. A writer that hits such a conflict reloads the head and
reseals its pending entries on top of it. Timestamps are informational: they are not required to increase.
