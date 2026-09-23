package com.playervault;

import android.content.Context;
import android.content.SharedPreferences;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;

import java.security.KeyStore;
import java.security.SecureRandom;

import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

/**
 * The Android half of PlayerVault's save signing. Called from AndroidKeystoreKeyStore in C#.
 *
 * Each player's signing key is 32 random bytes, stored in private SharedPreferences encrypted
 * with an AES key that lives in the Android Keystore and cannot be exported. The save counter
 * sits next to it. Neither can be read or changed through adb on a phone that is not rooted.
 *
 * If the Keystore no longer has the AES key, for example after a backup was restored onto
 * another phone, the stored key cannot be decrypted and a new one is created. The existing save
 * then fails its check, which is the intended result: it was not signed on this device. Any other
 * failure is thrown, so a Keystore that is briefly unavailable fails the open instead of
 * replacing a good key.
 */
public final class PlayerVaultKeyStore {
    private static final String KEYSTORE = "AndroidKeyStore";
    private static final String WRAPPING_ALIAS = "playervault.wrapping";
    private static final String PREFERENCES = "playervault.integrity";
    private static final int KEY_LENGTH = 32;
    private static final int IV_LENGTH = 12;
    private static final int TAG_BITS = 128;

    private PlayerVaultKeyStore() {
    }

    /** Returns the player's signing key as Base64, creating it on first use. */
    public static synchronized String getOrCreateKey(Context context, String playerId) throws Exception {
        SharedPreferences preferences = preferences(context);

        KeyStore store = KeyStore.getInstance(KEYSTORE);
        store.load(null);
        boolean wrappingKeyExisted = store.containsAlias(WRAPPING_ALIAS);
        SecretKey wrapping = wrappingKeyExisted ? (SecretKey) store.getKey(WRAPPING_ALIAS, null) : createWrappingKey();

        String stored = preferences.getString("key." + playerId, null);
        if (stored != null && wrappingKeyExisted) {
            byte[] sealed = Base64.decode(stored, Base64.NO_WRAP);
            Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
            cipher.init(Cipher.DECRYPT_MODE, wrapping, new GCMParameterSpec(TAG_BITS, sealed, 0, IV_LENGTH));
            byte[] key = cipher.doFinal(sealed, IV_LENGTH, sealed.length - IV_LENGTH);
            return Base64.encodeToString(key, Base64.NO_WRAP);
        }

        byte[] key = new byte[KEY_LENGTH];
        new SecureRandom().nextBytes(key);

        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
        cipher.init(Cipher.ENCRYPT_MODE, wrapping);
        byte[] iv = cipher.getIV();
        byte[] encrypted = cipher.doFinal(key);

        byte[] sealed = new byte[iv.length + encrypted.length];
        System.arraycopy(iv, 0, sealed, 0, iv.length);
        System.arraycopy(encrypted, 0, sealed, iv.length, encrypted.length);

        if (!preferences.edit().putString("key." + playerId, Base64.encodeToString(sealed, Base64.NO_WRAP)).commit()) {
            throw new IllegalStateException("Could not store the PlayerVault signing key.");
        }

        return Base64.encodeToString(key, Base64.NO_WRAP);
    }

    /** The highest save number recorded for the player, or 0. */
    public static synchronized long readCounter(Context context, String playerId) {
        return preferences(context).getLong("counter." + playerId, 0L);
    }

    /** Records a save number. Never lowers the stored value. */
    public static synchronized boolean writeCounter(Context context, String playerId, long counter) {
        SharedPreferences preferences = preferences(context);
        if (counter <= preferences.getLong("counter." + playerId, 0L)) return true;
        if (!preferences.edit().putLong("counter." + playerId, counter).commit()) {
            throw new IllegalStateException("Could not store the PlayerVault save counter.");
        }
        return true;
    }

    /** Removes the player's key and counter. */
    public static synchronized boolean delete(Context context, String playerId) {
        preferences(context).edit().remove("key." + playerId).remove("counter." + playerId).commit();
        return true;
    }

    private static SharedPreferences preferences(Context context) {
        return context.getApplicationContext().getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE);
    }

    private static SecretKey createWrappingKey() throws Exception {
        KeyGenerator generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, KEYSTORE);
        generator.init(new KeyGenParameterSpec.Builder(
                WRAPPING_ALIAS, KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build());
        return generator.generateKey();
    }
}
