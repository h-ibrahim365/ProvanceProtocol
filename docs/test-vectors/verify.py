"""Independent check of the PROVANCE format v1 test vectors.

Recomputes every expected value of v1.json from its inputs, without any .NET code,
using the rfc8785 package (pip install rfc8785) for canonical JSON.

Usage: python verify.py [path/to/v1.json]
"""

import hashlib
import hmac
import json
import pathlib
import sys

import rfc8785


def sha256_hex(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def main() -> int:
    path = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else pathlib.Path(__file__).with_name("v1.json"))
    vectors = json.loads(path.read_text(encoding="utf-8"))
    secret = vectors["hmac"]["secretUtf8"].encode("utf-8")
    failures = 0

    def check(label: str, actual: str, expected: str) -> None:
        nonlocal failures
        if actual != expected:
            failures += 1
            print(f"FAIL {label}\n  expected {expected}\n  actual   {actual}")

    genesis = vectors["genesis"]
    check("genesis", sha256_hex(("provance:genesis:v1:" + genesis["ledgerId"]).encode("utf-8")), genesis["hash"])

    previous = genesis["hash"]
    for vector in vectors["entries"]:
        name, inp, exp = vector["name"], vector["input"], vector["expected"]

        canonical_payload = rfc8785.dumps(inp["payload"])
        check(f"{name}: canonicalPayload", canonical_payload.decode("utf-8"), exp["canonicalPayload"])
        payload_hash = sha256_hex(canonical_payload)
        check(f"{name}: payloadHash", payload_hash, exp["payloadHash"])

        check(f"{name}: previousHash links to the previous entry", inp["previousHash"], previous)

        envelope = rfc8785.dumps({
            "v": vectors["formatVersion"],
            "ledgerId": inp["ledgerId"],
            "sequence": inp["sequence"],
            "id": inp["id"],
            "timestamp": inp["timestamp"],
            "eventType": inp["eventType"],
            "payloadHash": payload_hash,
            "previousHash": inp["previousHash"],
            "keyId": inp["keyId"],
        })
        check(f"{name}: canonicalEnvelope", envelope.decode("utf-8"), exp["canonicalEnvelope"])
        entry_hash = sha256_hex(envelope)
        check(f"{name}: hash", entry_hash, exp["hash"])

        signature = hmac.new(secret, bytes.fromhex(entry_hash), hashlib.sha256).hexdigest()
        check(f"{name}: signature", signature, exp["signature"])
        previous = entry_hash

    cp_in, cp_exp = vectors["checkpoint"]["input"], vectors["checkpoint"]["expected"]
    body = rfc8785.dumps({
        "v": vectors["formatVersion"],
        "type": "checkpoint",
        "ledgerId": cp_in["ledgerId"],
        "sequence": cp_in["sequence"],
        "hash": cp_in["hash"],
        "createdAt": cp_in["createdAt"],
        "keyId": cp_in["keyId"],
    })
    check("checkpoint: canonicalBody", body.decode("utf-8"), cp_exp["canonicalBody"])
    digest = sha256_hex(body)
    check("checkpoint: digest", digest, cp_exp["digest"])
    check("checkpoint: signature", hmac.new(secret, bytes.fromhex(digest), hashlib.sha256).hexdigest(), cp_exp["signature"])
    check("checkpoint: references the last entry", cp_in["hash"], previous)

    count = len(vectors["entries"])
    if failures:
        print(f"{failures} check(s) failed")
        return 1
    print(f"All checks passed ({count} entries and 1 checkpoint).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
