package com.tctools.unlock.tile

import android.content.Context
import com.tctools.unlock.data.AppPrefs

/**
 * 快捷设置磁贴与 App 之间的进程内状态桥（v0.2.0-rc2）。
 *
 * 磁贴（[UnlockTileService]）与 Activity 默认跑在同一进程，因此用单例传递实时状态；
 * 进程冷启动（用户直接下拉控制中心点磁贴）时用 [ensureLoaded] 从普通 SharedPreferences
 * 恢复「是否已配对 / 电脑名」这类**非机密**信息（PSK 永远只经 [com.tctools.unlock.data.PairingStore]）。
 */
object UnlockTileState {

    enum class Phase {
        /** 尚未配对电脑。 */
        UNPAIRED,

        /** 已配对，当前没有连接。 */
        IDLE,

        /** 正在扫描/连接/发现服务。 */
        CONNECTING,

        /** 已连接并读完 challenge，等待指纹。 */
        READY,

        /** 正在验证指纹 / 正在发送解锁指令。 */
        UNLOCKING,

        /** 刚刚解锁成功。 */
        SUCCESS,

        /** 刚刚失败（含权限、蓝牙关闭、PROOF 失败等）。 */
        FAILED,
    }

    data class Snapshot(
        val paired: Boolean,
        val hostName: String?,
        val phase: Phase,
        val detail: String?,
    ) {
        /** 磁贴主状态（stateDescription）。 */
        fun shortStatus(): String = when (phase) {
            Phase.UNPAIRED -> "未配对"
            Phase.IDLE -> "未连接"
            Phase.CONNECTING -> "连接中"
            Phase.READY -> "已就绪"
            Phase.UNLOCKING -> "解锁中"
            Phase.SUCCESS -> "已解锁"
            Phase.FAILED -> "解锁失败"
        }

        /** 磁贴副标题（API 29+ 支持 subtitle）。 */
        fun subtitle(): String = when (phase) {
            Phase.UNPAIRED -> "点按去配对"
            Phase.IDLE -> hostName?.let { "点按解锁 · $it" } ?: "点按解锁"
            Phase.CONNECTING -> hostName?.let { "正在连接 $it" } ?: "正在连接"
            Phase.READY -> "点按指纹解锁"
            Phase.UNLOCKING -> "请验证指纹"
            Phase.SUCCESS -> "解锁成功"
            Phase.FAILED -> detail ?: "点按重试"
        }

        /** 无障碍朗读用的完整描述。 */
        fun fullStatus(): String = buildString {
            append("TC-Tools 解锁电脑，")
            append(shortStatus())
            hostName?.let { append("，目标电脑 $it") }
            detail?.takeIf { phase == Phase.FAILED }?.let { append("，$it") }
        }
    }

    /** 成功/失败状态在磁贴上的保留时长，超时后回落为「已就绪 / 未连接」。 */
    private const val TRANSIENT_MS = 6_000L

    @Volatile
    private var loaded = false

    @Volatile
    private var paired = false

    @Volatile
    private var hostName: String? = null

    @Volatile
    private var phase = Phase.UNPAIRED

    @Volatile
    private var detail: String? = null

    @Volatile
    private var updatedAt = 0L

    @Volatile
    private var lastPublished: String? = null

    /** 从普通 SharedPreferences 恢复非机密状态（进程冷启动时调用一次）。 */
    fun ensureLoaded(context: Context): Snapshot {
        if (!loaded) {
            synchronized(this) {
                if (!loaded) {
                    val prefs = AppPrefs(context.applicationContext)
                    paired = prefs.tilePaired
                    hostName = prefs.tileHostName
                    phase = if (paired) Phase.IDLE else Phase.UNPAIRED
                    loaded = true
                }
            }
        }
        return snapshot()
    }

    fun snapshot(): Snapshot {
        val effective = resolveStale()
        return Snapshot(paired = paired, hostName = hostName, phase = effective, detail = detail)
    }

    /** 由 ViewModel 在 UI 状态变化时调用；文案无变化时不打扰系统。 */
    fun publish(
        context: Context,
        paired: Boolean,
        hostName: String?,
        phase: Phase,
        detail: String? = null,
    ) {
        this.loaded = true
        this.paired = paired
        this.hostName = hostName
        this.phase = phase
        this.detail = detail
        this.updatedAt = System.currentTimeMillis()

        val fingerprint = "${paired}|${hostName ?: ""}|$phase|${detail ?: ""}"
        if (fingerprint != lastPublished) {
            lastPublished = fingerprint
            UnlockTileService.refresh(context)
        }
    }

    private fun resolveStale(): Phase {
        val transient = phase == Phase.SUCCESS || phase == Phase.FAILED
        if (!transient) return phase
        if (System.currentTimeMillis() - updatedAt <= TRANSIENT_MS) return phase
        // 成功/失败只闪现一小会儿，之后回落到稳定状态
        return when {
            !paired -> Phase.UNPAIRED
            phase == Phase.SUCCESS -> Phase.IDLE
            else -> Phase.READY
        }
    }
}
