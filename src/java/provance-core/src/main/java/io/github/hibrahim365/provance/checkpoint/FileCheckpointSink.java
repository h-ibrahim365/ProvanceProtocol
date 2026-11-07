package io.github.hibrahim365.provance.checkpoint;

import io.github.hibrahim365.provance.LedgerCheckpoint;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.FileAlreadyExistsException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.nio.file.StandardOpenOption;
import java.nio.channels.FileChannel;
import java.nio.ByteBuffer;
import java.util.UUID;
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
        if (isValid(path, checkpoint)) {
            return; // same head already published
        }

        // Written to a temporary file, then moved into place: a crash never leaves a truncated checkpoint behind.
        Path temp = folder.resolve("." + path.getFileName() + "." + UUID.randomUUID() + ".tmp");
        try {
            try (FileChannel channel = FileChannel.open(temp, StandardOpenOption.CREATE_NEW, StandardOpenOption.WRITE)) {
                ByteBuffer bytes = ByteBuffer.wrap(checkpoint.toJson().getBytes(StandardCharsets.UTF_8));
                while (bytes.hasRemaining()) {
                    channel.write(bytes);
                }
                channel.force(true);
            }
            try {
                Files.createLink(path, temp); // fails if the file exists; a rename would replace it
            } catch (FileAlreadyExistsException e) {
                replaceIfBroken(temp, path, checkpoint);
            } catch (UnsupportedOperationException | IOException e) {
                // File systems without hard links.
                if (Files.exists(path)) {
                    replaceIfBroken(temp, path, checkpoint);
                } else {
                    try {
                        Files.move(temp, path);
                    } catch (FileAlreadyExistsException e2) {
                        replaceIfBroken(temp, path, checkpoint);
                    }
                }
            }
        } finally {
            Files.deleteIfExists(temp);
        }
    }

    /** Published concurrently, or a broken file left by a crash of an older version: only the latter is replaced. */
    private static void replaceIfBroken(Path temp, Path path, LedgerCheckpoint checkpoint) throws IOException {
        if (!isValid(path, checkpoint)) {
            Files.move(temp, path, StandardCopyOption.REPLACE_EXISTING);
        }
    }

    private static boolean isValid(Path path, LedgerCheckpoint expected) {
        Optional<LedgerCheckpoint> existing = tryRead(path);
        return existing.isPresent() && existing.get().sequence() == expected.sequence()
                && existing.get().ledgerId().equals(expected.ledgerId());
    }

    private static Optional<LedgerCheckpoint> tryRead(Path path) {
        try {
            return Optional.of(LedgerCheckpoint.fromJson(Files.readString(path, StandardCharsets.UTF_8)));
        } catch (IOException | RuntimeException e) {
            return Optional.empty();
        }
    }

    /**
     * Reads the checkpoint with the highest sequence for a ledger, or empty if there is none. A file that does not
     * parse (left by a crash) is skipped rather than hiding the older valid ones.
     */
    public static Optional<LedgerCheckpoint> readLatest(Path directory, String ledgerId) throws IOException {
        Path folder = folder(directory, ledgerId);
        if (!Files.isDirectory(folder)) {
            return Optional.empty();
        }
        java.util.TreeMap<Long, Path> bySequence = new java.util.TreeMap<>();
        try (Stream<Path> files = Files.list(folder)) {
            for (Path p : (Iterable<Path>) files::iterator) {
                String name = p.getFileName().toString();
                if (!name.endsWith(".json")) {
                    continue;
                }
                long sequence = parseSequence(name.substring(0, name.length() - 5));
                if (sequence > 0) {
                    bySequence.put(sequence, p);
                }
            }
        }
        for (Path p : bySequence.descendingMap().values()) {
            Optional<LedgerCheckpoint> checkpoint = tryRead(p);
            if (checkpoint.isPresent()) {
                return checkpoint;
            }
        }
        return Optional.empty();
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
