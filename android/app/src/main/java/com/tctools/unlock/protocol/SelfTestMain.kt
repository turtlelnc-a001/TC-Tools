package com.tctools.unlock.protocol

import java.io.File
import kotlin.system.exitProcess

/**
 * 可执行的 JVM 入口：`gradle :app:selfTest`（或直接 java -cp 运行）。
 *
 * 用法：
 *   java -cp <classes>:<deps> com.tctools.unlock.protocol.SelfTestMainKt [输出JSON路径]
 * 退出码 0 = 全部通过，1 = 有断言失败。
 */
fun main(args: Array<String>) {
    val result = LoopbackSelfTest.run()
    result.lines.forEach { println(it) }

    val outPath = args.firstOrNull { !it.startsWith("-") }
    if (outPath != null) {
        val f = File(outPath)
        f.absoluteFile.parentFile?.mkdirs()
        f.writeText(result.json, Charsets.UTF_8)
        println("自测 JSON 已写入：${f.absolutePath}")
    }
    println("SELFTEST_RESULT=${if (result.ok) "PASS" else "FAIL"}")
    if (!result.ok) exitProcess(1)
}
