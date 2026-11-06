package io.github.hibrahim365.provance.signing;

/**
 * Signs entry and checkpoint digests. The default implementation is {@link HmacLedgerSigner}.
 *
 * <p>Implement this interface to keep the key in a KMS or HSM (AWS KMS, Azure Key Vault, Google Cloud KMS,
 * ...) so the application never holds it in clear. The writer calls {@link #sign(byte[])} once per entry:
 * a remote signer adds its latency to every append. Implementations must be thread safe.
 */
public interface LedgerSigner {

    /** Identifier of the key used by {@link #sign(byte[])}. Stored in every entry. */
    String keyId();

    /** Signs a 32 byte SHA-256 digest with the current key. */
    byte[] sign(byte[] digest);

    /** Checks a signature produced by the key {@code keyId}, current or retired. */
    SignatureCheck verify(String keyId, byte[] digest, byte[] signature);
}
