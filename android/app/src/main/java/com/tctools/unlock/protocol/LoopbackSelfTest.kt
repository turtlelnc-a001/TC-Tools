package com.tctools.unlock.protocol

/**
 * 协议第 8 节「回环自测向量」的 Android 端实现（纯 JVM，可在桌面运行）。
 *
 * 期望值来源：**验证负责人（verify）用第三种独立实现（Node.js 内置 crypto）算出并回填
 * `docs/UNLOCK-PROTOCOL.md` §8 的权威值**，本文件只是把它们固化成断言，绝不由本端输出反推。
 * 交叉核对命令：
 *   node TC-tools\tests\unlock\ref-vectors.mjs --compare <本端输出的 JSON>
 */
object LoopbackSelfTest {

    // ---- 权威期望值（docs/UNLOCK-PROTOCOL.md §8，verify/task-4 冻结） -------
    const val EXPECTED_K_SESSION_HEX = "21f2a1a3890968e1da28553de67b49cab6ab2ffb7f3ecbaec72d284852b9af47"
    const val EXPECTED_PROOF_HEX = "353d6fdd6e73a63620389b806711e6c2c726407045d4c7ddae29264a9e8bab10"
    const val EXPECTED_PSK_BASE64URL = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8"
    const val EXPECTED_IV_HEX = "000000000000000100000000"
    const val EXPECTED_CIPHERTEXT_HEX = "678def935e80e0eeb8ca5a9ee9bbef97f6"
    const val EXPECTED_TAG_HEX = "a2c4302b392deeb76b81f496665496c6"
    const val EXPECTED_SEALED_HEX = "000000000000000100000000" +
        "678def935e80e0eeb8ca5a9ee9bbef97f6" + "a2c4302b392deeb76b81f496665496c6"
    const val EXPECTED_SEALED_LEN = 45

    // ---- 用例输入（§8.1–8.3） ---------------------------------------------
    const val PSK_HEX = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"
    const val NONCE_HEX = "202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f"
    const val HOST_ID = "3f2a1c9e-4b6d-4f7a-9c11-6c5d3e8f2b01"
    const val PEER_ID = "8a7b6c5d-4e3f-4a2b-9c8d-7e6f5a4b3c2d"
    const val PLAINTEXT = "{\"type\":\"unlock\"}"

    data class Result(
        val ok: Boolean,
        val lines: List<String>,
        val failures: List<String>,
        val json: String,
        val kSessionHex: String,
        val proofHex: String,
        val sealedHex: String,
    )

    fun run(): Result {
        val lines = ArrayList<String>()
        val failures = ArrayList<String>()

        fun check(name: String, condition: Boolean, detail: String = "") {
            lines.add("[${if (condition) "PASS" else "FAIL"}] $name${if (detail.isEmpty()) "" else "  $detail"}")
            if (!condition) failures.add("$name${if (detail.isEmpty()) "" else " ($detail)"}")
        }

        val psk = TcProtocol.unhex(PSK_HEX)
        val nonce = TcProtocol.unhex(NONCE_HEX)

        lines.add("== TC-tools Unlock 协议回环自测（Android 端，协议 §8） ==")
        lines.add("PSK   = ${TcProtocol.hex(psk)}")
        lines.add("NONCE = ${TcProtocol.hex(nonce)}")
        lines.add("HOST_ID = $HOST_ID")
        lines.add("PEER_ID = $PEER_ID")

        // 1) K_session
        val kSession = TcProtocol.deriveSessionKey(psk, nonce)
        val kSessionHex = TcProtocol.hex(kSession)
        lines.add("K_session = $kSessionHex")
        check("K_session == 权威值", kSessionHex == EXPECTED_K_SESSION_HEX, "期望 $EXPECTED_K_SESSION_HEX")

        // 2) PROOF
        val proof = TcProtocol.computeProof(psk, nonce, HOST_ID, PEER_ID)
        val proofHex = TcProtocol.hex(proof)
        lines.add("PROOF     = $proofHex")
        check("PROOF == 权威值", proofHex == EXPECTED_PROOF_HEX, "期望 $EXPECTED_PROOF_HEX")
        check("PROOF 长度 = 32 字节（PROOF 帧即裸 32 字节）", proof.size == TcProtocol.PROOF_LEN)

        // 3) base64url（无填充）
        val b64 = TcProtocol.base64UrlEncode(psk)
        lines.add("base64url(PSK) = $b64")
        check("base64url(PSK) == 权威值", b64 == EXPECTED_PSK_BASE64URL)
        check("base64url 不含 '=' 填充", !b64.contains('='))
        check("base64url 往返一致", TcProtocol.base64UrlDecode(b64).contentEquals(psk))

        // 4) SEAL 线格式：IV = counter(8B BE) || 4B 0x00
        val iv = TcProtocol.ivFor(1)
        val ivHex = TcProtocol.hex(iv)
        lines.add("IV(counter=1) = $ivHex")
        check("IV = 8 字节大端 counter + 4 字节 0x00", ivHex == EXPECTED_IV_HEX)

        val sealed = TcProtocol.sealJson(kSession, 1, PLAINTEXT)
        val sealedHex = TcProtocol.hex(sealed)
        lines.add("SEALED(K_session,1,\"$PLAINTEXT\") = $sealedHex")
        check("SEALED 整帧 == 权威值", sealedHex == EXPECTED_SEALED_HEX)
        check(
            "SEALED 长度 = 12 + len(pt) + 16 = $EXPECTED_SEALED_LEN",
            sealed.size == EXPECTED_SEALED_LEN && sealed.size == TcProtocol.sealedFrameLength(PLAINTEXT.toByteArray(Charsets.UTF_8).size)
        )
        val ctHex = TcProtocol.hex(sealed.copyOfRange(12, sealed.size - 16))
        val tagHex = TcProtocol.hex(sealed.copyOfRange(sealed.size - 16, sealed.size))
        lines.add("ciphertext = $ctHex")
        lines.add("tag        = $tagHex")
        check("ciphertext == 权威值", ctHex == EXPECTED_CIPHERTEXT_HEX)
        check("GCM tag == 权威值（密文||tag 顺序）", tagHex == EXPECTED_TAG_HEX)

        // 5) OPEN 往返 + counter 参与 IV
        val opened = TcProtocol.openWithEmbeddedIv(kSession, sealed)
        check("OPEN(SEAL(...)) 往返明文一致", opened != null && String(opened, Charsets.UTF_8) == PLAINTEXT)

        val wrongCounter = TcProtocol.open(kSession, 2, sealed.copyOfRange(12, sealed.size))
        check("counter 不匹配时 OPEN 失败（IV 参与认证）", wrongCounter == null)

        // 5b) 主体（ct||tag，不含 IV）最小长度 17：1 字节明文也必须能解开
        val tiny = TcProtocol.seal(kSession, 3, byteArrayOf(0x41))
        val tinyBody = tiny.copyOfRange(12, tiny.size)
        check("ct||tag 主体长度 = 1+16 = 17", tinyBody.size == 17)
        val tinyOpened = TcProtocol.open(kSession, 3, tinyBody)
        check(
            "短明文(1 字节)的 ct||tag 主体可解（MIN_BODY_LEN=17，修正 verify 发现的潜在缺陷）",
            tinyOpened != null && tinyOpened.contentEquals(byteArrayOf(0x41))
        )

        // 5c) IV 填充必须为 0x00（与 Windows 端 HasValidIvPadding 对齐）
        val badPadding = sealed.copyOf()
        badPadding[9] = 1
        check("IV 填充非 0x00 被判非法", !TcProtocol.hasValidIvPadding(badPadding))
        check("正常帧 IV 填充校验通过", TcProtocol.hasValidIvPadding(sealed))
        val padAsm = SealedFrameAssembler(kSession)
        check(
            "IV 填充非 0 的帧被组装器拒绝（bad-frame）",
            padAsm.feed(badPadding).any { it is SealedFrameAssembler.Outcome.Rejected }
        )

        val tampered = sealed.copyOf()
        tampered[tampered.size - 1] = (tampered[tampered.size - 1].toInt() xor 0x01).toByte()
        check("密文被篡改时 OPEN 失败（GCM tag 校验）", TcProtocol.openWithEmbeddedIv(kSession, tampered) == null)

        // 6) 分片重组：45 字节帧按 20 字节 MTU 载荷切片
        val assembler = SealedFrameAssembler(kSession)
        val chunks = sealed.toList().chunked(20).map { it.toByteArray() }
        val outcomes = ArrayList<SealedFrameAssembler.Outcome>()
        for (c in chunks) outcomes.addAll(assembler.feed(c))
        val msg = outcomes.filterIsInstance<SealedFrameAssembler.Outcome.Message>().firstOrNull()
        check(
            "分片重组：45B 帧切 ${chunks.size} 片后完整解出（counter=1）",
            msg != null && msg.counter == 1L && msg.json == PLAINTEXT
        )

        // 7) 带 2 字节大端长度前缀的分片格式
        val prefixed = SealedFrameAssembler(kSession)
        val header = byteArrayOf(((sealed.size shr 8) and 0xFF).toByte(), (sealed.size and 0xFF).toByte())
        val p1 = prefixed.feed(header + sealed.copyOfRange(0, 10))
        val p2 = prefixed.feed(sealed.copyOfRange(10, sealed.size))
        val pMsg = (p1 + p2).filterIsInstance<SealedFrameAssembler.Outcome.Message>().firstOrNull()
        check("长度前缀分片格式可解出", pMsg != null && pMsg.json == PLAINTEXT)

        // 8) 重放防护：同一帧第二次必须被拒
        val replay = SealedFrameAssembler(kSession)
        replay.feed(sealed)
        val second = replay.feed(sealed)
        check(
            "重放：counter 未严格递增时被拒",
            second.any { it is SealedFrameAssembler.Outcome.Rejected }
        )

        // 9) 单调递增：counter=2 的帧在 counter=1 之后应被接受
        val seq = SealedFrameAssembler(kSession)
        seq.feed(sealed)
        val m2 = seq.feed(TcProtocol.sealJson(kSession, 2, TcProtocol.pingJson()))
        check(
            "counter 严格递增的消息被接受",
            m2.any { it is SealedFrameAssembler.Outcome.Message && it.counter == 2L }
        )

        // 10) 配对载荷（§4.1）往返
        val payloadText = MiniJson.obj(
            "v" to 1, "p" to "tcunlock", "id" to HOST_ID, "name" to "DESKTOP-ABC测试机", "psk" to b64
        )
        val parsed = Provisioning.parse(payloadText)
        check(
            "配对二维码文本可解析且往返一致",
            parsed is ProvisioningResult.Ok &&
                (parsed.payload.psk.contentEquals(psk)) &&
                parsed.payload.hostId == HOST_ID &&
                parsed.payload.hostName == "DESKTOP-ABC测试机" &&
                parsed.payload.toPayloadText() == payloadText
        )
        check(
            "非法 pairing 文本被拒（PSK 长度错）",
            Provisioning.parse("{\"v\":1,\"p\":\"tcunlock\",\"id\":\"$HOST_ID\",\"name\":\"X\",\"psk\":\"AAAA\"}") is ProvisioningResult.Err
        )
        check(
            "非法 pairing 文本被拒（产品标识错）",
            Provisioning.parse("{\"v\":1,\"p\":\"other\",\"id\":\"$HOST_ID\",\"name\":\"X\",\"psk\":\"$b64\"}") is ProvisioningResult.Err
        )

        val ok = failures.isEmpty()
        lines.add("== 结果：${if (ok) "全部通过" else "失败 ${failures.size} 项"} ==")
        failures.forEach { lines.add("   ✗ $it") }

        val json = buildString {
            append("{\n")
            append("  \"impl\": \"android-kotlin\",\n")
            append("  \"protocol_version\": ${TcProtocol.PROTOCOL_VERSION},\n")
            append("  \"psk_hex\": \"${TcProtocol.hex(psk)}\",\n")
            append("  \"nonce_hex\": \"${TcProtocol.hex(nonce)}\",\n")
            append("  \"host_id\": \"$HOST_ID\",\n")
            append("  \"peer_id\": \"$PEER_ID\",\n")
            append("  \"k_session_hex\": \"$kSessionHex\",\n")
            append("  \"proof_hex\": \"$proofHex\",\n")
            append("  \"psk_base64url\": \"$b64\",\n")
            append("  \"plaintext_hex\": \"${TcProtocol.hex(PLAINTEXT.toByteArray(Charsets.UTF_8))}\",\n")
            append("  \"iv_hex\": \"$ivHex\",\n")
            append("  \"ciphertext_hex\": \"$ctHex\",\n")
            append("  \"tag_hex\": \"$tagHex\",\n")
            append("  \"ct_tag_hex\": \"${TcProtocol.hex(sealed.copyOfRange(12, sealed.size))}\",\n")
            append("  \"seal_hex\": \"$sealedHex\",\n")
            append("  \"sealed_frame_len\": ${sealed.size},\n")
            append("  \"fragment_chunks\": ${chunks.size},\n")
            append("  \"all_checks_passed\": $ok,\n")
            append("  \"failures\": [${failures.joinToString(",") { MiniJson.quote(it) }}]\n")
            append("}\n")
        }

        return Result(ok = ok, lines = lines, failures = failures, json = json, kSessionHex = kSessionHex, proofHex = proofHex, sealedHex = sealedHex)
    }
}
