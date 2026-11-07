package io.github.hibrahim365.provance.spring;

import io.github.hibrahim365.provance.AuditedPayload;
import io.github.hibrahim365.provance.LedgerCheckpoint;
import io.github.hibrahim365.provance.LedgerEntry;
import io.github.hibrahim365.provance.ProvanceLedger;
import io.github.hibrahim365.provance.compare.LedgerComparison;
import io.github.hibrahim365.provance.jdbc.JdbcLedgerStore;
import io.github.hibrahim365.provance.jdbc.SqlDialect;
import io.github.hibrahim365.provance.signing.HmacLedgerSigner;
import io.github.hibrahim365.provance.signing.LedgerSigner;
import io.github.hibrahim365.provance.store.InMemoryLedgerStore;
import io.github.hibrahim365.provance.store.LedgerStore;

import org.junit.jupiter.api.Test;
import org.springframework.boot.autoconfigure.AutoConfigurations;
import org.springframework.boot.test.context.runner.ApplicationContextRunner;
import org.springframework.boot.test.context.runner.WebApplicationContextRunner;

import java.nio.file.Files;
import java.time.Duration;
import java.nio.file.Path;
import java.util.UUID;

import javax.sql.DataSource;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertInstanceOf;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

class ProvanceAutoConfigurationTests {

    private static final String SECRET = "unit-test-secret-key-0123456789abcdef";

    private final ApplicationContextRunner runner = new ApplicationContextRunner()
            .withConfiguration(AutoConfigurations.of(ProvanceAutoConfiguration.class));

    private static DataSource h2() {
        return new TestDataSource("jdbc:h2:mem:" + UUID.randomUUID() + ";DB_CLOSE_DELAY=-1");
    }

    private static String failureMessages(Throwable failure) {
        StringBuilder sb = new StringBuilder();
        for (Throwable t = failure; t != null; t = t.getCause()) {
            sb.append(t.getMessage()).append('\n');
        }
        return sb.toString();
    }

    @Test
    void ledgerIsCreatedOnTheApplicationDataSource() {
        runner.withBean(DataSource.class, ProvanceAutoConfigurationTests::h2)
                .withPropertyValues("provance.ledger-id=orders-api-test", "provance.secret-key=" + SECRET,
                        "provance.jdbc.table-name=audit_ledger")
                .run(context -> {
                    ProvanceLedger ledger = context.getBean(ProvanceLedger.class);
                    LedgerEntry entry = ledger.addEntry("ORDER_CREATED", AuditedPayload.of("user-42", "created"));
                    assertEquals("orders-api-test", entry.ledgerId());
                    assertTrue(ledger.verify().valid());

                    JdbcLedgerStore store = assertInstanceOf(JdbcLedgerStore.class, context.getBean(LedgerStore.class));
                    assertEquals(SqlDialect.H2, store.dialect());
                    assertTrue(store.createTableStatements().get(0).startsWith("CREATE TABLE audit_ledger"));
                });
    }

    @Test
    void missingSecretFailsAtStartup() {
        runner.withBean(DataSource.class, ProvanceAutoConfigurationTests::h2)
                .withPropertyValues("provance.ledger-id=orders-api-test")
                .run(context -> {
                    assertNotNull(context.getStartupFailure());
                    assertTrue(failureMessages(context.getStartupFailure()).contains("no secret configured"),
                            failureMessages(context.getStartupFailure()));
                });
    }

    @Test
    void missingLedgerIdFailsAtStartup() {
        runner.withBean(DataSource.class, ProvanceAutoConfigurationTests::h2)
                .withPropertyValues("provance.secret-key=" + SECRET)
                .run(context -> assertTrue(failureMessages(context.getStartupFailure()).contains("ledgerId is required")));
    }

    @Test
    void withoutDataSourceOrStoreTheApplicationDoesNotStart() {
        runner.withPropertyValues("provance.ledger-id=orders-api-test", "provance.secret-key=" + SECRET)
                .run(context -> assertTrue(failureMessages(context.getStartupFailure()).contains("No PROVANCE store is configured")));
    }

    @Test
    void customStoreAndSignerReplaceTheDefaults() {
        runner.withBean(LedgerStore.class, InMemoryLedgerStore::new)
                .withBean(LedgerSigner.class, () -> new HmacLedgerSigner("kms-key", SECRET))
                .withPropertyValues("provance.ledger-id=orders-api-test") // no secret needed with a custom signer
                .run(context -> {
                    ProvanceLedger ledger = context.getBean(ProvanceLedger.class);
                    assertEquals("kms-key", ledger.addEntry("EVENT", AuditedPayload.of("u", "x")).keyId());
                    assertInstanceOf(InMemoryLedgerStore.class, context.getBean(LedgerStore.class));
                });
    }

    @Test
    void retiredKeysAndCheckpointDirectoryAreBound() throws Exception {
        Path directory = Files.createTempDirectory("provance-spring");
        runner.withBean(LedgerStore.class, InMemoryLedgerStore::new)
                .withPropertyValues("provance.ledger-id=orders-api-test", "provance.signing-key-id=k2",
                        "provance.secret-key=" + SECRET, "provance.retired-keys.k1=another-unit-test-secret-0123456789",
                        "provance.checkpoints.directory=" + directory, "provance.checkpoint-interval=1h")
                .run(context -> {
                    ProvanceProperties properties = context.getBean(ProvanceProperties.class);
                    assertEquals("another-unit-test-secret-0123456789", properties.getRetiredKeys().get("k1"));
                    assertEquals(directory, properties.getCheckpoints().getDirectory());
                    assertEquals(3600, properties.getCheckpointInterval().getSeconds());

                    ProvanceLedger ledger = context.getBean(ProvanceLedger.class);
                    ledger.addEntry("EVENT", AuditedPayload.of("u", "x"));
                    LedgerCheckpoint checkpoint = ledger.createCheckpoint().orElseThrow();
                    assertEquals("k2", checkpoint.keyId());
                });
    }

    @Test
    void httpAuditingIsOptIn() {
        WebApplicationContextRunner web = new WebApplicationContextRunner()
                .withConfiguration(AutoConfigurations.of(ProvanceAutoConfiguration.class))
                .withBean(LedgerStore.class, InMemoryLedgerStore::new)
                .withPropertyValues("provance.ledger-id=orders-api-test", "provance.secret-key=" + SECRET);

        web.run(context -> assertTrue(context.getBeansOfType(ProvanceAuditFilter.class).isEmpty()));
        web.withPropertyValues("provance.http.enabled=true", "provance.http.include-client-ip=true", "provance.http.order=5")
                .withBean(HttpAuditCustomizer.class, () -> options -> options.getExcludedPaths().add("/internal"))
                .run(context -> {
                    ProvanceAuditFilter filter = context.getBean(ProvanceAuditFilter.class);
                    assertEquals(5, filter.getOrder());
                });
    }

    @Test
    void archiveTableOnTheApplicationDataSource() {
        runner.withBean(DataSource.class, ProvanceAutoConfigurationTests::h2)
                .withPropertyValues("provance.ledger-id=orders-api-test", "provance.secret-key=" + SECRET,
                        "provance.archive.enabled=true")
                .run(context -> {
                    ProvanceLedger ledger = context.getBean(ProvanceLedger.class);
                    ledger.addEntry("ORDER_CREATED", AuditedPayload.of("user-42", "created"));
                    LedgerComparison comparison = ledger.compareWithArchive();
                    assertTrue(comparison.identical(), comparison.describe());
                    assertEquals(1, comparison.compared());

                    JdbcLedgerStore archive = assertInstanceOf(JdbcLedgerStore.class, context.getBean(ProvanceArchive.class).store());
                    assertTrue(archive.createTableStatements().get(0).startsWith("CREATE TABLE provance_archive"));
                    assertInstanceOf(JdbcLedgerStore.class, context.getBean(LedgerStore.class)); // still a single store bean
                });
    }

    @Test
    void archiveTableMustDifferFromTheLedgerTable() {
        runner.withBean(DataSource.class, ProvanceAutoConfigurationTests::h2)
                .withPropertyValues("provance.ledger-id=orders-api-test", "provance.secret-key=" + SECRET,
                        "provance.archive.enabled=true", "provance.archive.table-name=provance_ledger")
                .run(context -> assertTrue(failureMessages(context.getStartupFailure()).contains("must differ")));
    }

    @Test
    void anArchiveBeanAndRetentionPropertiesAreUsed() {
        InMemoryLedgerStore archive = new InMemoryLedgerStore();
        runner.withBean(LedgerStore.class, InMemoryLedgerStore::new)
                .withBean(ProvanceArchive.class, () -> new ProvanceArchive(archive))
                .withPropertyValues("provance.ledger-id=orders-api-test", "provance.secret-key=" + SECRET,
                        "provance.retention-keep-for=400d", "provance.retention-interval=12h",
                        "provance.retention-require-archive=true")
                .run(context -> {
                    ProvanceProperties properties = context.getBean(ProvanceProperties.class);
                    assertEquals(Duration.ofDays(400), properties.getRetentionKeepFor());
                    assertEquals(Duration.ofHours(12), properties.getRetentionInterval());

                    ProvanceLedger ledger = context.getBean(ProvanceLedger.class);
                    ledger.addEntry("EVENT", AuditedPayload.of("u", "x"));
                    assertTrue(ledger.compareWithArchive().identical());
                    assertEquals(1, archive.size());
                    assertTrue(ledger.archiveStatus().orElseThrow().healthy());
                });
    }

    @Test
    void severalLedgersAndTheOneThatReceivesHttpEntries() {
        new WebApplicationContextRunner()
                .withConfiguration(AutoConfigurations.of(ProvanceAutoConfiguration.class))
                .withBean("ordersLedger", ProvanceLedger.class, () -> namedLedger("orders"))
                .withBean("paymentsLedger", ProvanceLedger.class, () -> namedLedger("payments"))
                .withPropertyValues("provance.http.enabled=true", "provance.http.ledger-bean=paymentsLedger")
                .run(context -> {
                    assertEquals(2, context.getBeansOfType(ProvanceLedger.class).size());
                    ProvanceAuditFilter filter = context.getBean(ProvanceAuditFilter.class);
                    assertEquals("payments", ((ProvanceLedger) filter.ledger()).ledgerId());
                });
    }

    @Test
    void withSeveralLedgersHttpAuditingNeedsTheLedgerName() {
        new WebApplicationContextRunner()
                .withConfiguration(AutoConfigurations.of(ProvanceAutoConfiguration.class))
                .withBean("ordersLedger", ProvanceLedger.class, () -> namedLedger("orders"))
                .withBean("paymentsLedger", ProvanceLedger.class, () -> namedLedger("payments"))
                .withPropertyValues("provance.http.enabled=true")
                .run(context -> assertTrue(failureMessages(context.getStartupFailure()).contains("provance.http.ledger-bean")));
    }

    private static ProvanceLedger namedLedger(String id) {
        return ProvanceLedger.builder().ledgerId(id).secretKey(SECRET).store(new InMemoryLedgerStore()).build();
    }
}
