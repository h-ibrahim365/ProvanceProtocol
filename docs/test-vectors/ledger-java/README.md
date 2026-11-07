# Cross-language ledger fixture

A small ledger written by the Java library (`provance-core` 0.1.0), exported as JSON Lines, with a checkpoint of
its head. Every implementation must verify it, like the ledgers written by [Python](../ledger-python/) and
[.NET](../ledger-dotnet/).

What it covers:

* payloads with escaped characters, U+2028, an emoji, control characters and numbers that test RFC 8785 formatting
  (`0.1`, `1e21`, `1e-7`, `-0`, `333333333.33333329`);
* a redacted payload (entry 4, `payloadJson` is `null`), erased with version 0.1, which wrote no redaction record:
  0.2 verifiers accept it only when told to accept unrecorded redactions;
* a key rotation: entries 1 to 4 are signed with `k1`, entries 5 and 6 with `k2`;
* a checkpoint of the head (sequence 6) signed with `k2`.

| Key id | HMAC secret (UTF-8) |
| :----- | :------------------ |
| `k1` | `provance-test-vector-key-0123456789abcdef` |
| `k2` | `provance-test-vector-key-2-abcdef0123456789` |

Ledger id: `cross-language-test`. Default genesis hash.

`entries.jsonl` has one entry per line, with the field names of the .NET and Java `LedgerEntry`
(`v`, `ledgerId`, `sequence`, `id`, `timestamp`, `eventType`, `payloadJson`, `payloadHash`, `previousHash`,
`keyId`, `hash`, `signature`). This is also the format of `provance export` in the Python package.
