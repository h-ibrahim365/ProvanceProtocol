package io.github.hibrahim365.provance.jdbc;

import org.testcontainers.containers.MySQLContainer;

import javax.sql.DataSource;

/** MySQL in a container (Docker required, skipped otherwise). */
class MySqlJdbcStoreTests extends JdbcStoreContractTests {

    private static final SharedContainer CONTAINER = new SharedContainer(() -> new MySQLContainer<>("mysql:8.4"));

    @Override
    protected DataSource dataSource() {
        return CONTAINER.dataSource();
    }

    @Override
    protected SqlDialect expectedDialect() {
        return SqlDialect.MYSQL;
    }
}
