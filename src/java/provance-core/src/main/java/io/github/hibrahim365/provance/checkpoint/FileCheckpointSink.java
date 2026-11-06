package io.github.hibrahim365.provance.checkpoint;

import io.github.hibrahim365.provance.LedgerCheckpoint;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.FileAlreadyExistsException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;
import java.util.Objects;
import java.util.Locale;
import java.util.Optional;
import java.util.stream.Stream;

/**
 * Writes each checkpoint to {@code {directory}/{ledgerId}/{sequence}.json} and never overwrites a file.
 * The layout is the same as the .NET {@code FileCheckpointSink}.
 *
 * <p>Useful when the directory is mounted from immutable storage. On a plain local disk it only protects against
 * mistakes, not against someone with access to the machine.
 */
public final class FileCheckpointSink implements LedgerCheckpointSink {

    private final Path directory;

    /** Creates a sink writing under {@code directory}. */
    public FileCheckpointSink(Path directory) {
        this.directory = Objects.requireNonNull(directory, "directory");
    }

    @Override
    public void publish(LedgerCheckpoint checkpoint) throws IOException {
        Path folder = folder(directory, checkpoint.ledgerId());
        Files.createDirectories(folder);
        Path path = folder.resolve(fileName(checkpoint.sequence()));
        if (Files.exists(path)) {
            return; // same head already published
        }
        try {
            Files.write(path, checkpoint.toJson().getBytes(StandardCharsets.UTF_8),
                    StandardOpenOption.CREATE_NEW, StandardOpenOption.WRITE);
        } catch (FileAlreadyExistsException e) {
            // Another instance published the same head concurrently.
        }
    }

    /** Reads the checkpoint with the highest sequence for a ledger, or empty if there is none. */
    public static Optional<LedgerCheckpoint> readLatest(Path directory, String ledgerId) throws IOException {
        Path folder = folder(directory, ledgerId);
        if (!Files.isDirectory(folder)) {
            return Optional.empty();
        }
        Path latest = null;
        long latestSequence = 0;
        try (Stream<Path> files = Files.list(folder)) {
            for (Path p : (Iterable<Path>) files::iterator) {
                String name = p.getFileName().toString();
                if (!name.endsWith(".json")) {
                    continue;
                }
                long sequence = parseSequence(name.substring(0, name.length() - 5));
                if (sequence > latestSequence) {
                    latestSequence = sequence;
                    latest = p;
                }
            }
        }
        if (latest == null) {
            return Optional.empty();
        }
        return Optional.of(LedgerCheckpoint.fromJson(Files.readString(latest, StandardCharsets.UTF_8)));
    }

    private static Path folder(Path directory, String ledgerId) {
        if (ledgerId == null || ledgerId.isBlank() || ledgerId.equals(".") || ledgerId.equals("..")
                || ledgerId.contains("/") || ledgerId.contains("\\")) {
            throw new IllegalArgumentException("Ledger id '" + ledgerId + "' cannot be used as a folder name.");
        }
        return directory.resolve(ledgerId);
    }

    private static long parseSequence(String text) {
        if (text.isEmpty()) {
            return -1;
        }
        for (int i = 0; i < text.length(); i++) {
            if (text.charAt(i) < '0' || text.charAt(i) > '9') {
                return -1;
            }
        }
        try {
            return Long.parseLong(text);
        } catch (NumberFormatException e) {
            return -1;
        }
    }

    private static String fileName(long sequence) {
        return String.format(Locale.ROOT, "%016d.json", sequence);
    }
}
