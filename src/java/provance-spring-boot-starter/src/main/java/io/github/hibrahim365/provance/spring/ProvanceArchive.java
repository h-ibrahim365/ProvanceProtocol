package io.github.hibrahim365.provance.spring;

import io.github.hibrahim365.provance.store.LedgerStore;

import java.util.Objects;

/**
 * The archive store of the auto-configured ledger (see {@code docs/ARCHIVE.md}). It is wrapped in this type so that it
 * is not a second {@link LedgerStore} bean.
 *
 * <p>Declare one to copy the ledger to another database, ideally with an account that can only insert into the archive
 * table:
 *
 * <pre>{@code
 * @Bean
 * ProvanceArchive provanceArchive(@Qualifier("archiveDataSource") DataSource archive) {
 *     return new ProvanceArchive(new JdbcLedgerStore(archive, archiveOptions));
 * }
 * }</pre>
 *
 * <p>Without such a bean, {@code provance.archive.enabled=true} creates the table {@code provance_archive} on the
 * application DataSource.
 */
public final class ProvanceArchive {

    private final LedgerStore store;

    public ProvanceArchive(LedgerStore store) {
        this.store = Objects.requireNonNull(store, "store");
    }

    /** The archive store. */
    public LedgerStore store() {
        return store;
    }
}
