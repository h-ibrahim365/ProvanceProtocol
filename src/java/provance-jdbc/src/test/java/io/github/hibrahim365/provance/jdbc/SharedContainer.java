package io.github.hibrahim365.provance.jdbc;

import org.junit.jupiter.api.Assumptions;
import org.testcontainers.DockerClientFactory;
import org.testcontainers.containers.JdbcDatabaseContainer;

import java.util.function.Supplier;

import javax.sql.DataSource;

/** One container per database for the whole test run, started on first use. Tests are skipped without Docker. */
final class SharedContainer {

    private final Supplier<JdbcDatabaseContainer<?>> factory;
    private DataSource dataSource;

    SharedContainer(Supplier<JdbcDatabaseContainer<?>> factory) {
        this.factory = factory;
    }

    synchronized DataSource dataSource() {
        Assumptions.assumeTrue(DockerClientFactory.instance().isDockerAvailable(), "Docker is not available");
        if (dataSource == null) {
            JdbcDatabaseContainer<?> container = factory.get();
            container.start();
            dataSource = new SimpleDataSource(container.getJdbcUrl(), container.getUsername(), container.getPassword());
        }
        return dataSource;
    }
}
