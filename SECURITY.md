# Security policy

## Supported versions

| Version | Supported |
| :------ | :-------- |
| 0.1.x   | Yes |
| 0.0.x   | No. Upgrade to 0.1.x (see docs/MIGRATION.md). |

## Reporting a vulnerability

Please do not open a public issue for a security problem.

Use GitHub private vulnerability reporting: on the repository page, open the **Security** tab and choose
**Report a vulnerability**. Include the affected version, what an attacker can do, and if possible a
minimal reproduction.

You should get a first answer within 7 days. Once a fix is released, the advisory is published with
credit to the reporter, unless you prefer to stay anonymous.

## Scope

In scope: anything that lets an entry, a checkpoint or a ledger be modified, removed or forged without
`VerifyAsync` reporting it, given the guarantees listed in the README and docs/SPEC.md. Also: secrets or
personal data leaking through logs, exceptions or the HTTP middleware.

Out of scope: the limits documented in the README (for example truncation without a trusted checkpoint,
or a full rewrite by someone who holds the signing key and can also modify the checkpoints).
