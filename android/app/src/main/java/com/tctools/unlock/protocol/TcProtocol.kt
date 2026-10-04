package com.tctools.unlock.protocol

import java.util.Base64
import javax.crypto.Cipher
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * TC-tools Unlock 协议核心（v1.0）。
 *
 * 本文件是 Android 端对 `TC-tools/docs/UNLOCK-PROTOCOL.md` 的唯一实现入口。
 * 全部逻辑为纯 JVM 代码（不引用任何 android.* 类），因此可以在桌面 JVM 上
 * 直接跑回环自测（见 [LoopbackSelfTest] 与 `:app:selfTest` / `:app:testDebugUnitTest`）。
 *
 * 字节级要点（不得偏离）：
 *  - `K_session = HMAC-SHA256(PSK, NONCE || "TCUNLOCK-SESSION-V1")`
 *  - `PROOF     = HMAC-SHA256(PSK, NONCE || "TCUNLOCK-PROOF-V1" || HOST_ID || PEER_ID)`
 *  - 拼接顺序固定：NONCE 在前，标签居中，ID 在后；字符串取 ASCII 原始字节（不含 `\0`）
 *  - SEAL 线格式：`IV(12) || ciphertext(n) || tag(16)`，IV = counter(8 字节 BE) || 4 字节 0x00
 *  - base64url 编码去掉 `=` 填充
 */
object TcProtocol {

    // ---- 协议常量 ----------------------------------------------------------

    const val PROTOCOL_VERSION = 1
    const val PRODUCT_TAG = "tcunlock"

    const val LABEL_SESSION = "TCUNLOCK-SESSION-V1"
    const val LABEL_PROOF = "TCUNLOCK-PROOF-V1"

    const val SERVICE_UUID = "7a1c9e40-2f3d-4b6c-9a11-6c5d3e8f2b01"
    const val CHAR_CHALLENGE_UUID = "7a1c9e41-2f3d-4b6c-9a11-6c5d3e8f2b01"
    const val CHAR_COMMAND_UUID = "7a1c9e42-2f3d-4b6c-9a11-6c5d3e8f2b01"
    const val CCCD_UUID = "00002902-0000-1000-8000-00805f9b34fb"

    const val PSK_LEN = 32
    const val NONCE_LEN = 32
    const val PROOF_LEN = 32
    const val IV_LEN = 12
    const val TAG_LEN = 16
    const val GCM_TAG_BITS = 128

    /** 最小 SEALED 帧长度：IV(12) + 至少 1 字节密文 + tag(16) = 29。 */
    const val MIN_SEALED_LEN = IV_LEN + 1 + TAG_LEN
    /** 最小 `ct||tag` 主体长度（**不含 IV**）：1 + 16 = 17。 */
    const val MIN_BODY_LEN = 1 + TAG_LEN
    /** 协议第 3.2 节约定：Notify 载荷（未分片时）限制在 180 字节以内。 */
    const val MAX_NOTIFY_PAYLOAD = 180
    /** 分片模式下前 2 字节为大端总长度前缀。 */
    const val FRAGMENT_HEADER_LEN = 2
    /** 单次会话内允许的最大重组缓冲（防御性上限）。 */
    const val MAX_REASSEMBLY_BYTES = 4096

    /** 消息类型（第 3.3.2 节）。 */
    const val TYPE_UNLOCK = "unlock"
    const val TYPE_PING = "ping"
    const val TYPE_BYE = "bye"
    const val TYPE_READY = "ready"
    const val TYPE_UNLOCK_RESULT = "unlock_result"
    const val TYPE_PONG = "pong"
    const val TYPE_ERROR = "error"

    /** 已配对电脑上最多连续失败 5 次即失效 PSK（第 3.3.1 节，Host 侧行为）。 */
    const val MAX_CONSECUTIVE_AUTH_FAILURES = 5

    // ---- 字节工具 ----------------------------------------------------------

    /** 协议规定字符串一律取 ASCII 原始字节（不含结尾 `\0`）。 */
    fun ascii(text: String): ByteArray = text.toByteArray(Charsets.US_ASCII)

    fun utf8(text: String): ByteArray = text.toByteArray(Charsets.UTF_8)

    fun hex(bytes: ByteArray): String {
        val sb = StringBuilder(bytes.size * 2)
        for (b in bytes) {
            val v = b.toInt() and 0xFF
            sb.append(HEX_CHARS[v ushr 4])
            sb.append(HEX_CHARS[v and 0x0F])
        }
        return sb.toString()
    }

    fun unhex(text: String): ByteArray {
        val clean = text.filter { !it.isWhitespace() }
        require(clean.length % 2 == 0) { "hex 长度必须是偶数" }
        val out = ByteArray(clean.length / 2)
        for (i in out.indices) {
            out[i] = ((hexVal(clean[i * 2]) shl 4) or hexVal(clean[i * 2 + 1])).toByte()
        }
        return out
    }

    private fun hexVal(c: Char): Int = when (c) {
        in '0'..'9' -> c - '0'
        in 'a'..'f' -> c - 'a' + 10
        in 'A'..'F' -> c - 'A' + 10
        else -> throw IllegalArgumentException("非法 hex 字符: $c")
    }

    private val HEX_CHARS = "0123456789abcdef".toCharArray()

    /** 第 4.1 节 zigzag 编码规则：base64url，去掉 `=` 填充。 */
    fun base64UrlEncode(bytes: ByteArray): String =
        Base64.getUrlEncoder().withoutPadding().encodeToString(bytes)

    fun base64UrlDecode(text: String): ByteArray {
        val t = text.trim()
        val pad = (4 - t.length % 4) % 4
        return Base64.getUrlDecoder().decode(t + "=".repeat(pad))
    }

    /** GCM 用常量时间比较（第 3.3.1 节）。 */
    fun constantTimeEquals(a: ByteArray, b: ByteArray): Boolean =
        java.security.MessageDigest.isEqual(a, b)

    // ---- 密码学原语（第 2 节） ---------------------------------------------

    fun hmacSha256(key: ByteArray, data: ByteArray): ByteArray {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(key, "HmacSHA256"))
        return mac.doFinal(data)
    }

    /** `K_session = HMAC-SHA256(PSK, NONCE || "TCUNLOCK-SESSION-V1")` */
    fun deriveSessionKey(psk: ByteArray, nonce: ByteArray): ByteArray {
        require(psk.size == PSK_LEN) { "PSK 必须为 32 字节，实际 ${psk.size}" }
        require(nonce.size == NONCE_LEN) { "NONCE 必须为 32 字节，实际 ${nonce.size}" }
        val msg = ByteArray(nonce.size + LABEL_SESSION.length)
        System.arraycopy(nonce, 0, msg, 0, nonce.size)
        System.arraycopy(ascii(LABEL_SESSION), 0, msg, nonce.size, LABEL_SESSION.length)
        return hmacSha256(psk, msg)
    }

    /** `PROOF = HMAC-SHA256(PSK, NONCE || "TCUNLOCK-PROOF-V1" || HOST_ID || PEER_ID)` */
    fun computeProof(psk: ByteArray, nonce: ByteArray, hostId: String, peerId: String): ByteArray {
        require(psk.size == PSK_LEN) { "PSK 必须为 32 字节，实际 ${psk.size}" }
        require(nonce.size == NONCE_LEN) { "NONCE 必须为 32 字节，实际 ${nonce.size}" }
        val host = ascii(hostId)
        val peer = ascii(peerId)
        require(host.size == 36) { "HOST_ID 必须是 36 字节 UUID 字符串" }
        require(peer.size == 36) { "PEER_ID 必须是 36 字节 UUID 字符串" }
        val label = ascii(LABEL_PROOF)
        val msg = ByteArray(nonce.size + label.size + host.size + peer.size)
        var o = 0
        System.arraycopy(nonce, 0, msg, o, nonce.size); o += nonce.size
        System.arraycopy(label, 0, msg, o, label.size); o += label.size
        System.arraycopy(host, 0, msg, o, host.size); o += host.size
        System.arraycopy(peer, 0, msg, o, peer.size)
        return hmacSha256(psk, msg)
    }

    // ---- SEAL / OPEN（第 2.1 节） ------------------------------------------

    /** `IV = counter(8 字节 big-endian) || 4 字节 0x00`。 */
    fun ivFor(counter: Long): ByteArray {
        require(counter >= 1) { "counter 从 1 开始" }
        val iv = ByteArray(IV_LEN)
        var c = counter
        for (i in 7 downTo 0) {
            iv[i] = (c and 0xFF).toByte()
            c = c ushr 8
        }
        return iv
    }

    /** 从 SEALED 帧的前 12 字节 IV 还原 counter（用于重放校验与日志）。 */
    fun counterOfSealedFrame(frame: ByteArray): Long {
        require(frame.size >= MIN_SEALED_LEN) { "SEALED 帧至少 $MIN_SEALED_LEN 字节" }
        var c = 0L
        for (i in 0 until 8) c = (c shl 8) or (frame[i].toLong() and 0xFF)
        return c
    }

    /**
     * §2.1 规定 IV 的后 4 字节必须是 0x00。
     * Windows 端（Protocol.cs `HasValidIvPadding`）会校验并在非零时判 bad-frame；Android 端保持一致。
     */
    fun hasValidIvPadding(frame: ByteArray): Boolean {
        if (frame.size < IV_LEN) return false
        for (i in 8 until IV_LEN) {
            if (frame[i] != 0.toByte()) return false
        }
        return true
    }

    fun sealedFrameLength(plaintextLen: Int): Int = IV_LEN + plaintextLen + TAG_LEN

    /**
     * `SEAL(K_session, counter, json_utf8)` → `IV || ciphertext || tag`（无任何头部）。
     */
    fun seal(key: ByteArray, counter: Long, plaintext: ByteArray): ByteArray {
        val iv = ivFor(counter)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(GCM_TAG_BITS, iv))
        val body = cipher.doFinal(plaintext) // 输出 = ciphertext || tag(16)
        val out = ByteArray(IV_LEN + body.size)
        System.arraycopy(iv, 0, out, 0, IV_LEN)
        System.arraycopy(body, 0, out, IV_LEN, body.size)
        return out
    }

    /** 便捷重载：SEAL 一个 UTF-8 JSON 字符串。 */
    fun sealJson(key: ByteArray, counter: Long, json: String): ByteArray = seal(key, counter, utf8(json))

    /** `OPEN(key, counter, ciphertext)`：失败返回 null（调用方丢弃，第 2 节）。 */
    fun open(key: ByteArray, counter: Long, frame: ByteArray): ByteArray? = open(key, ivFor(counter), frame)

    fun open(key: ByteArray, iv: ByteArray, frame: ByteArray): ByteArray? {
        // 注意：此重载的 frame 是 ct||tag（不含 IV），最小长度 1+16=17，而非整帧的 29
        if (frame.size < MIN_BODY_LEN) return null
        return try {
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.DECRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(GCM_TAG_BITS, iv))
            cipher.doFinal(frame)
        } catch (t: Throwable) {
            null // AEADBadTagException / IllegalBlockSizeException 等都视为认证失败
        }
    }

    /** 用帧内自带的 IV 解密（Host → 手机方向；counter 由调用方另行做单调性校验）。 */
    fun openWithEmbeddedIv(key: ByteArray, frame: ByteArray): ByteArray? {
        if (frame.size < MIN_SEALED_LEN) return null
        val iv = frame.copyOfRange(0, IV_LEN)
        val body = frame.copyOfRange(IV_LEN, frame.size)
        return open(key, iv, body)
    }

    // ---- 会话编号（第 2.2 节） ---------------------------------------------

    /**
     * `SESSION_ID` = `OPEN(K_session, 1, host_ready.ciphertext)` 中 `sid` 字段的 4 字节十六进制小写。
     * 不参与安全判定；解析失败返回 null。
     */
    fun sessionIdFromReady(kSession: ByteArray, readyCiphertext: ByteArray): String? {
        val plain = openWithEmbeddedIv(kSession, readyCiphertext) ?: return null
        val obj = MiniJson.parseObject(String(plain, Charsets.UTF_8)) ?: return null
        val sid = obj["sid"] as? String ?: return null
        return sid.lowercase()
    }

    // ---- 明文载荷构造（第 3.3.1 / 3.3.2 节） -------------------------------

    /** PROOF 帧：32 字节明文。 */
    fun proofFrame(proof: ByteArray): ByteArray {
        require(proof.size == PROOF_LEN) { "PROOF 必须是 32 字节" }
        return proof
    }

    fun unlockJson(): String = MiniJson.obj("type" to TYPE_UNLOCK)

    fun pingJson(): String = MiniJson.obj("type" to TYPE_PING)

    fun byeJson(): String = MiniJson.obj("type" to TYPE_BYE)
}
