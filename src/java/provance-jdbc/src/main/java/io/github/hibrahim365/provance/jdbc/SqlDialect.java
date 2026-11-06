package io.github.hibrahim365.provance.jdbc;

import java.sql.PreparedStatement;
import java.sql.ResultSet;
import java.sql.SQLException;
import java.sql.Timestamp;
import java.time.Instant;
import java.time.LocalDateTime;
import java.time.OffsetDateTime;
import java.time.ZoneOffset;
import java.time.format.DateTimeFormatter;
import java.util.Calendar;
import java.util.List;
import java.util.Locale;
import java.util.TimeZone;
import java.util.UUID;

/**
 * Database specific details of {@link JdbcLedgerStore}: column types and how ids and timestamps are bound.
 * Every query is plain SQL shared by all dialects.
 *
 * <p>Column names, and on PostgreSQL, SQL Server, MySQL and SQLite the column types, are the same as the table
 * created by the .NET Entity Framework Core store.
 */
public enum SqlDialect {

    /** PostgreSQL: {@code uuid}, {@code timestamp with time zone}, {@code text}. */
    POSTGRESQL("uuid", "integer", "varchar(128)", "bigint", "timestamp with time zone", "varchar(200)", "text",
            "varchar(64)", "varchar(1024)", "", UuidBinding.OBJECT, TimeBinding.OFFSET),

    /** SQL Server and Azure SQL: {@code uniqueidentifier}, {@code datetimeoffset}, {@code nvarchar(max)}. */
    SQL_SERVER("uniqueidentifier", "int", "nvarchar(128)", "bigint", "datetimeoffset", "nvarchar(200)", "nvarchar(max)",
            "nvarchar(64)", "nvarchar(1024)", "", UuidBinding.STRING, TimeBinding.OFFSET),

    /** MySQL and MariaDB: {@code char(36)}, {@code datetime(6)} in UTC, {@code longtext}, utf8mb4. */
    MYSQL("char(36)", "int", "varchar(128)", "bigint", "datetime(6)", "varchar(200)", "longtext",
            "varchar(64)", "varchar(1024)", " CHARACTER SET utf8mb4 COLLATE utf8mb4_bin", UuidBinding.STRING, TimeBinding.LOCAL_UTC),

    /** Oracle 12c and later: {@code varchar2(36)}, {@code timestamp with time zone}, {@code nclob}. */
    ORACLE("varchar2(36)", "number(10)", "nvarchar2(128)", "number(19)", "timestamp(6) with time zone", "nvarchar2(200)", "nclob",
            "varchar2(64)", "varchar2(1024)", "", UuidBinding.STRING, TimeBinding.OFFSET),

    /** SQLite: text columns, ids in upper case and timestamps formatted like Entity Framework Core does. */
    SQLITE("text", "integer", "text", "integer", "text", "text", "text",
            "text", "text", "", UuidBinding.STRING_UPPER, TimeBinding.TEXT),

    /** H2: {@code uuid}, {@code timestamp with time zone}, {@code clob}. */
    H2("uuid", "integer", "varchar(128)", "bigint", "timestamp(6) with time zone", "varchar(200)", "clob",
            "varchar(64)", "varchar(1024)", "", UuidBinding.OBJECT, TimeBinding.OFFSET),

    /** Any other database with standard SQL types (Derby, DB2, ...): {@code char(36)}, {@code timestamp} in UTC, {@code clob}. */
    GENERIC("char(36)", "integer", "varchar(128)", "bigint", "timestamp", "varchar(200)", "clob",
            "varchar(64)", "varchar(1024)", "", UuidBinding.STRING, TimeBinding.TIMESTAMP_UTC);

    private enum UuidBinding { OBJECT, STRING, STRING_UPPER }

    private enum TimeBinding { OFFSET, LOCAL_UTC, TEXT, TIMESTAMP_UTC }

    private static final DateTimeFormatter SQLITE_SECONDS = DateTimeFormatter.ofPattern("uuuu-MM-dd HH:mm:ss", Locale.ROOT);

    private final String idType;
    private final String intType;
    private final String ledgerIdType;
    private final String sequenceType;
    private final String timeType;
    private final String eventType;
    private final String payloadType;
    private final String hashType;
    private final String signatureType;
    private final String tableSuffix;
    private final UuidBinding uuidBinding;
    private final TimeBinding timeBinding;

    SqlDialect(String idType, String intType, String ledgerIdType, String sequenceType, String timeType, String eventType,
               String payloadType, String hashType, String signatureType, String tableSuffix,
               UuidBinding uuidBinding, TimeBinding timeBinding) {
        this.idType = idType;
        this.intType = intType;
        this.ledgerIdType = ledgerIdType;
        this.sequenceType = sequenceType;
        this.timeType = timeType;
        this.eventType = eventType;
        this.payloadType = payloadType;
        this.hashType = hashType;
        this.signatureType = signatureType;
        this.tableSuffix = tableSuffix;
        this.uuidBinding = uuidBinding;
        this.timeBinding = timeBinding;
    }

    /** Picks the dialect from {@link java.sql.DatabaseMetaData#getDatabaseProductName()}. */
    public static SqlDialect fromProductName(String productName) {
        String p = productName == null ? "" : productName.toLowerCase(Locale.ROOT);
        if (p.contains("postgres")) return POSTGRESQL;
        if (p.contains("microsoft sql server") || p.contains("sql server")) return SQL_SERVER;
        if (p.contains("mysql") || p.contains("mariadb")) return MYSQL;
        if (p.contains("oracle")) return ORACLE;
        if (p.contains("sqlite")) return SQLITE;
        if (p.equals("h2")) return H2;
        return GENERIC;
    }

    /**
     * Statements that create the ledger table and its unique index on the sequence. Use them in your migrations
     * (Flyway, Liquibase) when automatic creation is disabled. The unique index is required: it keeps the chain
     * linear when several instances write at the same time.
     */
    public List<String> createTableStatements(String table, String indexName) {
        String create = "CREATE TABLE " + table + " ("
                + "id " + idType + " NOT NULL, "
                + "format_version " + intType + " NOT NULL, "
                + "ledger_id " + ledgerIdType + " NOT NULL, "
                + "seq " + sequenceType + " NOT NULL, "
                + "sealed_at " + timeType + " NOT NULL, "
                + "event_type " + eventType + " NOT NULL, "
                + "payload " + payloadType + ", "
                + "payload_hash " + hashType + " NOT NULL, "
                + "previous_hash " + hashType + " NOT NULL, "
                + "key_id " + ledgerIdType + " NOT NULL, "
                + "entry_hash " + hashType + " NOT NULL, "
                + "signature " + signatureType + " NOT NULL, "
                + "redacted_at " + timeType + ", "
                + "PRIMARY KEY (id))" + tableSuffix;
        String index = "CREATE UNIQUE INDEX " + indexName + " ON " + table + " (seq)";
        return List.of(create, index);
    }

    void bindId(PreparedStatement ps, int index, UUID id) throws SQLException {
        switch (uuidBinding) {
            case OBJECT:
                ps.setObject(index, id);
                break;
            case STRING_UPPER:
                ps.setString(index, id.toString().toUpperCase(Locale.ROOT));
                break;
            default:
                ps.setString(index, id.toString());
                break;
        }
    }

    UUID readId(ResultSet rs, String column) throws SQLException {
        String value = rs.getString(column);
        return value == null ? null : UUID.fromString(value.trim());
    }

    void bindTime(PreparedStatement ps, int index, Instant instant) throws SQLException {
        switch (timeBinding) {
            case OFFSET:
                ps.setObject(index, OffsetDateTime.ofInstant(instant, ZoneOffset.UTC));
                break;
            case LOCAL_UTC:
                ps.setObject(index, LocalDateTime.ofInstant(instant, ZoneOffset.UTC));
                break;
            case TEXT:
                ps.setString(index, formatSqliteTime(instant));
                break;
            default:
                ps.setTimestamp(index, Timestamp.from(instant), utcCalendar());
                break;
        }
    }

    Instant readTime(ResultSet rs, String column) throws SQLException {
        switch (timeBinding) {
            case OFFSET: {
                OffsetDateTime value = rs.getObject(column, OffsetDateTime.class);
                return value == null ? null : value.toInstant();
            }
            case LOCAL_UTC: {
                LocalDateTime value = rs.getObject(column, LocalDateTime.class);
                return value == null ? null : value.toInstant(ZoneOffset.UTC);
            }
            case TEXT: {
                String value = rs.getString(column);
                return value == null ? null : parseSqliteTime(value);
            }
            default: {
                Timestamp value = rs.getTimestamp(column, utcCalendar());
                return value == null ? null : value.toInstant();
            }
        }
    }

    /** Same text as Entity Framework Core on SQLite: {@code 2026-01-15 09:30:00.123+00:00}, fraction trimmed. */
    static String formatSqliteTime(Instant instant) {
        OffsetDateTime t = OffsetDateTime.ofInstant(instant, ZoneOffset.UTC);
        StringBuilder sb = new StringBuilder(SQLITE_SECONDS.format(t));
        int nanos = t.getNano();
        if (nanos != 0) {
            String fraction = String.format(Locale.ROOT, "%09d", nanos);
            int end = fraction.length();
            while (end > 0 && fraction.charAt(end - 1) == '0') end--;
            sb.append('.').append(fraction, 0, end);
        }
        return sb.append("+00:00").toString();
    }

    static Instant parseSqliteTime(String text) {
        String t = text.trim().replace(' ', 'T');
        if (t.endsWith("Z") || t.matches(".*[+-]\\d{2}:\\d{2}$")) {
            return OffsetDateTime.parse(t).toInstant();
        }
        return LocalDateTime.parse(t).toInstant(ZoneOffset.UTC);
    }

    private static Calendar utcCalendar() {
        return Calendar.getInstance(TimeZone.getTimeZone("UTC"), Locale.ROOT);
    }
}
