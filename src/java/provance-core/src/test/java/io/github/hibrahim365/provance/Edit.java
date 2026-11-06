package io.github.hibrahim365.provance;

/** Copies of entries with one field changed, as an attacker editing rows would produce. */
final class Edit {

    private Edit() {
    }

    static LedgerEntry eventType(LedgerEntry e, String value) {
        return new LedgerEntry(e.formatVersion(), e.ledgerId(), e.sequence(), e.id(), e.timestamp(), value, e.payloadJson(),
                e.payloadHash(), e.previousHash(), e.keyId(), e.hash(), e.signature());
    }

    static LedgerEntry payload(LedgerEntry e, String payloadJson, String payloadHash) {
        return new LedgerEntry(e.formatVersion(), e.ledgerId(), e.sequence(), e.id(), e.timestamp(), e.eventType(), payloadJson,
                payloadHash, e.previousHash(), e.keyId(), e.hash(), e.signature());
    }

    static LedgerEntry hash(LedgerEntry e, String value) {
        return new LedgerEntry(e.formatVersion(), e.ledgerId(), e.sequence(), e.id(), e.timestamp(), e.eventType(), e.payloadJson(),
                e.payloadHash(), e.previousHash(), e.keyId(), value, e.signature());
    }

    static LedgerEntry sequence(LedgerEntry e, long value) {
        return new LedgerEntry(e.formatVersion(), e.ledgerId(), value, e.id(), e.timestamp(), e.eventType(), e.payloadJson(),
                e.payloadHash(), e.previousHash(), e.keyId(), e.hash(), e.signature());
    }

    static LedgerCheckpoint checkpointSequence(LedgerCheckpoint c, long value) {
        return new LedgerCheckpoint(c.formatVersion(), c.ledgerId(), value, c.hash(), c.createdAt(), c.keyId(), c.signature());
    }
}
