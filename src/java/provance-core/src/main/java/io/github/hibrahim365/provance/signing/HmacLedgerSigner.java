package io.github.hibrahim365.provance.signing;

import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.MessageDigest;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

import javax.crypto.Mac;
import javax.crypto.spec.SecretKeySpec;

/**
 * HMAC-SHA256 signer.
 *
 * <p>HMAC is symmetric: whoever can verify can also sign. Combine it with external checkpoints, or replace
 * it with a {@link LedgerSigner} backed by a KMS, if the people who run the application must not be able to
 * rewrite history.
 */
public final class HmacLedgerSigner implements LedgerSigner {

    /** Minimum key length, in UTF-8 bytes. */
    public static final int MINIMUM_KEY_BYTES = 32;

    private static final String ALGORITHM = "HmacSHA256";

    private static final List<String> KNOWN_PLACEHOLDERS = List.of(
            "DEFAULT_INSECURE_KEY_CHANGE_ME",
            "CHANGE_ME",
            "CHANGE_ME_IN_PROD",
            "MY_SUPER_SECRET_LOCAL_DEV_KEY_12345");

    private final String keyId;
    private final SecretKeySpec signingKey;
    private final Map<String, SecretKeySpec> keys;

    /** Creates a signer with a single key. */
    public HmacLedgerSigner(String keyId, String secretKey) {
        this(keyId, secretKey, Map.of());
    }

    /**
     * Creates a signer with a current key and retired keys (key id to secret) used only to verify old entries.
     *
     * @throws IllegalStateException a key is missing, shorter than {@value #MINIMUM_KEY_BYTES} bytes or a known placeholder
     */
    public HmacLedgerSigner(String keyId, String secretKey, Map<String, String> retiredKeys) {
        if (keyId == null || keyId.isBlank()) {
            throw new IllegalStateException("PROVANCE: signingKeyId is required.");
        }
        this.keyId = keyId;
        this.signingKey = validateKey(keyId, secretKey);
        Map<String, SecretKeySpec> all = new HashMap<>();
        all.put(keyId, signingKey);
        if (retiredKeys != null) {
            for (Map.Entry<String, String> e : retiredKeys.entrySet()) {
                if (keyId.equals(e.getKey())) {
                    throw new IllegalStateException("PROVANCE: key id '" + e.getKey() + "' is both the signing key and a retired key.");
                }
                all.put(e.getKey(), validateKey(e.getKey(), e.getValue()));
            }
        }
        this.keys = Map.copyOf(all);
    }

    @Override
    public String keyId() {
        return keyId;
    }

    @Override
    public byte[] sign(byte[] digest) {
        return hmac(signingKey, digest);
    }

    @Override
    public SignatureCheck verify(String keyId, byte[] digest, byte[] signature) {
        SecretKeySpec key = keyId == null ? null : keys.get(keyId);
        if (key == null) {
            return SignatureCheck.UNKNOWN_KEY;
        }
        return MessageDigest.isEqual(hmac(key, digest), signature) ? SignatureCheck.VALID : SignatureCheck.INVALID;
    }

    private static byte[] hmac(SecretKeySpec key, byte[] data) {
        try {
            Mac mac = Mac.getInstance(ALGORITHM);
            mac.init(key);
            return mac.doFinal(data);
        } catch (GeneralSecurityException e) {
            throw new IllegalStateException("HMAC-SHA256 is not available in this JVM.", e);
        }
    }

    private static SecretKeySpec validateKey(String keyId, String secret) {
        if (secret == null || secret.isBlank()) {
            throw new IllegalStateException("PROVANCE: no secret configured for key '" + keyId
                    + "'. Set provance.secret-key from a secret store or an environment variable, or provide your own LedgerSigner.");
        }
        for (String placeholder : KNOWN_PLACEHOLDERS) {
            if (placeholder.equalsIgnoreCase(secret)) {
                throw new IllegalStateException("PROVANCE: the secret of key '" + keyId + "' is a placeholder value. Generate a real key.");
            }
        }
        byte[] bytes = secret.getBytes(StandardCharsets.UTF_8);
        if (bytes.length < MINIMUM_KEY_BYTES) {
            throw new IllegalStateException("PROVANCE: the secret of key '" + keyId + "' is " + bytes.length
                    + " bytes long, the minimum is " + MINIMUM_KEY_BYTES + ".");
        }
        return new SecretKeySpec(bytes, ALGORITHM);
    }
}
