# Cross-language ledger fixture (.NET)

A small ledger written by the .NET library (`Provance.Core` 0.2.0), exported as JSON Lines, with a checkpoint of its head. Every
implementation must verify it, like [the Java fixture](../ledger-java/).

What it covers:

* payloads with escaped characters, U+2028, non-ASCII text, an emoji, control characters and numbers that test
  RFC 8785 formatting (`0.1`, `1e21`, `1e-7`, `-0`, `333333333.33333329`);
* a redacted payload (entry 4, `payloadJson` is `null`), erased through the library: entry 5 is the
  `provance.redaction` record that authorizes it (docs/SPEC.md, section 6);
* a key rotation: entries 1 to 5 are signed with `k1`, entries 6 and 7 with `k2`;
* a checkpoint of the head (sequence 7) signed with `k2`.

Same keys as the Java fixture:

| Key id | HMAC secret (UTF-8) |
| :----- | :------------------ |
| `k1` | `provance-test-vector-key-0123456789abcdef` |
| `k2` | `provance-test-vector-key-2-abcdef0123456789` |

Ledger id: `cross-language-dotnet`. Default genesis hash. Same file format as the Java fixture.
