package com.tctools.unlock.protocol

/** 第 4.1 节二维码载荷解析结果。 */
data class ProvisioningPayload(
    val version: Int,
    val product: String,
    val hostId: String,
    val hostName: String,
    val psk: ByteArray,
) {
    fun pskBase64Url(): String = TcProtocol.base64UrlEncode(psk)

    /** 反向序列化（用于"显示/复制这段文本"通道，第 4.1 节要求两端都支持）。 */
    fun toPayloadText(): String = MiniJson.obj(
        "v" to version,
        "p" to product,
        "id" to hostId,
        "name" to hostName,
        "psk" to pskBase64Url(),
    )

    override fun equals(other: Any?): Boolean {
        if (other !is ProvisioningPayload) return false
        return version == other.version && product == other.product && hostId == other.hostId &&
            hostName == other.hostName && psk.contentEquals(other.psk)
    }

    override fun hashCode(): Int {
        var r = version
        r = 31 * r + product.hashCode()
        r = 31 * r + hostId.hashCode()
        r = 31 * r + hostName.hashCode()
        r = 31 * r + psk.contentHashCode()
        return r
    }

    override fun toString(): String = "ProvisioningPayload(v=$version, hostId=$hostId, hostName=$hostName, psk=${psk.size}B)"
}

sealed class ProvisioningResult {
    data class Ok(val payload: ProvisioningPayload) : ProvisioningResult()
    data class Err(val message: String) : ProvisioningResult()
}

/**
 * 一次性配对二维码载荷（协议第 4.1 节）：
 * `{"v":1,"p":"tcunlock","id":"<HOST_ID>","name":"<电脑名>","psk":"<base64url(PSK),无填充>"}`
 */
object Provisioning {

    private val UUID_RE = Regex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOption.IGNORE_CASE)

    fun parse(text: String): ProvisioningResult {
        val raw = text.trim().removePrefix("\uFEFF").trim()
        if (raw.isEmpty()) return ProvisioningResult.Err("内容为空，请粘贴二维码中的 JSON 文本")

        val obj = try {
            MiniJson.parseObject(raw)
        } catch (t: Throwable) {
            return ProvisioningResult.Err("不是合法的 JSON：${t.message ?: t.javaClass.simpleName}")
        } ?: return ProvisioningResult.Err("顶层必须是 JSON 对象，例如 {\"v\":1,\"p\":\"tcunlock\",...}")

        val v = (obj["v"] as? Number)?.toInt() ?: return ProvisioningResult.Err("缺少字段 v（协议版本）")
        if (v != TcProtocol.PROTOCOL_VERSION) return ProvisioningResult.Err("协议版本不支持：v=$v，本应用只支持 v=${TcProtocol.PROTOCOL_VERSION}")

        val p = obj["p"] as? String ?: return ProvisioningResult.Err("缺少字段 p（产品标识）")
        if (p != TcProtocol.PRODUCT_TAG) return ProvisioningResult.Err("不是 TC-Tools 配对二维码：p=\"$p\"")

        val id = (obj["id"] as? String)?.trim()?.lowercase()
            ?: return ProvisioningResult.Err("缺少字段 id（HOST_ID）")
        if (!UUID_RE.matches(id)) return ProvisioningResult.Err("HOST_ID 不是合法 UUID：\"$id\"")

        val name = (obj["name"] as? String)?.takeIf { it.isNotBlank() } ?: "未命名电脑"

        val pskText = (obj["psk"] as? String)?.trim()
            ?: return ProvisioningResult.Err("缺少字段 psk")
        val psk = try {
            TcProtocol.base64UrlDecode(pskText)
        } catch (t: Throwable) {
            return ProvisioningResult.Err("psk 不是合法的 base64url 文本")
        }
        if (psk.size != TcProtocol.PSK_LEN) {
            return ProvisioningResult.Err("PSK 长度必须是 ${TcProtocol.PSK_LEN} 字节（当前 ${psk.size} 字节）")
        }

        return ProvisioningResult.Ok(
            ProvisioningPayload(version = v, product = p, hostId = id, hostName = name, psk = psk)
        )
    }
}
