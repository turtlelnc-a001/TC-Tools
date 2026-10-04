package com.tctools.unlock.protocol

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 协议 §8 回环自测的 JUnit 版本：`gradle :app:testDebugUnitTest`
 *
 * 期望值来自验证负责人（verify）用 Node.js 第三种独立实现算出的权威值，
 * 与 docs/UNLOCK-PROTOCOL.md §8 一致。**不得**按本端输出反推期望值。
 */
class LoopbackSelfTestTest {

    @Test
    fun `section 8 - K_session 与 PROOF 与权威向量逐字节一致`() {
        val r = LoopbackSelfTest.run()
        r.lines.forEach { println(it) }
        println("K_session = ${r.kSessionHex}")
        println("PROOF     = ${r.proofHex}")
        assertTrue("回环自测失败项: ${r.failures}", r.ok)
        assertEquals(LoopbackSelfTest.EXPECTED_K_SESSION_HEX, r.kSessionHex)
        assertEquals(LoopbackSelfTest.EXPECTED_PROOF_HEX, r.proofHex)
    }

    @Test
    fun `SEAL 帧长度与线格式 = 12 + len(pt) + 16`() {
        val psk = TcProtocol.unhex(LoopbackSelfTest.PSK_HEX)
        val nonce = TcProtocol.unhex(LoopbackSelfTest.NONCE_HEX)
        val key = TcProtocol.deriveSessionKey(psk, nonce)
        val frame = TcProtocol.sealJson(key, 1, LoopbackSelfTest.PLAINTEXT)
        assertEquals(45, frame.size)
        assertEquals(TcProtocol.sealedFrameLength(17), frame.size)
        assertEquals(LoopbackSelfTest.EXPECTED_SEALED_HEX, TcProtocol.hex(frame))
        assertEquals("000000000000000100000000", TcProtocol.hex(frame.copyOfRange(0, 12)))
        assertEquals(1L, TcProtocol.counterOfSealedFrame(frame))
    }

    @Test
    fun `IV 为 counter 8 字节大端 + 4 字节 0x00`() {
        assertEquals("000000000000000100000000", TcProtocol.hex(TcProtocol.ivFor(1)))
        assertEquals("000000000000010000000000", TcProtocol.hex(TcProtocol.ivFor(0x0100)))
        assertEquals("00000000000000ff00000000", TcProtocol.hex(TcProtocol.ivFor(255)))
        assertEquals(12, TcProtocol.ivFor(1).size)
    }

    @Test
    fun `base64url 无填充且往返一致`() {
        val psk = TcProtocol.unhex(LoopbackSelfTest.PSK_HEX)
        val s = TcProtocol.base64UrlEncode(psk)
        assertEquals(LoopbackSelfTest.EXPECTED_PSK_BASE64URL, s)
        assertEquals(43, s.length)
        assertTrue(!s.contains('=') && !s.contains('+') && !s.contains('/'))
        assertTrue(TcProtocol.base64UrlDecode(s).contentEquals(psk))
    }

    @Test
    fun `GCM 认证失败与重放均被拒绝`() {
        val psk = TcProtocol.unhex(LoopbackSelfTest.PSK_HEX)
        val nonce = TcProtocol.unhex(LoopbackSelfTest.NONCE_HEX)
        val key = TcProtocol.deriveSessionKey(psk, nonce)
        val frame = TcProtocol.sealJson(key, 1, LoopbackSelfTest.PLAINTEXT)

        // 篡改 tag
        val bad = frame.copyOf()
        bad[bad.size - 1] = (bad[bad.size - 1].toInt() xor 0x80).toByte()
        assertNull(TcProtocol.openWithEmbeddedIv(key, bad))

        // 用错误的 key 解
        assertNull(TcProtocol.openWithEmbeddedIv(TcProtocol.hmacSha256(key, byteArrayOf(1)), frame))

        // 重放：同一帧第二次必须被拒
        val asm = SealedFrameAssembler(key)
        assertTrue(asm.feed(frame).any { it is SealedFrameAssembler.Outcome.Message })
        assertTrue(asm.feed(frame).any { it is SealedFrameAssembler.Outcome.Rejected })
    }

    @Test
    fun `分片重组 - 20 字节 MTU 切片与长度前缀两种格式`() {
        val psk = TcProtocol.unhex(LoopbackSelfTest.PSK_HEX)
        val nonce = TcProtocol.unhex(LoopbackSelfTest.NONCE_HEX)
        val key = TcProtocol.deriveSessionKey(psk, nonce)
        val frame = TcProtocol.sealJson(key, 1, LoopbackSelfTest.PLAINTEXT)

        val a = SealedFrameAssembler(key)
        val outA = frame.toList().chunked(20).flatMap { a.feed(it.toByteArray()) }
        val msgA = outA.filterIsInstance<SealedFrameAssembler.Outcome.Message>().firstOrNull()
        assertNotNull(msgA)
        assertEquals(LoopbackSelfTest.PLAINTEXT, msgA!!.json)

        val b = SealedFrameAssembler(key)
        val header = byteArrayOf(((frame.size shr 8) and 0xFF).toByte(), (frame.size and 0xFF).toByte())
        val outB = listOf(header + frame.copyOfRange(0, 8), frame.copyOfRange(8, 30), frame.copyOfRange(30, frame.size))
            .flatMap { b.feed(it) }
        val msgB = outB.filterIsInstance<SealedFrameAssembler.Outcome.Message>().firstOrNull()
        assertNotNull(msgB)
        assertEquals(LoopbackSelfTest.PLAINTEXT, msgB!!.json)
    }

    @Test
    fun `配对载荷解析 - 合法通过 非法拒绝`() {
        val psk = TcProtocol.unhex(LoopbackSelfTest.PSK_HEX)
        val b64 = TcProtocol.base64UrlEncode(psk)
        val text = "{\"v\":1,\"p\":\"tcunlock\",\"id\":\"${LoopbackSelfTest.HOST_ID}\",\"name\":\"我的电脑\",\"psk\":\"$b64\"}"
        val ok = Provisioning.parse(text)
        assertTrue(ok is ProvisioningResult.Ok)
        assertEquals(LoopbackSelfTest.HOST_ID, (ok as ProvisioningResult.Ok).payload.hostId)
        assertEquals("我的电脑", ok.payload.hostName)
        assertTrue(ok.payload.psk.contentEquals(psk))
        assertEquals(text, ok.payload.toPayloadText())

        assertTrue(Provisioning.parse("") is ProvisioningResult.Err)
        assertTrue(Provisioning.parse("{}") is ProvisioningResult.Err)
        assertTrue(Provisioning.parse("{\"v\":2,\"p\":\"tcunlock\",\"id\":\"${LoopbackSelfTest.HOST_ID}\",\"name\":\"x\",\"psk\":\"$b64\"}") is ProvisioningResult.Err)
        assertTrue(Provisioning.parse("{\"v\":1,\"p\":\"tcunlock\",\"id\":\"not-a-uuid\",\"name\":\"x\",\"psk\":\"$b64\"}") is ProvisioningResult.Err)
        assertTrue(Provisioning.parse("{\"v\":1,\"p\":\"tcunlock\",\"id\":\"${LoopbackSelfTest.HOST_ID}\",\"name\":\"x\",\"psk\":\"AAAA\"}") is ProvisioningResult.Err)
    }

    @Test
    fun `MiniJson 解析 - 字符串转义 数字 布尔 null 数组`() {
        val obj = MiniJson.parseObject("{\"type\":\"unlock_result\",\"ok\":true,\"locked\":false,\"n\":3,\"r\":\"a\\\"b\\n中文\",\"list\":[1,2]}")!!
        assertEquals("unlock_result", obj["type"])
        assertEquals(true, obj["ok"])
        assertEquals(false, obj["locked"])
        assertEquals(3L, obj["n"])
        assertEquals("a\"b\n中文", obj["r"])
        assertEquals(listOf(1L, 2L), obj["list"])
        assertEquals("{\"type\":\"unlock\"}", MiniJson.obj("type" to "unlock"))
    }
}
