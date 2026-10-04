package com.tctools.unlock.protocol

/**
 * 极简 JSON 解析/构造器（纯 JVM，不依赖 `org.json`）。
 *
 * 之所以自己实现：单元测试运行在桌面 JVM 上，`org.json` 是 android.jar 的桩实现会抛
 * `RuntimeException("Stub!")`；协议里的 JSON 结构又非常小（单层对象 + 字符串/布尔/数字）。
 *
 * 支持：对象、数组、字符串（含 `\" \\ \/ \b \f \n \r \t \uXXXX`）、数字、true/false/null。
 */
object MiniJson {

    fun parse(text: String): Any? {
        val p = Parser(text)
        p.skipWs()
        val v = p.parseValue()
        p.skipWs()
        if (!p.eof()) throw IllegalArgumentException("JSON 末尾存在多余字符（位置 ${p.pos}）")
        return v
    }

    @Suppress("UNCHECKED_CAST")
    fun parseObject(text: String): Map<String, Any?>? = parse(text) as? Map<String, Any?>

    /** 按传入顺序序列化（保证字节稳定，便于测试比对）。 */
    fun obj(vararg pairs: Pair<String, Any?>): String {
        val sb = StringBuilder("{")
        pairs.forEachIndexed { i, (k, v) ->
            if (i > 0) sb.append(',')
            sb.append(quote(k)).append(':').append(value(v))
        }
        return sb.append('}').toString()
    }

    fun value(v: Any?): String = when (v) {
        null -> "null"
        is String -> quote(v)
        is Boolean -> if (v) "true" else "false"
        is Int, is Long, is Short, is Byte -> v.toString()
        is Double -> if (v == v.toLong().toDouble()) v.toLong().toString() else v.toString()
        is Float -> value(v.toDouble())
        is Map<*, *> -> {
            val sb = StringBuilder("{")
            v.entries.forEachIndexed { i, e ->
                if (i > 0) sb.append(',')
                sb.append(quote(e.key.toString())).append(':').append(value(e.value))
            }
            sb.append('}').toString()
        }
        is List<*> -> v.joinToString(prefix = "[", postfix = "]") { value(it) }
        else -> quote(v.toString())
    }

    fun quote(s: String): String {
        val sb = StringBuilder(s.length + 2).append('"')
        for (ch in s) {
            when (ch) {
                '"' -> sb.append("\\\"")
                '\\' -> sb.append("\\\\")
                '\n' -> sb.append("\\n")
                '\r' -> sb.append("\\r")
                '\t' -> sb.append("\\t")
                '\b' -> sb.append("\\b")
                '\u000C' -> sb.append("\\f")
                else -> if (ch < ' ') sb.append("\\u%04x".format(ch.code)) else sb.append(ch)
            }
        }
        return sb.append('"').toString()
    }

    private class Parser(private val s: String) {
        var pos = 0
            private set

        fun eof() = pos >= s.length

        fun skipWs() {
            while (pos < s.length && s[pos].let { it == ' ' || it == '\t' || it == '\n' || it == '\r' }) pos++
        }

        fun parseValue(): Any? {
            if (eof()) throw IllegalArgumentException("JSON 提前结束")
            return when (s[pos]) {
                '{' -> parseObjectBody()
                '[' -> parseArrayBody()
                '"' -> parseString()
                't' -> { expect("true"); true }
                'f' -> { expect("false"); false }
                'n' -> { expect("null"); null }
                else -> parseNumber()
            }
        }

        private fun parseObjectBody(): Map<String, Any?> {
            expect("{")
            val map = LinkedHashMap<String, Any?>()
            skipWs()
            if (peek() == '}') { pos++; return map }
            while (true) {
                skipWs()
                val key = parseString()
                skipWs()
                expect(":")
                skipWs()
                map[key] = parseValue()
                skipWs()
                when (val c = peek()) {
                    ',' -> pos++
                    '}' -> { pos++; return map }
                    else -> throw IllegalArgumentException("对象中出现非法字符 '$c'（位置 $pos）")
                }
            }
        }

        private fun parseArrayBody(): List<Any?> {
            expect("[")
            val list = ArrayList<Any?>()
            skipWs()
            if (peek() == ']') { pos++; return list }
            while (true) {
                skipWs()
                list.add(parseValue())
                skipWs()
                when (val c = peek()) {
                    ',' -> pos++
                    ']' -> { pos++; return list }
                    else -> throw IllegalArgumentException("数组中出现非法字符 '$c'（位置 $pos）")
                }
            }
        }

        private fun parseString(): String {
            expect("\"")
            val sb = StringBuilder()
            while (true) {
                if (eof()) throw IllegalArgumentException("字符串未闭合")
                val c = s[pos++]
                when {
                    c == '"' -> return sb.toString()
                    c == '\\' -> {
                        if (eof()) throw IllegalArgumentException("转义未结束")
                        when (val e = s[pos++]) {
                            '"' -> sb.append('"')
                            '\\' -> sb.append('\\')
                            '/' -> sb.append('/')
                            'b' -> sb.append('\b')
                            'f' -> sb.append('\u000C')
                            'n' -> sb.append('\n')
                            'r' -> sb.append('\r')
                            't' -> sb.append('\t')
                            'u' -> {
                                if (pos + 4 > s.length) throw IllegalArgumentException("\\u 转义不完整")
                                val hex = s.substring(pos, pos + 4)
                                pos += 4
                                sb.append(hex.toInt(16).toChar())
                            }
                            else -> throw IllegalArgumentException("非法转义 \\$e")
                        }
                    }
                    else -> sb.append(c)
                }
            }
        }

        private fun parseNumber(): Any {
            val start = pos
            if (peek() == '-') pos++
            while (!eof() && (s[pos].isDigit() || s[pos] == '.' || s[pos] == 'e' || s[pos] == 'E' || s[pos] == '+' || s[pos] == '-')) pos++
            val raw = s.substring(start, pos)
            if (raw.isEmpty()) throw IllegalArgumentException("非法 JSON 值（位置 $start）")
            return if (raw.contains('.') || raw.contains('e') || raw.contains('E')) raw.toDouble() else raw.toLong()
        }

        private fun peek(): Char {
            if (eof()) throw IllegalArgumentException("JSON 提前结束")
            return s[pos]
        }

        private fun expect(literal: String) {
            if (!s.startsWith(literal, pos)) throw IllegalArgumentException("期望 '$literal'（位置 $pos）")
            pos += literal.length
        }
    }
}
