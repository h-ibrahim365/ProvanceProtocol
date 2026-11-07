# Cross-language ledger fixture (Python)

A small ledger written by the Python library (`provance` 0.1.0), exported as JSON Lines, with a checkpoint of its head. Every
implementation must verify it, like [the Java fixture](../ledger-java/).

What it covers:

* payloads with escaped characters, U+2028, non-ASCII text, an emoji, control characters and numbers that test
  RFC 8785 formatting (`0.1`, `1e21`, `1e-7`, `-0`, `333333333.33333329`);
* a redacted payload (entry 4, `payloadJson` is `null`), erased with version 0.1, which wrote no redaction record:
  0.2 verifiers accept it only when told to accept unrecorded redactions;
* a key rotation: entries 1 to 4 are signed with `k1`, entries 5 and 6 with `k2`;
* a checkpoint of the head (sequence 6) signed with `k2`.

Same keys as the Java fixture:

| Key id | HMAC secret (UTF-8) |
| :----- | :------------------ |
| `k1` | `provance-test-vector-key-0123456789abcdef` |
| `k2` | `provance-test-vector-key-2-abcdef0123456789` |

Ledger id: `cross-language-python`. Default genesis hash. Same file format as the Java fixture.
