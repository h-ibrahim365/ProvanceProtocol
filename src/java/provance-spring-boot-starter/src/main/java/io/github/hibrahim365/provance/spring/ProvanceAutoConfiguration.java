package io.github.hibrahim365.provance.spring;

import io.github.hibrahim365.provance.LedgerService;
import io.github.hibrahim365.provance.ProvanceLedger;
import io.github.hibrahim365.provance.checkpoint.LedgerCheckpointSink;
import io.github.hibrahim365.provance.jdbc.JdbcLedgerStore;
import io.github.hibrahim365.provance.json.PayloadSerializer;
import io.github.hibrahim365.provance.signing.HmacLedgerSigner;
import io.github.hibrahim365.provance.signing.LedgerSigner;
import io.github.hibrahim365.provance.store.LedgerStore;

import org.springframework.beans.factory.BeanFactory;
import org.springframework.beans.factory.ObjectProvider;
import org.springframework.boot.autoconfigure.AutoConfiguration;
import org.springframework.boot.autoconfigure.condition.ConditionalOnClass;
import org.springframework.boot.autoconfigure.condition.ConditionalOnMissingBean;
import org.springframework.boot.autoconfigure.condition.ConditionalOnProperty;
import org.springframework.boot.autoconfigure.condition.ConditionalOnSingleCandidate;
import org.springframework.boot.autoconfigure.condition.ConditionalOnWebApplication;
import org.springframework.boot.context.properties.EnableConfigurationProperties;
import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;

import java.util.Objects;

import javax.sql.DataSource;

/**
 * Auto-configuration of PROVANCE.
 *
 * <ul>
 *   <li>a {@link ProvanceLedger} bean configured from {@code provance.*}, closed on shutdown;</li>
 *   <li>a {@link JdbcLedgerStore} on the application {@link DataSource}, unless you declare your own {@link LedgerStore};</li>
 *   <li>an HMAC signer from {@code provance.secret-key}, unless you declare your own {@link LedgerSigner};</li>
 *   <li>the HTTP audit filter when {@code provance.http.enabled=true};</li>
 *   <li>checkpoints to every {@link LedgerCheckpointSink} bean and to {@code provance.checkpoints.directory};</li>
 *   <li>a copy to the {@link ProvanceArchive} bean, or to the table {@code provance_archive} when
 *       {@code provance.archive.enabled=true};</li>
 *   <li>retention from {@code provance.retention-keep-for} (see {@code docs/RETENTION.md}).</li>
 * </ul>
 *
 * <p>For several ledgers, declare them as {@link ProvanceLedger} beans with their own stores: this ledger is then not
 * created (nor its store, signer and archive), and {@code provance.http.ledger-bean} names the one that receives the HTTP entries.
 *
 * <p>There is no silent fallback: without a DataSource or a LedgerStore bean, the application fails to start.
 */
@AutoConfiguration(afterName = {
        "org.springframework.boot.jdbc.autoconfigure.DataSourceAutoConfiguration",
        "org.springframework.boot.autoconfigure.jdbc.DataSourceAutoConfiguration"})
@EnableConfigurationProperties(ProvanceProperties.class)
public class ProvanceAutoConfiguration {

    /** HMAC signer from {@code provance.signing-key-id}, {@code provance.secret-key} and {@code provance.retired-keys}. */
    @Bean
    @ConditionalOnMissingBean({LedgerSigner.class, LedgerService.class})
    public HmacLedgerSigner provanceLedgerSigner(ProvanceProperties properties) {
        return new HmacLedgerSigner(properties.getSigningKeyId(), properties.getSecretKey(), properties.getRetiredKeys());
    }

    /** JDBC store on the application DataSource. */
    @Bean
    @ConditionalOnMissingBean({LedgerStore.class, LedgerService.class})
    @ConditionalOnSingleCandidate(DataSource.class)
    public JdbcLedgerStore provanceLedgerStore(DataSource dataSource, ProvanceProperties properties) {
        return new JdbcLedgerStore(dataSource, properties.getJdbc());
    }

    /** Archive table on the application DataSource, with {@code provance.archive.enabled=true}. */
    @Bean
    @ConditionalOnMissingBean({ProvanceArchive.class, LedgerService.class})
    @ConditionalOnProperty(prefix = "provance.archive", name = "enabled", havingValue = "true")
    @ConditionalOnSingleCandidate(DataSource.class)
    public ProvanceArchive provanceArchive(DataSource dataSource, ProvanceProperties properties) {
        ProvanceProperties.Archive archive = properties.getArchive();
        if (archive.getTableName().equalsIgnoreCase(properties.getJdbc().getTableName())
                && Objects.equals(archive.getSchema(), properties.getJdbc().getSchema())) {
            throw new IllegalStateException("provance.archive.table-name must differ from provance.jdbc.table-name.");
        }
        return new ProvanceArchive(new JdbcLedgerStore(dataSource, archive));
    }

    /** The ledger. Spring calls {@code close()} on shutdown, which writes the queued entries. */
    @Bean
    @ConditionalOnMissingBean(LedgerService.class)
    public ProvanceLedger provanceLedger(ProvanceProperties properties,
                                         ObjectProvider<LedgerStore> store,
                                         LedgerSigner signer,
                                         ObjectProvider<PayloadSerializer> serializer,
                                         ObjectProvider<LedgerCheckpointSink> sinks,
                                         ObjectProvider<ProvanceArchive> archive) {
        LedgerStore ledgerStore = store.getIfUnique();
        if (ledgerStore == null) {
            throw new IllegalStateException("No PROVANCE store is configured. Add a DataSource (for example "
                    + "spring-boot-starter-jdbc with a database driver) or declare a LedgerStore bean.");
        }
        ProvanceLedger.Builder builder = ProvanceLedger.builder()
                .options(properties)
                .store(ledgerStore)
                .signer(signer);
        PayloadSerializer customSerializer = serializer.getIfUnique();
        if (customSerializer != null) {
            builder.payloadSerializer(customSerializer);
        }
        ProvanceArchive archiveStore = archive.getIfUnique();
        if (archiveStore != null) {
            builder.archive(archiveStore.store());
        } else if (properties.getArchive().isEnabled()) {
            throw new IllegalStateException("provance.archive.enabled is true but there is no single DataSource for the "
                    + "archive table. Declare a ProvanceArchive bean.");
        }
        sinks.orderedStream().forEach(builder::checkpointSink);
        if (properties.getCheckpoints().getDirectory() != null) {
            builder.fileCheckpoints(properties.getCheckpoints().getDirectory());
        }
        return builder.build();
    }

    /** HTTP auditing, enabled with {@code provance.http.enabled=true}. */
    @Configuration(proxyBeanMethods = false)
    @ConditionalOnWebApplication(type = ConditionalOnWebApplication.Type.SERVLET)
    @ConditionalOnClass(name = "jakarta.servlet.Filter")
    @ConditionalOnProperty(prefix = "provance.http", name = "enabled", havingValue = "true")
    static class HttpAuditConfiguration {

        @Bean
        @ConditionalOnMissingBean
        ProvanceAuditFilter provanceAuditFilter(ObjectProvider<LedgerService> ledgers, BeanFactory beans,
                                                ProvanceProperties properties,
                                                ObjectProvider<HttpAuditCustomizer> customizers) {
            String name = properties.getHttp().getLedgerBean();
            LedgerService ledger = name != null ? beans.getBean(name, LedgerService.class) : ledgers.getIfUnique();
            if (ledger == null) {
                throw new IllegalStateException("provance.http.enabled is true but there is no single ledger. With several "
                        + "ledgers, set provance.http.ledger-bean to the name of the one that receives the HTTP entries.");
            }
            HttpAuditOptions options = HttpAuditOptions.from(properties.getHttp());
            customizers.orderedStream().forEach(c -> c.customize(options));
            return new ProvanceAuditFilter(ledger, options, properties.getHttp().getOrder());
        }
    }
}
