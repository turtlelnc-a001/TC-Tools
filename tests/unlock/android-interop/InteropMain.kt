package com.tctools.unlock.protocol

import java.io.File
import kotlin.system.exitProcess

/**
 * verify/task-4 — Android 端产品源码的**双向互通**运行宿主（本文件属验证工具，不是产品代码）。
 *
 * 与 winproto-harness 配对：
 *   --emit <counter> <kind> <out>  用产品代码 TcProtocol.seal 生成 SEAL 帧
 *                                  kind ∈ {unlock, ping, bye}，避免 shell 吞掉 JSON 里的双引号
 *   --open <in> <expectedCounter>  读取对端产出的 JSON，用**本端自行派生**的 K_session 解密
 *                                  （刻意忽略输入文件里的任何密钥字段，避免"同一把错钥匙自证"）
 *
 * JSON 解析/构造走产品代码 MiniJson，顺带验证它对线上 JSON 的处理。
 */

private const val PSK_HEX = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"
private const val NONCE_HEX = "202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f"

fun main(args: Array<String>) {
    val psk = TcProtocol.unhex(PSK_HEX)
    val nonce = TcProtocol.unhex(NONCE_HEX)
    // K_session 一律由本端从 §8 的 PSK/NONCE 重新派生
    val key = TcProtocol.deriveSessionKey(psk, nonce)

    when (args.firstOrNull()) {
        "--emit" -> {
            if (args.size < 4) { println("用法: --emit <counter> <kind> <outPath>"); exitProcess(2) }
            val counter = args[1].toLong()
            val kind = args[2]
            val json = when (kind) {
                "unlock" -> TcProtocol.unlockJson()
                "ping" -> TcProtocol.pingJson()
                "bye" -> TcProtocol.byeJson()
                else -> { println("未知载荷种类: $kind（可用 unlock|ping|bye）"); exitProcess(2) }
            }
            val outPath = args[3]
            val frame = TcProtocol.sealJson(key, counter, json)
            val text = MiniJson.obj(
                "emitter" to "android",
                "counter" to counter,
                "plaintext" to json,
                "seal_hex" to TcProtocol.hex(frame),
                "iv_hex" to TcProtocol.hex(frame.copyOfRange(0, TcProtocol.IV_LEN)),
                "tag_hex" to TcProtocol.hex(frame.copyOfRange(frame.size - TcProtocol.TAG_LEN, frame.size)),
            )
            File(outPath).writeText(text, Charsets.UTF_8)
            println("EMIT android counter=$counter len=${frame.size} -> $outPath")
            println("     seal_hex = ${TcProtocol.hex(frame)}")
            exitProcess(0)
        }

        "--open" -> {
            if (args.size < 3) { println("用法: --open <inPath> <expectedCounter>"); exitProcess(2) }
            val inPath = args[1]
            val expected = args[2].toLong()
            val obj = MiniJson.parseObject(File(inPath).readText(Charsets.UTF_8))
            if (obj == null) { println("输入不是 JSON 对象: $inPath"); exitProcess(2) }
            val emitter = obj["emitter"] as? String ?: "?"
            val sealHex = obj["seal_hex"] as? String ?: run {
                println("输入缺少 seal_hex: $inPath"); exitProcess(2)
            }
            val frame = TcProtocol.unhex(sealHex)

            val counter = TcProtocol.counterOfSealedFrame(frame)
            val plain = TcProtocol.openWithEmbeddedIv(key, frame)
            val ok = plain != null
            val text = plain?.toString(Charsets.UTF_8) ?: ""
            val counterOk = ok && counter == expected

            println(
                MiniJson.obj(
                    "opener" to "android",
                    "input" to File(inPath).name,
                    "emitter" to emitter,
                    "opened" to ok,
                    "counter" to counter,
                    "expected_counter" to expected,
                    "counter_ok" to counterOk,
                    "plaintext" to text,
                    "error" to if (ok) "" else "AEAD 认证失败或帧结构非法",
                    "k_session_self_derived" to TcProtocol.hex(key),
                )
            )
            exitProcess(if (counterOk) 0 else 1)
        }

        else -> {
            println("用法: InteropMainKt --emit <counter> <kind> <outPath> | --open <inPath> <expectedCounter>")
            exitProcess(2)
        }
    }
}
