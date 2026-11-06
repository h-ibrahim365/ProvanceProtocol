# PROVANCE for Java

Java implementation of PROVANCE, the tamper-evident audit trail. It writes the same ledger format as the .NET
library ([format specification](../../docs/SPEC.md)) and passes the same [test vectors](../../docs/test-vectors/v1.json):
a ledger written in one language can be verified in the other.

Requires Java 17 or later. Works with Kotlin too.

| Module | Use |
| :----- | :-- |
| `provance-core` | Ledger, verification, checkpoints. No dependencies. |
| `provance-jdbc` | Store for PostgreSQL, SQL Server, MySQL, MariaDB, Oracle, SQLite, H2 and other JDBC databases. |
| `provance-spring-boot-starter` | Spring Boot 4 auto-configuration and an optional HTTP audit filter. |

Published on Maven Central under `io.github.h-ibrahim365`.

## Spring Boot

```xml
<dependency>
    <groupId>io.github.h-ibrahim365</groupId>
    <artifactId>provance-spring-boot-starter</artifactId>
    <version>0.1.0</version>
</dependency>
```

With Gradle:

```kotlin
implementation("io.github.h-ibrahim365:provance-spring-boot-starter:0.1.0")
```

`application.properties` (no secret here):

```properties
provance.ledger-id=orders-api-prod
provance.signing-key-id=k1
```

The key comes from an environment variable or a secret store, at least 32 bytes. Spring maps the environment
variable `PROVANCE_SECRETKEY` to `provance.secret-key`:

```bash
export PROVANCE_SECRETKEY="$(openssl rand -base64 48)"
```

The starter creates a `ProvanceLedger` on the application `DataSource`. The table `provance_ledger` is created on
first use.

```java
@RestController
class RefundController {

    private final LedgerService ledger;

    RefundController(LedgerService ledger) {
        this.ledger = ledger;
    }

    @PostMapping("/orders/{id}/refund")
    void refund(@PathVariable String id, @RequestBody RefundRequest request, Principal user) {
        ledger.addEntry("ORDER_REFUNDED", AuditedPayload.of(user.getName(), "Refund issued",
                Map.of("orderId", id, "amount", request.amount())));
        // ... do the refund
    }
}
```

`addEntry` returns once the entry is stored. `addEntryAsync` returns a `CompletableFuture` instead.

The application refuses to start with a missing ledger id, a short or placeholder key, or no store. There is no
silent fallback to an in-memory store.

### One entry per HTTP request

```properties
provance.http.enabled=true
```

The filter records the method, path, status code, duration, authenticated user and trace id. Query strings, client
IP addresses and user agents are personal data in many cases and are off by default
(`provance.http.include-query-string`, `include-client-ip`, `include-user-agent`). `/actuator`, `/health` and
`/metrics` are excluded. It runs after Spring Security, so the user is known.

Change the rest in code:

```java
@Bean
HttpAuditCustomizer provanceHttp() {
    return options -> options
            .setShouldAudit(request -> request.getRequestURI().startsWith("/api/"))
            .setActorIdResolver(request -> request.getHeader("X-Service-Name"));
}
```

### Properties

| Property | Default |
| :------- | :------ |
| `provance.ledger-id` | required |
| `provance.secret-key` | required unless you declare your own `LedgerSigner` bean |
| `provance.signing-key-id` | `k1` |
| `provance.retired-keys.<id>` | none, keys kept to verify entries after a rotation |
| `provance.jdbc.table-name`, `provance.jdbc.schema` | `provance_ledger`, none |
| `provance.jdbc.create-table` | `true`, set `false` when your migrations create the table |
| `provance.checkpoints.directory` | none, see Checkpoints |
| `provance.checkpoint-interval` | `5m` |
| `provance.queue-capacity`, `provance.queue-full-behavior` | `10000`, `wait` |
| `provance.http.enabled` | `false` |

Declare a `LedgerStore`, `LedgerSigner`, `PayloadSerializer` or `LedgerCheckpointSink` bean to replace the default.

## Without Spring

```java
ProvanceLedger ledger = ProvanceLedger.builder()
        .ledgerId("orders-api-prod")
        .secretKey(System.getenv("PROVANCE_SECRET_KEY"))
        .store(new JdbcLedgerStore(dataSource))
        .build();

ledger.addEntry("ROLE_GRANTED", AuditedPayload.of("admin", "reader role for alice"));

LedgerVerificationResult result = ledger.verify();
// result.valid(), result.failure(), result.failedAtSequence(), result.lastSequence(), result.lastHash()

ledger.close(); // on shutdown: writes the queued entries
```

Payloads can be records, maps, lists or classes with getters. The built-in serializer has no dependency; to use
Jackson, pass `.payloadSerializer(mapper::writeValueAsString)` (wrap the checked exception). Write money amounts and
large numbers as strings: payloads are canonicalized with RFC 8785, which treats numbers as doubles.

## Databases

| Database | Tested in CI |
| :------- | :----------- |
| PostgreSQL | Yes (container) |
| SQL Server, Azure SQL | Yes (container) |
| MySQL | Yes (container) |
| H2, SQLite | Yes |
| MariaDB | No, same code path as MySQL |
| Oracle 12c and later | No |
| Anything else | `SqlDialect.GENERIC` (standard SQL types), or implement `LedgerStore` (5 methods) |

The dialect is detected from the connection. Several instances of the application can write to the same ledger:
the unique index on `seq` rejects a sequence that already exists and the losing instance reloads the head and
retries.

When migrations manage your schema, set `create-table` to `false` and take the statements from
`new JdbcLedgerStore(dataSource).createTableStatements()`. Keep the unique index on `seq`.

The table has the same name and columns as the one created by the .NET Entity Framework Core store. On PostgreSQL
and SQL Server the column types are the same too. Reading a table written by the other implementation is the
intended use but is not covered by a test yet.

## Verifying and checkpoints

Verification detects a modified field, a deleted, inserted or moved entry, and hashes recomputed without the key.
Entries removed from the end, and a full rewrite by someone holding the key, are only detected against a
checkpoint stored where those people cannot write. See [What it detects](../../README.md#what-it-detects-and-what-it-does-not).

```java
LedgerCheckpoint checkpoint = LedgerCheckpoint.fromJson(jsonFromTrustedStorage);
LedgerVerificationResult result = ledger.verify(LedgerVerificationOptions.withCheckpoint(checkpoint));
```

Checkpoints are published automatically to every `LedgerCheckpointSink` (one method), or to files with
`provance.checkpoints.directory` / `.fileCheckpoints(path)`. Checkpoint JSON is the same in Java and .NET.

`LedgerVerifier` works on its own, for an audit tool with read access to the database and the verification key:

```java
LedgerVerifier verifier = new LedgerVerifier(new JdbcLedgerStore(readOnlyDataSource), new HmacLedgerSigner("k1", key), "orders-api-prod");
```

The default signature is HMAC-SHA256: whoever has the key can sign. Implement `LedgerSigner` to keep the key in a
KMS so the application never sees it.

## Building

```bash
cd src/java
mvn verify            # tests; PostgreSQL, SQL Server and MySQL tests need Docker and are skipped without it
mvn install           # installs the current version in your local Maven repository
```

## Publishing to Maven Central (maintainers)

1. Create an account on [central.sonatype.com](https://central.sonatype.com), sign in with GitHub so the namespace
   `io.github.h-ibrahim365` is verified, and generate a user token.
2. Create a GPG key and publish its public part to `keyserver.ubuntu.com`.
3. Add the repository secrets `MAVEN_CENTRAL_USERNAME`, `MAVEN_CENTRAL_PASSWORD` (the token),
   `MAVEN_GPG_PRIVATE_KEY` (armored private key) and `MAVEN_GPG_PASSPHRASE`.
4. Publish a GitHub release tagged `java-v0.1.0`. The `Release (Java)` workflow checks the tag against the POM,
   runs the tests, signs and uploads the three modules.
