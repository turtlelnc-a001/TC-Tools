package com.tctools.unlock

import android.app.Application
import android.bluetooth.BluetoothDevice
import android.os.Build
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.tctools.unlock.ble.BleCentral
import com.tctools.unlock.ble.BleEvent
import com.tctools.unlock.ble.BlePhase
import com.tctools.unlock.data.AppPrefs
import com.tctools.unlock.data.PairingStore
import com.tctools.unlock.protocol.LoopbackSelfTest
import com.tctools.unlock.protocol.MiniJson
import com.tctools.unlock.protocol.Provisioning
import com.tctools.unlock.protocol.ProvisioningResult
import com.tctools.unlock.protocol.TcProtocol
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

data class UiState(
    val hostName: String? = null,
    val hostId: String? = null,
    val peerId: String? = null,
    val pairedAt: Long = 0L,
    val hasPairing: Boolean = false,
    val phase: BlePhase = BlePhase.IDLE,
    val statusMessage: String? = null,
    val rssi: Int? = null,
    val mtu: Int? = null,
    val advertisedName: String? = null,
    val lastUnlockAt: Long = 0L,
    val lastResult: String = "",
    val sessionId: String? = null,
    val banner: String? = null,
    val bannerIsError: Boolean = false,
    val busy: Boolean = false,
    val awaitingBiometric: Boolean = false,
    val storageMode: String = "",
    val selfTestReport: String? = null,
    val selfTestOk: Boolean? = null,
    val autoDisconnect: Boolean = true,
    val selfTestRunning: Boolean = false,
)

/**
 * 解锁流程编排（协议第 1 节）：
 * 连接 → 读 32 字节 challenge → **指纹验证通过后才计算 PROOF 与 K_session** → 写 PROOF
 * → 收 `ready`（counter=1，解出 sid）→ 发 `unlock`（counter=1）→ 收 `unlock_result`（counter=2）。
 */
class UnlockViewModel(app: Application) : AndroidViewModel(app) {

    private val store = PairingStore(app)
    private val prefs = AppPrefs(app)
    private val ble = BleCentral(app)

    private val _ui = MutableStateFlow(UiState())
    val ui: StateFlow<UiState> = _ui.asStateFlow()

    /** 当前连接内的 challenge（NONCE），只在连接内有效。 */
    private var nonce: ByteArray? = null
    private var pendingUnlock = false
    private var kSession: ByteArray? = null
    private var txCounter: Long = 0L

    val bleCentral: BleCentral get() = ble

    init {
        reloadPairing()
        _ui.update {
            it.copy(
                autoDisconnect = prefs.autoDisconnect,
                lastUnlockAt = prefs.lastUnlockAt,
                lastResult = prefs.lastResult,
                storageMode = store.storageMode,
            )
        }
        viewModelScope.launch {
            ble.status.collect { s ->
                _ui.update {
                    it.copy(
                        phase = s.phase,
                        statusMessage = s.message,
                        rssi = s.rssi,
                        mtu = s.mtu,
                        advertisedName = s.deviceName,
                    )
                }
                if (s.phase == BlePhase.CONNECTING || s.phase == BlePhase.DISCONNECTED || s.phase == BlePhase.IDLE) {
                    nonce = null
                    kSession = null
                    ble.setSessionKey(null)
                    txCounter = 0L
                }
                if (s.phase == BlePhase.READY || s.phase == BlePhase.SCANNING || s.phase == BlePhase.DISCONNECTED) {
                    _ui.update { it.copy(busy = false) }
                }
            }
        }
        viewModelScope.launch {
            ble.events.collect { handleEvent(it) }
        }
    }

    // ---- 配对 --------------------------------------------------------------

    fun reloadPairing() {
        val p = store.load()
        _ui.update {
            it.copy(
                hostName = p?.hostName,
                hostId = p?.hostId,
                peerId = p?.peerId,
                pairedAt = p?.pairedAt ?: 0L,
                hasPairing = p != null,
                storageMode = store.storageMode,
            )
        }
    }

    /** 扫码 / 手动粘贴 配对文本（协议第 4.1 节）。返回错误信息，null 表示成功。 */
    fun pairFromText(text: String): String? {
        return when (val r = Provisioning.parse(text)) {
            is ProvisioningResult.Ok -> {
                val saved = store.save(r.payload.hostId, r.payload.hostName, r.payload.psk)
                reloadPairing()
                banner("已配对「${saved.hostName}」，现在可以用指纹解锁了", ok = true)
                null
            }

            is ProvisioningResult.Err -> {
                banner(r.message, ok = false)
                r.message
            }
        }
    }

    fun forgetHost() {
        ble.disconnect()
        store.clear()
        nonce = null
        kSession = null
        ble.setSessionKey(null)
        reloadPairing()
        banner("已忘记这台电脑，请重新扫码配对", ok = true)
    }

    // ---- 解锁主流程 --------------------------------------------------------

    fun startUnlock() {
        val pairing = store.load()
        if (pairing == null) {
            banner("尚未配对电脑，请先在「配对」页扫码或粘贴配对信息", ok = false)
            return
        }
        if (!blePermissionHint) {
            banner("缺少「附近的设备」权限", ok = false)
            return
        }
        pendingUnlock = true
        ble.peerId = store.peerId()
        _ui.update { it.copy(banner = null, busy = true, awaitingBiometric = false) }

        val phase = ble.status.value.phase
        if (phase == BlePhase.READY && !ble.sessionEstablished) {
            // 复用现有连接：重新读取 challenge（保证使用最新 NONCE）
            if (!ble.readChallenge()) {
                pendingUnlock = false
                _ui.update { it.copy(busy = false) }
                banner("读取 challenge 失败，正在重新连接…", ok = false)
                ble.startScan(pairing.hostName)
            }
        } else {
            nonce = null
            ble.startScan(pairing.hostName)
        }
    }

    /** UI 侧权限检查通过后置位（仅用于状态提示，真正校验在 BlePermissions）。 */
    var blePermissionHint: Boolean = true

    fun cancelUnlock() {
        pendingUnlock = false
        _ui.update { it.copy(busy = false, awaitingBiometric = false) }
    }

    fun connectOnly() {
        val pairing = store.load() ?: run {
            banner("尚未配对电脑", ok = false)
            return
        }
        pendingUnlock = false
        ble.peerId = store.peerId()
        _ui.update { it.copy(banner = null, busy = true) }
        ble.startScan(pairing.hostName)
    }

    fun disconnect() {
        pendingUnlock = false
        ble.disconnect()
        _ui.update { it.copy(busy = false, awaitingBiometric = false) }
    }

    fun isBluetoothEnabled(): Boolean = try {
        val manager = getApplication<Application>().getSystemService(android.content.Context.BLUETOOTH_SERVICE) as? android.bluetooth.BluetoothManager
        manager?.adapter?.isEnabled == true
    } catch (_: Throwable) {
        false
    }

    // ---- 指纹回调（PROOF 只在成功后计算，协议第 1 节安全要点） --------------

    fun onBiometricSuccess() {
        val pairing = store.load()
        val n = nonce
        if (pairing == null || n == null) {
            _ui.update { it.copy(awaitingBiometric = false, busy = false) }
            banner("会话已失效，请重试", ok = false)
            return
        }
        _ui.update { it.copy(awaitingBiometric = false) }
        viewModelScope.launch {
            val proof = TcProtocol.computeProof(pairing.psk, n, pairing.hostId, pairing.peerId)
            val session = TcProtocol.deriveSessionKey(pairing.psk, n)
            kSession = session
            txCounter = 0L
            ble.setSessionKey(session)
            val ok = ble.writeProof(TcProtocol.proofFrame(proof))
            if (!ok) {
                pendingUnlock = false
                _ui.update { it.copy(busy = false) }
                banner("发送 PROOF 失败，请重试", ok = false)
            }
        }
    }

    fun onBiometricError(message: String) {
        pendingUnlock = false
        _ui.update { it.copy(awaitingBiometric = false, busy = false) }
        banner(message, ok = false)
    }

    // ---- BLE 事件 ----------------------------------------------------------

    private fun handleEvent(event: BleEvent) {
        when (event) {
            is BleEvent.Nonce -> {
                nonce = event.value
                if (pendingUnlock) {
                    _ui.update { it.copy(awaitingBiometric = true, busy = false) }
                }
            }

            is BleEvent.Message -> handleMessage(event)

            is BleEvent.Rejected -> {
                banner("收到非法数据帧：${event.reason}", ok = false)
            }

            is BleEvent.Failure -> {
                pendingUnlock = false
                _ui.update { it.copy(busy = false, awaitingBiometric = false) }
                banner(event.message, ok = false)
            }
        }
    }

    private fun handleMessage(event: BleEvent.Message) {
        val obj = try {
            MiniJson.parseObject(event.json)
        } catch (t: Throwable) {
            null
        } ?: return

        when (event.type) {
            TcProtocol.TYPE_READY -> {
                val sid = kSession?.let { key ->
                    TcProtocol.sessionIdFromReady(key, event.raw)
                } ?: (obj["sid"] as? String)
                _ui.update { it.copy(sessionId = sid) }
                ble.markSessionEstablished()
                // 会话已建立 → 立即发送 unlock（counter 从 1 开始）
                viewModelScope.launch { sendUnlock() }
            }

            TcProtocol.TYPE_UNLOCK_RESULT -> {
                val ok = obj["ok"] as? Boolean ?: false
                val reason = obj["reason"] as? String ?: "unknown"
                val locked = obj["locked"] as? Boolean
                val sent = obj["sent"] as? Boolean
                val text = describeUnlockResult(ok, reason, locked, sent)
                pendingUnlock = false
                val now = System.currentTimeMillis()
                if (ok) {
                    prefs.lastUnlockAt = now
                    prefs.lastResult = text
                }
                _ui.update {
                    it.copy(
                        busy = false,
                        awaitingBiometric = false,
                        lastUnlockAt = if (ok) now else it.lastUnlockAt,
                        lastResult = if (ok) text else it.lastResult,
                    )
                }
                banner(text, ok = ok)
                if (ok && prefs.autoDisconnect) {
                    ble.disconnect()
                }
            }

            TcProtocol.TYPE_PONG -> Unit

            TcProtocol.TYPE_ERROR -> {
                val code = obj["code"] as? String ?: "unknown"
                val msg = obj["msg"] as? String ?: ""
                pendingUnlock = false
                _ui.update { it.copy(busy = false, awaitingBiometric = false) }
                banner("电脑返回错误：${describeErrorCode(code)}${if (msg.isBlank()) "" else "（$msg）"}", ok = false)
            }

            else -> Unit
        }
    }

    private suspend fun sendUnlock() {
        val key = kSession ?: return
        val counter = ++txCounter
        val frame = TcProtocol.sealJson(key, counter, TcProtocol.unlockJson())
        val ok = ble.writeSealed(frame)
        if (!ok) {
            pendingUnlock = false
            _ui.update { it.copy(busy = false) }
            banner("发送解锁指令失败", ok = false)
        }
    }

    private fun describeUnlockResult(ok: Boolean, reason: String, locked: Boolean?, sent: Boolean?): String {
        return when (reason) {
            "ok" -> "解锁成功" + if (locked == false) "（电脑未锁屏）" else ""
            "no-password" -> "电脑端未配置密码，无法注入"
            "not-locked" -> "电脑当前未锁屏，已跳过注入"
            "inject-failed" -> "密码注入失败${if (sent == false) "（未发送）" else ""}"
            "unsupported" -> "密码包含无法映射的字符，已整段中止"
            "throttled" -> "操作过于频繁（1.5 秒内重复请求）"
            else -> if (ok) "解锁成功" else "解锁失败（$reason）"
        }
    }

    private fun describeErrorCode(code: String): String = when (code) {
        "bad-frame" -> "帧结构非法"
        "not-authenticated" -> "未通过验证就发送命令"
        "auth-failed" -> "PROOF 校验失败（PSK 可能已失效，请重新配对）"
        "replay" -> "计数器未递增（疑似重放）"
        "decrypt-failed" -> "解密失败"
        "nopsz" -> "电脑未配置密码"
        "busy" -> "电脑正忙"
        else -> code
    }

    // ---- 设置 / 自测 -------------------------------------------------------

    fun setAutoDisconnect(value: Boolean) {
        prefs.autoDisconnect = value
        _ui.update { it.copy(autoDisconnect = value) }
    }

    fun runSelfTest() {
        _ui.update { it.copy(selfTestRunning = true, selfTestReport = null, selfTestOk = null) }
        viewModelScope.launch {
            val result = withContext(Dispatchers.Default) { LoopbackSelfTest.run() }
            val report = result.lines.joinToString("\n")
            // 便于 adb 自动化：adb shell am start -n com.tctools.unlock/.MainActivity --ez selftest true
            //                  adb logcat -s TcSelfTest
            android.util.Log.i("TcSelfTest", "SELFTEST_RESULT=${if (result.ok) "PASS" else "FAIL"}")
            report.lines().forEach { android.util.Log.i("TcSelfTest", it) }
            _ui.update {
                it.copy(
                    selfTestRunning = false,
                    selfTestOk = result.ok,
                    selfTestReport = report,
                )
            }
        }
    }

    fun clearBanner() {
        _ui.update { it.copy(banner = null) }
    }

    private fun banner(text: String, ok: Boolean) {
        _ui.update { it.copy(banner = text, bannerIsError = !ok) }
    }

    fun connectDevice(device: BluetoothDevice, name: String?) {
        ble.connect(device, name)
    }

    override fun onCleared() {
        ble.close()
        super.onCleared()
    }

    /** 供 UI 显示对端名称（配对名优先，其次广播名）。 */
    fun displayHostName(state: UiState): String =
        state.hostName ?: state.advertisedName ?: "未配对"

    val protocolVersion: String = "v${TcProtocol.PROTOCOL_VERSION}"
    val appVersion: String = BuildConfig.VERSION_NAME
    val androidInfo: String = "Android ${Build.VERSION.RELEASE}（API ${Build.VERSION.SDK_INT}）"
    val peerIdValue: String get() = store.peerId()
    val storageModeValue: String get() = store.storageMode
}
