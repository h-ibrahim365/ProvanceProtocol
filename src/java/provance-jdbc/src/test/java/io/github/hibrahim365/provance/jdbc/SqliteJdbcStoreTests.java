package io.github.hibrahim365.provance.jdbc;

import java.io.IOException;
import java.io.UncheckedIOException;
import java.nio.file.Files;
import java.nio.file.Path;

import javax.sql.DataSource;

/** SQLite file database. No Docker needed. */
class SqliteJdbcStoreTests extends JdbcStoreContractTests {

    private static final DataSource DATA_SOURCE = new SimpleDataSource("jdbc:sqlite:" + tempFile() + "?busy_timeout=10000");

    private static Path tempFile() {
        try {
            Path file = Files.createTempFile("provance-", ".db");
            file.toFile().deleteOnExit();
            return file;
        } catch (IOException e) {
            throw new UncheckedIOException(e);
        }
    }

    @Override
    protected DataSource dataSource() {
        return DATA_SOURCE;
    }

    @Override
    protected SqlDialect expectedDialect() {
        return SqlDialect.SQLITE;
    }

    @Override
    protected int concurrentWritesPerInstance() {
        return 20;
    }
}
