package io.github.hibrahim365.provance.jdbc;

import org.testcontainers.containers.MSSQLServerContainer;

import javax.sql.DataSource;

/** SQL Server in a container (Docker required, skipped otherwise). */
class SqlServerJdbcStoreTests extends JdbcStoreContractTests {

    private static final SharedContainer CONTAINER = new SharedContainer(() -> new MSSQLServerContainer<>("mcr.microsoft.com/mssql/server:2022-latest").acceptLicense());

    @Override
    protected DataSource dataSource() {
        return CONTAINER.dataSource();
    }

    @Override
    protected SqlDialect expectedDialect() {
        return SqlDialect.SQL_SERVER;
    }
}
