package com.tctools.unlock.protocol

/**
 * Notify 载荷重组 + 重放防护（协议第 3.2 / 2.1 节）。
 *
 * 纯逻辑，不依赖 BLE，可在 JVM 上单测（协议第 7 节要求"无真机也能验证消息结构"）。
 *
 * 兼容两种线格式：
 *  1. 未分片：整段载荷就是 `SEAL` 字节流（IV||ct||tag）；
 *  2. 分片：前 2 字节为大端总长度前缀，其后才是完整 `SEAL` 帧（第 3.2 节的务实约定）。
 *
 * 判定方式不靠猜长度，而是靠 **AEAD tag 校验** 做裁决：只有能解出明文且 counter 严格
 * 递增的解释才会被接受，其余一律 NeedMore / Rejected。
 */
class SealedFrameAssembler(private val key: ByteArray) {

    sealed class Outcome {
        /** 数据还不完整，等下一个分片。 */
        object NeedMore : Outcome()
        /** 成功解出一条 Host → 手机 的消息。 */
        data class Message(val counter: Long, val json: String, val plaintext: ByteArray, val raw: ByteArray) : Outcome()
        /** 结构非法 / 认证失败 / 重放，应丢弃并（按第 2.1 节）视为攻击。 */
        data class Rejected(val reason: String) : Outcome()
    }

    private var buf = ByteArray(0)

    /** 已成功解出的最大 counter（Host → 手机方向，第 2.1 节要求严格递增）。 */
    var lastCounter: Long = 0L
        private set

    val pendingBytes: Int get() = buf.size

    fun reset() {
        buf = ByteArray(0)
    }

    /** 新会话：清空缓冲与 counter 记录。 */
    fun resetForNewSession() {
        buf = ByteArray(0)
        lastCounter = 0L
    }

    fun feed(chunk: ByteArray): List<Outcome> {
        if (chunk.isEmpty()) return listOf(Outcome.NeedMore)
        buf += chunk
        val out = ArrayList<Outcome>(2)
        while (true) {
            val o = tryDecode() ?: break
            out.add(o)
            if (o !is Outcome.Message) break
        }
        if (out.isEmpty()) out.add(Outcome.NeedMore)
        // 防御：缓冲异常膨胀时判为攻击载荷
        if (buf.size > TcProtocol.MAX_REASSEMBLY_BYTES) {
            buf = ByteArray(0)
            out.add(Outcome.Rejected("帧长度超限（> ${TcProtocol.MAX_REASSEMBLY_BYTES} 字节）"))
        }
        return out
    }

    /** 返回 null 表示需要更多数据。 */
    private fun tryDecode(): Outcome? {
        // 解释 1：带 2 字节大端长度前缀的分片（第 3.2 节务实约定）
        if (buf.size >= TcProtocol.FRAGMENT_HEADER_LEN) {
            val total = ((buf[0].toInt() and 0xFF) shl 8) or (buf[1].toInt() and 0xFF)
            if (total >= TcProtocol.MIN_SEALED_LEN) {
                if (buf.size < TcProtocol.FRAGMENT_HEADER_LEN + total) {
                    // 前缀已声明长度但还没收全：此时**不能**把整段当作裸帧解释，
                    // 否则会拿长度前缀当 IV 去校验，误判成 bad-frame 并丢弃数据。
                    return null
                }
                val frame = buf.copyOfRange(TcProtocol.FRAGMENT_HEADER_LEN, TcProtocol.FRAGMENT_HEADER_LEN + total)
                val decoded = decodeFrame(frame)
                buf = buf.copyOfRange(TcProtocol.FRAGMENT_HEADER_LEN + total, buf.size)
                return decoded ?: Outcome.Rejected("长度前缀声明的分片无法解密（total=$total）")
            }
        }
        // 解释 2：整段就是裸 SEAL 帧（counter 很小时前 2 字节为 0x0000，不会与上面冲突）
        val raw = decodeFrame(buf.copyOf())
        if (raw != null) {
            buf = ByteArray(0)
            return raw
        }
        return null
    }

    private fun decodeFrame(frame: ByteArray): Outcome? {
        if (frame.size < TcProtocol.MIN_SEALED_LEN) return null
        // §2.1：IV 的后 4 字节必须为 0x00（与 Windows 端 HasValidIvPadding 行为对齐）
        if (!TcProtocol.hasValidIvPadding(frame)) {
            return Outcome.Rejected("IV 填充非 0x00（bad-frame）")
        }
        val counter = TcProtocol.counterOfSealedFrame(frame)
        if (counter <= lastCounter) {
            return Outcome.Rejected("重放：counter=$counter 未严格大于已接受的 $lastCounter")
        }
        val plain = TcProtocol.openWithEmbeddedIv(key, frame) ?: return null
        lastCounter = counter
        return Outcome.Message(
            counter = counter,
            json = String(plain, Charsets.UTF_8),
            plaintext = plain,
            raw = frame,
        )
    }
}
