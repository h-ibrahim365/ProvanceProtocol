package io.github.hibrahim365.provance.jdbc;

import java.util.regex.Pattern;

/**
 * Options of {@link JdbcLedgerStore}. Same names and defaults as the .NET Entity Framework Core store.
 */
public class JdbcStoreOptions {

    /** Default table name, the same as the .NET store. */
    public static final String DEFAULT_TABLE_NAME = "provance_ledger";

    private static final Pattern IDENTIFIER = Pattern.compile("^[A-Za-z_][A-Za-z0-9_]{0,62}$");

    private String tableName = DEFAULT_TABLE_NAME;
    private String schema;
    private boolean createTable = true;
    private int readBatchSize = 1000;
    private SqlDialect dialect;

    /** Table name. Default {@code provance_ledger}. Letters, digits and underscores only. */
    public String getTableName() {
        return tableName;
    }

    /** @see #getTableName() */
    public void setTableName(String tableName) {
        this.tableName = tableName;
    }

    /** Optional schema of the table. */
    public String getSchema() {
        return schema;
    }

    /** @see #getSchema() */
    public void setSchema(String schema) {
        this.schema = schema;
    }

    /**
     * Create the table and its unique index on first use when the table does not exist. Default {@code true}.
     * Set it to {@code false} when the schema is managed by migrations (see {@link JdbcLedgerStore#createTableStatements()}).
     */
    public boolean isCreateTable() {
        return createTable;
    }

    /** @see #isCreateTable() */
    public void setCreateTable(boolean createTable) {
        this.createTable = createTable;
    }

    /** Number of rows fetched per query while streaming the ledger. Default 1000. */
    public int getReadBatchSize() {
        return readBatchSize;
    }

    /** @see #getReadBatchSize() */
    public void setReadBatchSize(int readBatchSize) {
        this.readBatchSize = readBatchSize;
    }

    /** SQL dialect. Default: detected from the database product name on first use. */
    public SqlDialect getDialect() {
        return dialect;
    }

    /** @see #getDialect() */
    public void setDialect(SqlDialect dialect) {
        this.dialect = dialect;
    }

    JdbcStoreOptions copy() {
        JdbcStoreOptions c = new JdbcStoreOptions();
        c.tableName = tableName;
        c.schema = schema;
        c.createTable = createTable;
        c.readBatchSize = readBatchSize;
        c.dialect = dialect;
        return c;
    }

    void validate() {
        if (tableName == null || !IDENTIFIER.matcher(tableName).matches()) {
            throw new IllegalStateException("PROVANCE JDBC: tableName must contain only letters, digits and underscores (max 63 characters).");
        }
        if (schema != null && !schema.isEmpty() && !IDENTIFIER.matcher(schema).matches()) {
            throw new IllegalStateException("PROVANCE JDBC: schema must contain only letters, digits and underscores (max 63 characters).");
        }
        if (readBatchSize < 1) {
            throw new IllegalStateException("PROVANCE JDBC: readBatchSize must be at least 1.");
        }
    }
}
