package io.github.hibrahim365.provance.jdbc;

import org.testcontainers.containers.PostgreSQLContainer;

import javax.sql.DataSource;

/** PostgreSQL in a container (Docker required, skipped otherwise). */
class PostgreSqlJdbcStoreTests extends JdbcStoreContractTests {

    private static final SharedContainer CONTAINER = new SharedContainer(() -> new PostgreSQLContainer<>("postgres:16-alpine"));

    @Override
    protected DataSource dataSource() {
        return CONTAINER.dataSource();
    }

    @Override
    protected SqlDialect expectedDialect() {
        return SqlDialect.POSTGRESQL;
    }
}
