package com.tctools.unlock.data

import android.content.Context
import android.content.SharedPreferences
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import android.util.Log
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey
import com.tctools.unlock.protocol.TcProtocol
import java.security.KeyStore
import java.util.UUID
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * 配对信息存储（协议第 4.2 节）。
 *
 * 主通道：`EncryptedSharedPreferences`（androidx.security:security-crypto 1.1.0-alpha06）。
 * 后备通道：若 EncryptedSharedPreferences 初始化失败（个别 ROM/keystore 异常），自动降级为
 * 「Android Keystore(AES-256-GCM) 自加密 → 普通 SharedPreferences」，PSK 依然不以明文落盘；
 * 实际使用的通道通过 [storageMode] 暴露给「关于」页，便于排查。
 */
class PairingStore(context: Context) {

    data class Pairing(
        val hostId: String,
        val hostName: String,
        val psk: ByteArray,
        val peerId: String,
        val peerName: String,
        val pairedAt: Long,
    )

    private companion object {
        const val TAG = "TcPairingStore"
        const val SECURE_FILE = "tc_unlock_secure"
        const val FALLBACK_FILE = "tc_unlock_fallback"
        const val KEY_HOST_ID = "host_id"
        const val KEY_HOST_NAME = "host_name"
        const val KEY_PSK = "psk_b64url"
        const val KEY_PEER_ID = "peer_id"
        const val KEY_PEER_NAME = "peer_name"
        const val KEY_PAIRED_AT = "paired_at"
    }

    private val appContext = context.applicationContext
    private val cipher: KeystoreCipher?
    private val prefs: SharedPreferences

    /** 实际生效的存储通道（写入「关于」页）。 */
    val storageMode: String

    init {
        var c: KeystoreCipher? = null
        var p: SharedPreferences? = null
        var mode = "未知"
        try {
            val masterKey = MasterKey.Builder(appContext)
                .setKeyScheme(MasterKey.KeyScheme.AES256_GCM)
                .build()
            p = EncryptedSharedPreferences.create(
                appContext,
                SECURE_FILE,
                masterKey,
                EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
                EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM,
            )
            mode = "EncryptedSharedPreferences (AES256-SIV / AES256-GCM)"
        } catch (t: Throwable) {
            Log.w(TAG, "EncryptedSharedPreferences 不可用，降级为 Keystore+AES-GCM", t)
        }
        if (p == null) {
            c = KeystoreCipher()
            p = appContext.getSharedPreferences(FALLBACK_FILE, Context.MODE_PRIVATE)
            mode = "Android Keystore AES-256-GCM + SharedPreferences（后备通道）"
        }
        cipher = c
        prefs = p
        storageMode = mode
    }

    private fun putString(key: String, value: String): Boolean {
        val stored = cipher?.encrypt(value) ?: value
        return prefs.edit().putString(key, stored).commit()
    }

    private fun getString(key: String): String? {
        val raw = prefs.getString(key, null) ?: return null
        return cipher?.decrypt(raw) ?: raw
    }

    fun load(): Pairing? = try {
        val hostId = getString(KEY_HOST_ID)
        val hostName = getString(KEY_HOST_NAME)
        val pskText = getString(KEY_PSK)
        if (hostId == null || hostName == null || pskText == null) {
            null
        } else {
            val psk = TcProtocol.base64UrlDecode(pskText)
            if (psk.size != TcProtocol.PSK_LEN) {
                Log.w(TAG, "PSK 长度异常：${psk.size}")
                null
            } else {
                Pairing(
                    hostId = hostId,
                    hostName = hostName,
                    psk = psk,
                    peerId = peerId(),
                    peerName = getString(KEY_PEER_NAME) ?: defaultPeerName(),
                    pairedAt = prefs.getLong(KEY_PAIRED_AT, 0L),
                )
            }
        }
    } catch (t: Throwable) {
        Log.w(TAG, "读取配对信息失败", t)
        null
    }

    fun save(hostId: String, hostName: String, psk: ByteArray): Pairing {
        require(psk.size == TcProtocol.PSK_LEN) { "PSK 必须 32 字节" }
        val peerId = getString(KEY_PEER_ID) ?: newPeerId()
        val peerName = defaultPeerName()
        putString(KEY_HOST_ID, hostId.lowercase())
        putString(KEY_HOST_NAME, hostName)
        putString(KEY_PSK, TcProtocol.base64UrlEncode(psk))
        putString(KEY_PEER_ID, peerId)
        putString(KEY_PEER_NAME, peerName)
        prefs.edit().putLong(KEY_PAIRED_AT, System.currentTimeMillis()).commit()
        return Pairing(
            hostId = hostId.lowercase(),
            hostName = hostName,
            psk = psk,
            peerId = peerId,
            peerName = peerName,
            pairedAt = prefs.getLong(KEY_PAIRED_AT, 0L),
        )
    }

    /** 忘记这台电脑：清空全部配对数据（重新配对会生成新 PSK）。 */
    fun clear() {
        prefs.edit().clear().commit()
        try {
            appContext.deleteSharedPreferences(SECURE_FILE)
        } catch (_: Throwable) {
        }
        try {
            appContext.deleteSharedPreferences(FALLBACK_FILE)
        } catch (_: Throwable) {
        }
    }

    /** 本机（Peer）身份：UUID v4 小写（协议第 4.2 节，配对时上报 Host）。 */
    fun newPeerId(): String = UUID.randomUUID().toString().lowercase()

    /**
     * 本机 PEER_ID：首次生成后持久化，**终身不变**（参与 PROOF 计算，换了就无法解锁）。
     * 认证前会以 36 字节 ASCII 写入 Command 特征（IDENT 帧，协议第 3.3.1 节）。
     */
    fun peerId(): String {
        val existing = getString(KEY_PEER_ID)
        if (existing != null && existing.length == 36) return existing.lowercase()
        val id = newPeerId()
        putString(KEY_PEER_ID, id)
        return id
    }

    private fun defaultPeerName(): String = try {
        "${android.os.Build.MANUFACTURER} ${android.os.Build.MODEL}".trim()
    } catch (_: Throwable) {
        "Android"
    }
}

/** 后备通道使用的 Keystore AES-256-GCM 加解密（IV 与密文一起以 base64 落盘）。 */
private class KeystoreCipher {

    private companion object {
        const val KEY_ALIAS = "tc_unlock_psk_key_v1"
        const val GCM_TAG_BITS = 128
        const val IV_LEN = 12
        const val TAG = "TcKeystoreCipher"
    }

    private fun secretKey(): SecretKey {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getEntry(KEY_ALIAS, null) as? KeyStore.SecretKeyEntry)?.let { return it.secretKey }
        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        generator.init(
            KeyGenParameterSpec.Builder(
                KEY_ALIAS,
                KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
            )
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build()
        )
        return generator.generateKey()
    }

    fun encrypt(plain: String): String {
        val c = Cipher.getInstance("AES/GCM/NoPadding")
        c.init(Cipher.ENCRYPT_MODE, secretKey())
        val body = c.doFinal(plain.toByteArray(Charsets.UTF_8))
        return Base64.encodeToString(c.iv + body, Base64.NO_WRAP)
    }

    fun decrypt(blob: String): String? = try {
        val all = Base64.decode(blob, Base64.NO_WRAP)
        val iv = all.copyOfRange(0, IV_LEN)
        val body = all.copyOfRange(IV_LEN, all.size)
        val c = Cipher.getInstance("AES/GCM/NoPadding")
        c.init(Cipher.DECRYPT_MODE, secretKey(), GCMParameterSpec(GCM_TAG_BITS, iv))
        String(c.doFinal(body), Charsets.UTF_8)
    } catch (t: Throwable) {
        Log.w(TAG, "后备存储解密失败（可能 keystore 被清除，需重新配对）", t)
        null
    }
}
