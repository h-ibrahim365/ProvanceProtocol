package io.github.hibrahim365.provance.spring;

import io.github.hibrahim365.provance.LedgerService;
import io.github.hibrahim365.provance.ProvanceLedger;
import io.github.hibrahim365.provance.checkpoint.LedgerCheckpointSink;
import io.github.hibrahim365.provance.jdbc.JdbcLedgerStore;
import io.github.hibrahim365.provance.json.PayloadSerializer;
import io.github.hibrahim365.provance.signing.HmacLedgerSigner;
import io.github.hibrahim365.provance.signing.LedgerSigner;
import io.github.hibrahim365.provance.store.LedgerStore;

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

import javax.sql.DataSource;

/**
 * Auto-configuration of PROVANCE.
 *
 * <ul>
 *   <li>a {@link ProvanceLedger} bean configured from {@code provance.*}, closed on shutdown;</li>
 *   <li>a {@link JdbcLedgerStore} on the application {@link DataSource}, unless you declare your own {@link LedgerStore};</li>
 *   <li>an HMAC signer from {@code provance.secret-key}, unless you declare your own {@link LedgerSigner};</li>
 *   <li>the HTTP audit filter when {@code provance.http.enabled=true};</li>
 *   <li>checkpoints to every {@link LedgerCheckpointSink} bean and to {@code provance.checkpoints.directory}.</li>
 * </ul>
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
    @ConditionalOnMissingBean(LedgerSigner.class)
    public HmacLedgerSigner provanceLedgerSigner(ProvanceProperties properties) {
        return new HmacLedgerSigner(properties.getSigningKeyId(), properties.getSecretKey(), properties.getRetiredKeys());
    }

    /** JDBC store on the application DataSource. */
    @Bean
    @ConditionalOnMissingBean(LedgerStore.class)
    @ConditionalOnSingleCandidate(DataSource.class)
    public JdbcLedgerStore provanceLedgerStore(DataSource dataSource, ProvanceProperties properties) {
        return new JdbcLedgerStore(dataSource, properties.getJdbc());
    }

    /** The ledger. Spring calls {@code close()} on shutdown, which writes the queued entries. */
    @Bean
    @ConditionalOnMissingBean(LedgerService.class)
    public ProvanceLedger provanceLedger(ProvanceProperties properties,
                                         ObjectProvider<LedgerStore> store,
                                         LedgerSigner signer,
                                         ObjectProvider<PayloadSerializer> serializer,
                                         ObjectProvider<LedgerCheckpointSink> sinks) {
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
        ProvanceAuditFilter provanceAuditFilter(LedgerService ledger, ProvanceProperties properties,
                                                ObjectProvider<HttpAuditCustomizer> customizers) {
            HttpAuditOptions options = HttpAuditOptions.from(properties.getHttp());
            customizers.orderedStream().forEach(c -> c.customize(options));
            return new ProvanceAuditFilter(ledger, options, properties.getHttp().getOrder());
        }
    }
}
