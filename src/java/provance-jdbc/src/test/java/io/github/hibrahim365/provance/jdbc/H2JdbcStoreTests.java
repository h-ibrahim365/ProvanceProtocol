package io.github.hibrahim365.provance.jdbc;

import javax.sql.DataSource;

/** H2 in memory. No Docker needed. */
class H2JdbcStoreTests extends JdbcStoreContractTests {

    private static final DataSource DATA_SOURCE = new SimpleDataSource("jdbc:h2:mem:provance;DB_CLOSE_DELAY=-1");

    @Override
    protected DataSource dataSource() {
        return DATA_SOURCE;
    }

    @Override
    protected SqlDialect expectedDialect() {
        return SqlDialect.H2;
    }
}
