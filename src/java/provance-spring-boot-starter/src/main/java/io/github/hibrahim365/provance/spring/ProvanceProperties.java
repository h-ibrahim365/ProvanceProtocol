package io.github.hibrahim365.provance.spring;

import io.github.hibrahim365.provance.ProvanceOptions;
import io.github.hibrahim365.provance.jdbc.JdbcStoreOptions;

import org.springframework.boot.context.properties.ConfigurationProperties;

import java.nio.file.Path;
import java.time.Duration;
import java.util.ArrayList;
import java.util.List;

/**
 * {@code provance.*} configuration properties. The ledger settings are those of {@link ProvanceOptions}
 * ({@code provance.ledger-id}, {@code provance.secret-key}, {@code provance.retired-keys.*}, ...).
 */
@ConfigurationProperties(prefix = "provance")
public class ProvanceProperties extends ProvanceOptions {

    private final Jdbc jdbc = new Jdbc();
    private final Http http = new Http();
    private final Checkpoints checkpoints = new Checkpoints();
    private final Archive archive = new Archive();

    /** JDBC store settings ({@code provance.jdbc.*}). */
    public Jdbc getJdbc() {
        return jdbc;
    }

    /** HTTP auditing settings ({@code provance.http.*}). */
    public Http getHttp() {
        return http;
    }

    /** Automatic checkpoints ({@code provance.checkpoints.*}). */
    public Checkpoints getCheckpoints() {
        return checkpoints;
    }

    /** Archive copy ({@code provance.archive.*}). */
    public Archive getArchive() {
        return archive;
    }

    /** JDBC store settings: table name, schema, automatic creation, read batch size, dialect. */
    public static class Jdbc extends JdbcStoreOptions {
    }

    /**
     * Archive copy on the application DataSource. The table settings are the same as {@code provance.jdbc.*}, under
     * {@code provance.archive.*}; the table name defaults to {@code provance_archive}. A {@link ProvanceArchive} bean
     * replaces it (for example to use another database and an account that can only insert).
     */
    public static class Archive extends JdbcStoreOptions {

        /** Default archive table. */
        public static final String DEFAULT_TABLE_NAME = "provance_archive";

        private boolean enabled;

        public Archive() {
            setTableName(DEFAULT_TABLE_NAME);
        }

        /** Copy every entry to the archive table. Default {@code false}. */
        public boolean isEnabled() {
            return enabled;
        }

        /** @see #isEnabled() */
        public void setEnabled(boolean enabled) {
            this.enabled = enabled;
        }
    }

    /** HTTP auditing settings. Privacy sensitive fields are off by default. */
    public static class Http {

        private boolean enabled;
        private HttpAuditFailureMode failureMode = HttpAuditFailureMode.LOG_AND_CONTINUE;
        private Duration auditTimeout = Duration.ofSeconds(5);
        private boolean includeQueryString;
        private boolean includeClientIp;
        private boolean includeUserAgent;
        private List<String> excludedPaths = new ArrayList<>(List.of("/actuator", "/health", "/healthz", "/metrics", "/favicon.ico"));
        private int order;
        private String ledgerBean;

        /** Record one ledger entry per HTTP request. Default {@code false}. */
        public boolean isEnabled() {
            return enabled;
        }

        /** @see #isEnabled() */
        public void setEnabled(boolean enabled) {
            this.enabled = enabled;
        }

        /** Behavior when the audit entry cannot be written. Default {@code log-and-continue}. */
        public HttpAuditFailureMode getFailureMode() {
            return failureMode;
        }

        /** @see #getFailureMode() */
        public void setFailureMode(HttpAuditFailureMode failureMode) {
            this.failureMode = failureMode;
        }

        /** Maximum time a request waits for its audit entry. Default 5 seconds. */
        public Duration getAuditTimeout() {
            return auditTimeout;
        }

        /** @see #getAuditTimeout() */
        public void setAuditTimeout(Duration auditTimeout) {
            this.auditTimeout = auditTimeout;
        }

        /** Record the query string. Off by default: query strings often carry tokens and personal data. */
        public boolean isIncludeQueryString() {
            return includeQueryString;
        }

        /** @see #isIncludeQueryString() */
        public void setIncludeQueryString(boolean includeQueryString) {
            this.includeQueryString = includeQueryString;
        }

        /** Record the client IP address. Off by default: an IP address is personal data under GDPR. */
        public boolean isIncludeClientIp() {
            return includeClientIp;
        }

        /** @see #isIncludeClientIp() */
        public void setIncludeClientIp(boolean includeClientIp) {
            this.includeClientIp = includeClientIp;
        }

        /** Record the User-Agent header. Off by default. */
        public boolean isIncludeUserAgent() {
            return includeUserAgent;
        }

        /** @see #isIncludeUserAgent() */
        public void setIncludeUserAgent(boolean includeUserAgent) {
            this.includeUserAgent = includeUserAgent;
        }

        /** Path prefixes that are never audited (matched on whole segments, case-insensitive). */
        public List<String> getExcludedPaths() {
            return excludedPaths;
        }

        /** @see #getExcludedPaths() */
        public void setExcludedPaths(List<String> excludedPaths) {
            this.excludedPaths = excludedPaths == null ? new ArrayList<>() : excludedPaths;
        }

        /**
         * Order of the servlet filter. Default 0, which places it after Spring Security (-100) so the authenticated
         * user is known when the entry is written.
         */
        public int getOrder() {
            return order;
        }

        /** @see #getOrder() */
        public void setOrder(int order) {
            this.order = order;
        }

        /**
         * Name of the ledger bean that receives the HTTP entries, when the application declares several ledgers.
         * Unset by default: the single (or {@code @Primary}) ledger is used.
         */
        public String getLedgerBean() {
            return ledgerBean;
        }

        /** @see #getLedgerBean() */
        public void setLedgerBean(String ledgerBean) {
            this.ledgerBean = ledgerBean == null || ledgerBean.isBlank() ? null : ledgerBean;
        }
    }

    /** Automatic checkpoints. */
    public static class Checkpoints {

        private Path directory;

        /**
         * Directory where checkpoints are written ({@code {directory}/{ledgerId}/{sequence}.json}) at
         * {@code provance.checkpoint-interval}. Point it to storage the application cannot overwrite. Unset by default.
         */
        public Path getDirectory() {
            return directory;
        }

        /** @see #getDirectory() */
        public void setDirectory(Path directory) {
            this.directory = directory;
        }
    }
}
