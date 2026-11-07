package io.github.hibrahim365.provance.jdbc;

import io.github.hibrahim365.provance.LedgerConcurrencyException;
import io.github.hibrahim365.provance.LedgerEntry;
import io.github.hibrahim365.provance.store.LedgerStore;
import io.github.hibrahim365.provance.store.PrunableLedgerStore;
import io.github.hibrahim365.provance.store.LedgerStoreException;

import java.sql.Connection;
import java.sql.PreparedStatement;
import java.sql.ResultSet;
import java.sql.SQLException;
import java.sql.Statement;
import java.time.Instant;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Iterator;
import java.util.List;
import java.util.NoSuchElementException;
import java.util.Objects;
import java.util.Optional;
import java.util.Spliterator;
import java.util.Spliterators;
import java.util.UUID;
import java.util.stream.Stream;
import java.util.stream.StreamSupport;

import javax.sql.DataSource;

/**
 * {@link LedgerStore} on top of any JDBC {@link DataSource}: PostgreSQL, SQL Server, MySQL, MariaDB, Oracle, SQLite,
 * H2 and other databases with standard SQL.
 *
 * <p>The table has the same name and columns as the one created by the .NET Entity Framework Core store. A unique
 * index on {@code seq} keeps the chain linear when several application instances write at the same time.
 *
 * <p>Conflicts are detected without parsing vendor specific error codes: when an insert fails, the store checks
 * whether a row with one of the batch's sequences or ids now exists. A batch is inserted in one transaction, so it
 * is persisted completely or not at all.
 */
public final class JdbcLedgerStore implements PrunableLedgerStore {

    private static final String COLUMNS =
            "id, format_version, ledger_id, seq, sealed_at, event_type, payload, payload_hash, previous_hash, key_id, entry_hash, signature";

    private static final int ID_CHUNK = 500;

    private final DataSource dataSource;
    private final JdbcStoreOptions options;
    private final String table;
    private final String indexName;
    private final Object initLock = new Object();
    private volatile SqlDialect dialect;
    private volatile boolean initialized;

    /** Creates a store with the default options (table {@code provance_ledger}, created on first use). */
    public JdbcLedgerStore(DataSource dataSource) {
        this(dataSource, new JdbcStoreOptions());
    }

    /** Creates a store with explicit options. */
    public JdbcLedgerStore(DataSource dataSource, JdbcStoreOptions options) {
        this.dataSource = Objects.requireNonNull(dataSource, "dataSource");
        this.options = Objects.requireNonNull(options, "options").copy();
        this.options.validate();
        this.table = this.options.getSchema() == null || this.options.getSchema().isEmpty()
                ? this.options.getTableName()
                : this.options.getSchema() + "." + this.options.getTableName();
        this.indexName = this.options.getTableName() + "_seq_unique";
        this.dialect = this.options.getDialect();
    }

    /** The dialect in use, detected from the database on first call when it was not configured. */
    public SqlDialect dialect() {
        SqlDialect d = dialect;
        if (d != null) {
            return d;
        }
        try (Connection c = dataSource.getConnection()) {
            d = SqlDialect.fromProductName(c.getMetaData().getDatabaseProductName());
            dialect = d;
            return d;
        } catch (SQLException e) {
            throw new LedgerStoreException("PROVANCE JDBC: cannot detect the database type: " + e.getMessage(), e);
        }
    }

    /** Statements creating the table and its unique index, for your migrations. */
    public List<String> createTableStatements() {
        SqlDialect d = dialect();
        if (d == SqlDialect.SQLITE && options.getSchema() != null && !options.getSchema().isEmpty()) {
            // SQLite qualifies the index name, not the table, in CREATE INDEX.
            List<String> statements = new ArrayList<>(d.createTableStatements(table, indexName));
            statements.set(1, "CREATE UNIQUE INDEX " + options.getSchema() + "." + indexName + " ON " + options.getTableName() + " (seq)");
            return Collections.unmodifiableList(statements);
        }
        return d.createTableStatements(table, indexName);
    }

    @Override
    public Optional<LedgerEntry> getHead() {
        ensureInitialized();
        String sql = "SELECT " + COLUMNS + " FROM " + table + " WHERE seq = (SELECT MAX(seq) FROM " + table + ")";
        try (Connection c = dataSource.getConnection(); PreparedStatement ps = c.prepareStatement(sql); ResultSet rs = ps.executeQuery()) {
            return rs.next() ? Optional.of(readEntry(rs)) : Optional.empty();
        } catch (SQLException e) {
            throw failure("reading the head", e);
        }
    }

    @Override
    public void append(List<LedgerEntry> entries) {
        Objects.requireNonNull(entries, "entries");
        if (entries.isEmpty()) {
            return;
        }
        ensureInitialized();
        SqlDialect d = dialect;
        String sql = "INSERT INTO " + table + " (" + COLUMNS + ") VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)";
        try {
            try (Connection c = dataSource.getConnection()) {
                boolean autoCommit = c.getAutoCommit();
                c.setAutoCommit(false);
                try (PreparedStatement ps = c.prepareStatement(sql)) {
                    for (LedgerEntry e : entries) {
                        d.bindId(ps, 1, e.id());
                        ps.setInt(2, e.formatVersion());
                        ps.setString(3, e.ledgerId());
                        ps.setLong(4, e.sequence());
                        d.bindTime(ps, 5, e.timestamp());
                        ps.setString(6, e.eventType());
                        ps.setString(7, e.payloadJson());
                        ps.setString(8, e.payloadHash());
                        ps.setString(9, e.previousHash());
                        ps.setString(10, e.keyId());
                        ps.setString(11, e.hash());
                        ps.setString(12, e.signature());
                        ps.addBatch();
                    }
                    ps.executeBatch();
                    c.commit();
                } catch (SQLException | RuntimeException e) {
                    rollbackQuietly(c, e);
                    throw e;
                } finally {
                    restoreAutoCommit(c, autoCommit);
                }
            }
        } catch (SQLException | RuntimeException e) {
            if (conflictExists(entries)) {
                throw new LedgerConcurrencyException("An entry with the same sequence or id already exists.", e);
            }
            if (e instanceof SQLException) {
                throw failure("appending " + entries.size() + " entries", (SQLException) e);
            }
            throw (RuntimeException) e;
        }
    }

    @Override
    public Optional<LedgerEntry> getById(UUID id) {
        Objects.requireNonNull(id, "id");
        ensureInitialized();
        String sql = "SELECT " + COLUMNS + " FROM " + table + " WHERE id = ?";
        try (Connection c = dataSource.getConnection(); PreparedStatement ps = c.prepareStatement(sql)) {
            dialect.bindId(ps, 1, id);
            try (ResultSet rs = ps.executeQuery()) {
                return rs.next() ? Optional.of(readEntry(rs)) : Optional.empty();
            }
        } catch (SQLException e) {
            throw failure("reading an entry", e);
        }
    }

    @Override
    public Stream<LedgerEntry> read(long fromSequence) {
        ensureInitialized();
        Iterator<LedgerEntry> pages = new PageIterator(fromSequence);
        return StreamSupport.stream(Spliterators.spliteratorUnknownSize(pages, Spliterator.ORDERED | Spliterator.NONNULL), false);
    }

    @Override
    public boolean redactPayload(UUID id) {
        Objects.requireNonNull(id, "id");
        ensureInitialized();
        String sql = "UPDATE " + table + " SET payload = NULL, redacted_at = ? WHERE id = ?";
        try (Connection c = dataSource.getConnection(); PreparedStatement ps = c.prepareStatement(sql)) {
            dialect.bindTime(ps, 1, Instant.now());
            dialect.bindId(ps, 2, id);
            int updated = ps.executeUpdate();
            if (!c.getAutoCommit()) {
                c.commit();
            }
            return updated > 0;
        } catch (SQLException e) {
            throw failure("redacting a payload", e);
        }
    }

    /** Deletes every entry with a lower sequence (retention). Needs {@code DELETE} on the table. */
    @Override
    public long deleteBefore(long sequence) {
        ensureInitialized();
        String sql = "DELETE FROM " + table + " WHERE seq < ?";
        try (Connection c = dataSource.getConnection(); PreparedStatement ps = c.prepareStatement(sql)) {
            ps.setLong(1, sequence);
            int deleted = ps.executeUpdate();
            if (!c.getAutoCommit()) {
                c.commit();
            }
            return deleted;
        } catch (SQLException e) {
            throw failure("deleting old entries", e);
        }
    }

    /** Reads one page of entries; used by the streaming iterator. Pages keep connections short on every database. */
    private List<LedgerEntry> readPage(long from) {
        String sql = "SELECT " + COLUMNS + " FROM " + table + " WHERE seq >= ? ORDER BY seq";
        int size = options.getReadBatchSize();
        try (Connection c = dataSource.getConnection(); PreparedStatement ps = c.prepareStatement(sql)) {
            ps.setMaxRows(size);
            ps.setFetchSize(Math.min(size, 1000));
            ps.setLong(1, from);
            List<LedgerEntry> page = new ArrayList<>(Math.min(size, 1024));
            try (ResultSet rs = ps.executeQuery()) {
                while (rs.next() && page.size() < size) {
                    page.add(readEntry(rs));
                }
            }
            return page;
        } catch (SQLException e) {
            throw failure("reading entries", e);
        }
    }

    private final class PageIterator implements Iterator<LedgerEntry> {
        private long next;
        private List<LedgerEntry> page = List.of();
        private int index;
        private boolean last;

        PageIterator(long from) {
            this.next = from;
        }

        @Override
        public boolean hasNext() {
            if (index < page.size()) {
                return true;
            }
            if (last) {
                return false;
            }
            page = readPage(next);
            index = 0;
            if (page.size() < options.getReadBatchSize()) {
                last = true;
            }
            if (!page.isEmpty()) {
                next = page.get(page.size() - 1).sequence() + 1;
            }
            return !page.isEmpty();
        }

        @Override
        public LedgerEntry next() {
            if (!hasNext()) {
                throw new NoSuchElementException();
            }
            return page.get(index++);
        }
    }

    private LedgerEntry readEntry(ResultSet rs) throws SQLException {
        SqlDialect d = dialect;
        return new LedgerEntry(
                rs.getInt("format_version"),
                rs.getString("ledger_id"),
                rs.getLong("seq"),
                d.readId(rs, "id"),
                d.readTime(rs, "sealed_at"),
                rs.getString("event_type"),
                rs.getString("payload"),
                rs.getString("payload_hash"),
                rs.getString("previous_hash"),
                rs.getString("key_id"),
                rs.getString("entry_hash"),
                rs.getString("signature"));
    }

    private boolean conflictExists(List<LedgerEntry> entries) {
        try (Connection c = dataSource.getConnection()) {
            // Batches have contiguous sequences, so a range query covers all of them.
            String rangeSql = "SELECT COUNT(*) FROM " + table + " WHERE seq >= ? AND seq <= ?";
            try (PreparedStatement ps = c.prepareStatement(rangeSql)) {
                ps.setLong(1, entries.get(0).sequence());
                ps.setLong(2, entries.get(entries.size() - 1).sequence());
                if (count(ps) > 0) {
                    return true;
                }
            }
            // An id can exist at another sequence when an earlier attempt was persisted.
            for (int start = 0; start < entries.size(); start += ID_CHUNK) {
                List<LedgerEntry> chunk = entries.subList(start, Math.min(entries.size(), start + ID_CHUNK));
                StringBuilder sql = new StringBuilder("SELECT COUNT(*) FROM ").append(table).append(" WHERE id IN (");
                for (int i = 0; i < chunk.size(); i++) {
                    sql.append(i == 0 ? "?" : ", ?");
                }
                sql.append(')');
                try (PreparedStatement ps = c.prepareStatement(sql.toString())) {
                    for (int i = 0; i < chunk.size(); i++) {
                        dialect.bindId(ps, i + 1, chunk.get(i).id());
                    }
                    if (count(ps) > 0) {
                        return true;
                    }
                }
            }
            return false;
        } catch (SQLException | RuntimeException e) {
            return false; // cannot tell: report the original failure, the writer will retry
        }
    }

    private static long count(PreparedStatement ps) throws SQLException {
        try (ResultSet rs = ps.executeQuery()) {
            return rs.next() ? rs.getLong(1) : 0;
        }
    }

    private void ensureInitialized() {
        if (initialized) {
            return;
        }
        synchronized (initLock) {
            if (initialized) {
                return;
            }
            dialect();
            if (options.isCreateTable()) {
                ensureTable();
            }
            initialized = true;
        }
    }

    private void ensureTable() {
        try (Connection c = dataSource.getConnection()) {
            boolean autoCommit = c.getAutoCommit();
            c.setAutoCommit(true); // a failed probe must not poison a transaction (PostgreSQL)
            try {
                if (tableExists(c)) {
                    return;
                }
                try {
                    for (String statement : createTableStatements()) {
                        try (Statement st = c.createStatement()) {
                            st.execute(statement);
                        }
                    }
                } catch (SQLException e) {
                    // Accept the failure only if another instance created the table concurrently.
                    if (!tableExists(c)) {
                        throw e;
                    }
                }
            } finally {
                restoreAutoCommit(c, autoCommit);
            }
        } catch (SQLException e) {
            throw failure("creating the table " + table, e);
        }
    }

    private boolean tableExists(Connection c) {
        try (Statement st = c.createStatement()) {
            st.executeQuery("SELECT 1 FROM " + table + " WHERE 1 = 0").close();
            return true;
        } catch (SQLException e) {
            return false;
        }
    }

    private static void rollbackQuietly(Connection c, Exception original) {
        try {
            c.rollback();
        } catch (SQLException e) {
            original.addSuppressed(e);
        }
    }

    private static void restoreAutoCommit(Connection c, boolean autoCommit) {
        try {
            if (c.getAutoCommit() != autoCommit) {
                c.setAutoCommit(autoCommit);
            }
        } catch (SQLException ignored) {
            // the connection is broken; the pool will discard it
        }
    }

    private LedgerStoreException failure(String operation, SQLException e) {
        return new LedgerStoreException("PROVANCE JDBC: " + operation + " in " + table + " failed: " + e.getMessage(), e);
    }
}
