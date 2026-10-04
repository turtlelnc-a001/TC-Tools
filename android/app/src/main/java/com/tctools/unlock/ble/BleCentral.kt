package com.tctools.unlock.ble

import android.annotation.SuppressLint
import android.bluetooth.BluetoothGatt
import android.bluetooth.BluetoothGattCallback
import android.bluetooth.BluetoothGattCharacteristic
import android.bluetooth.BluetoothGattDescriptor
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothProfile
import android.bluetooth.le.ScanCallback
import android.bluetooth.le.ScanFilter
import android.bluetooth.le.ScanResult
import android.bluetooth.le.ScanSettings
import android.content.Context
import android.os.Build
import android.os.ParcelUuid
import android.util.Log
import com.tctools.unlock.protocol.MiniJson
import com.tctools.unlock.protocol.SealedFrameAssembler
import com.tctools.unlock.protocol.TcProtocol
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import java.util.UUID

enum class BlePhase { IDLE, SCANNING, CONNECTING, DISCOVERING, READY, FAILED, DISCONNECTED }

data class BleStatus(
    val phase: BlePhase = BlePhase.IDLE,
    val rssi: Int? = null,
    val mtu: Int? = null,
    val deviceName: String? = null,
    val message: String? = null,
)

sealed interface BleEvent {
    /** 读到 32 字节 challenge（NONCE）。 */
    data class Nonce(val value: ByteArray) : BleEvent
    /** 成功解出一条 Host → 手机 的加密消息（[raw] 为完整 SEALED 帧，用于计算 SESSION_ID）。 */
    data class Message(val counter: Long, val json: String, val type: String?, val raw: ByteArray) : BleEvent {
        override fun equals(other: Any?): Boolean =
            other is Message && counter == other.counter && json == other.json && type == other.type

        override fun hashCode(): Int = (counter.hashCode() * 31 + json.hashCode()) * 31 + (type?.hashCode() ?: 0)
    }
    /** 帧被拒（认证失败 / 重放 / 结构非法）。 */
    data class Rejected(val reason: String) : BleEvent
    data class Failure(val message: String) : BleEvent
}

/**
 * BLE GATT Central（协议第 3 节）。
 *
 * - 扫描：按 Service UUID 过滤（`7a1c9e40-…`）
 * - 连接后发现服务 → `requestMtu(517)` → 打开 Characteristic A 的 Notify → 读取 32 字节 challenge
 * - 写 PROOF（Characteristic B，裸 32 字节）
 * - 写 SEALED 命令（Characteristic B，`IV||ct||tag`）
 * - 收 Notify：交给 [SealedFrameAssembler] 做分片重组 + 重放校验
 *
 * 所有 GATT 操作经由单条串行队列下发（Android 同一时刻只允许一个未完成的 GATT 操作）。
 */
@SuppressLint("MissingPermission")
class BleCentral(private val context: Context) {

    companion object {
        private const val TAG = "TcBle"
        private const val SCAN_TIMEOUT_MS = 15_000L
        private const val OP_TIMEOUT_MS = 8_000L
        private const val MTU_FALLBACK_MS = 3_000L
        val SERVICE: UUID = UUID.fromString(TcProtocol.SERVICE_UUID)
        val CHAR_CHALLENGE: UUID = UUID.fromString(TcProtocol.CHAR_CHALLENGE_UUID)
        val CHAR_COMMAND: UUID = UUID.fromString(TcProtocol.CHAR_COMMAND_UUID)
        val CCCD: UUID = UUID.fromString(TcProtocol.CCCD_UUID)
    }

    private val scope = CoroutineScope(Dispatchers.Main.immediate + SupervisorJob())

    private val _status = MutableStateFlow(BleStatus())
    val status: StateFlow<BleStatus> = _status.asStateFlow()

    private val _events = MutableSharedFlow<BleEvent>(extraBufferCapacity = 64)
    val events: SharedFlow<BleEvent> = _events.asSharedFlow()

    private var gatt: BluetoothGatt? = null
    private var challengeChar: BluetoothGattCharacteristic? = null
    private var commandChar: BluetoothGattCharacteristic? = null
    private var assembler: SealedFrameAssembler? = null
    private var scanTimeoutJob: Job? = null
    private var graceConnectJob: Job? = null
    private var firstCandidate: android.bluetooth.BluetoothDevice? = null
    private var bestRssi: Int = Int.MIN_VALUE
    var preferredName: String? = null

    /**
     * 本机 PEER_ID（36 字符小写 UUID 字符串）。
     * 认证前以 36 字节 ASCII 写入 Command 特征（IDENT 帧，协议 §3.3.1），
     * Host 用它与 HOST_ID、NONCE 一起计算期望的 PROOF。
     */
    @Volatile
    var peerId: String? = null

    /** 本次连接是否已经建立过会话（用于避免复用 NONCE）。 */
    var sessionEstablished: Boolean = false
        private set

    private class Op(
        val action: (BluetoothGatt) -> Boolean,
        val done: CompletableDeferred<Boolean>,
    )

    private val opQueue = Channel<Op>(Channel.UNLIMITED)
    private var pendingOp: Op? = null
    private val handshakeStarted = java.util.concurrent.atomic.AtomicBoolean(false)

    init {
        scope.launch {
            for (op in opQueue) {
                val g = gatt
                if (g == null) {
                    op.done.complete(false)
                    continue
                }
                pendingOp = op
                val issued = try {
                    op.action(g)
                } catch (t: Throwable) {
                    Log.w(TAG, "GATT op 下发异常", t)
                    false
                }
                if (!issued) {
                    pendingOp = null
                    op.done.complete(false)
                    continue
                }
                val settled = withTimeoutOrNull(OP_TIMEOUT_MS) { op.done.await() }
                if (settled == null) {
                    pendingOp = null
                    op.done.complete(false)
                }
            }
        }
    }

    // ---- 对外 API ----------------------------------------------------------

    fun startScan(hostName: String?) {
        preferredName = hostName
        firstCandidate = null
        val adapter = adapter() ?: run { fail("本机不支持蓝牙"); return }
        if (!adapter.isEnabled) {
            fail("蓝牙未开启，请在系统设置中打开蓝牙")
            return
        }
        val scanner = adapter.bluetoothLeScanner ?: run { fail("无法获取 BLE 扫描器"); return }
        stopScan()
        sessionEstablished = false
        _status.value = BleStatus(phase = BlePhase.SCANNING, message = "正在搜索电脑…", deviceName = null)

        val filters = listOf(
            ScanFilter.Builder().setServiceUuid(ParcelUuid(SERVICE)).build()
        )
        val settings = ScanSettings.Builder()
            .setScanMode(ScanSettings.SCAN_MODE_LOW_LATENCY)
            .build()
        try {
            scanner.startScan(filters, settings, scanCallback)
        } catch (t: Throwable) {
            fail("启动扫描失败：${t.message}")
            return
        }
        scanTimeoutJob = scope.launch {
            delay(SCAN_TIMEOUT_MS)
            if (_status.value.phase == BlePhase.SCANNING) {
                stopScan()
                fail("未发现电脑广播。请确认：①电脑端「蓝牙解锁服务」已启动；②手机与电脑相距 3 米内；③已授予「附近的设备」权限")
            }
        }
    }

    fun stopScan() {
        scanTimeoutJob?.cancel(); scanTimeoutJob = null
        graceConnectJob?.cancel(); graceConnectJob = null
        try {
            adapter()?.bluetoothLeScanner?.stopScan(scanCallback)
        } catch (_: Throwable) {
        }
    }

    fun connect(device: android.bluetooth.BluetoothDevice, name: String?) {
        stopScan()
        closeGatt()
        handshakeStarted.set(false)
        _status.value = BleStatus(phase = BlePhase.CONNECTING, deviceName = name, rssi = _status.value.rssi)
        gatt = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            device.connectGatt(context, false, gattCallback, android.bluetooth.BluetoothDevice.TRANSPORT_LE)
        } else {
            device.connectGatt(context, false, gattCallback)
        }
        if (gatt == null) fail("发起连接失败")
    }

    /** 读取最新的 challenge（每次尝试解锁前调用，保证 NONCE 不复用）。 */
    fun readChallenge(): Boolean {
        val g = gatt ?: return false
        val ch = challengeChar ?: return false
        return try {
            g.readCharacteristic(ch)
        } catch (t: Throwable) {
            fail("读取 challenge 失败：${t.message}")
            false
        }
    }

    /** 设置本次会话的 K_session（指纹通过后才调用），供 Notify 解密使用。 */
    fun setSessionKey(key: ByteArray?) {
        assembler = key?.let { SealedFrameAssembler(it) }
    }

    /** 写入 PROOF 帧（裸 32 字节，第 3.3.1 节）。 */
    suspend fun writeProof(proof: ByteArray): Boolean {
        val ch = commandChar ?: return false
        return runOp { g -> writeChar(g, ch, proof, BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT) }
    }

    /** 写入 SEALED 帧（第 3.3.2 节）。 */
    suspend fun writeSealed(frame: ByteArray): Boolean {
        val ch = commandChar ?: return false
        return runOp { g -> writeChar(g, ch, frame, BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT) }
    }

    fun disconnect() {
        stopScan()
        try {
            gatt?.disconnect()
        } catch (_: Throwable) {
        }
        closeGatt()
        if (_status.value.phase != BlePhase.FAILED) {
            _status.value = _status.value.copy(phase = BlePhase.DISCONNECTED, message = "已断开")
        }
    }

    fun close() {
        stopScan()
        closeGatt()
        scope.coroutineContext[Job]?.cancel()
    }

    // ---- 内部实现 ----------------------------------------------------------

    private fun adapter(): android.bluetooth.BluetoothAdapter? =
        (context.getSystemService(Context.BLUETOOTH_SERVICE) as? BluetoothManager)?.adapter

    private fun closeGatt() {
        pendingOp?.done?.complete(false)
        pendingOp = null
        handshakeStarted.set(false)
        try {
            gatt?.close()
        } catch (_: Throwable) {
        }
        gatt = null
        challengeChar = null
        commandChar = null
        assembler = null
    }

    private fun fail(message: String) {
        Log.w(TAG, message)
        _status.value = _status.value.copy(phase = BlePhase.FAILED, message = message)
        _events.tryEmit(BleEvent.Failure(message))
    }

    private val scanCallback = object : ScanCallback() {
        override fun onScanResult(callbackType: Int, result: ScanResult) {
            val name = try {
                result.scanRecord?.deviceName ?: result.device.name
            } catch (_: Throwable) {
                null
            }
            _status.value = _status.value.copy(rssi = result.rssi, deviceName = name)
            val wanted = preferredName
            if (wanted != null && name != null && name.equals(wanted, ignoreCase = true)) {
                stopScan()
                connect(result.device, name)
                return
            }
            if (firstCandidate == null) {
                firstCandidate = result.device
                bestRssi = result.rssi
                // 电脑端只广播 Service UUID、不广播名字，因此过滤器已足以识别；
                // 这里留 1.2 秒窗口挑选信号最强的设备，然后再连接。
                graceConnectJob = scope.launch {
                    delay(1200)
                    val dev = firstCandidate
                    if (dev != null && _status.value.phase == BlePhase.SCANNING) {
                        stopScan()
                        connect(dev, _status.value.deviceName ?: preferredName)
                    }
                }
            } else if (result.rssi > bestRssi) {
                firstCandidate = result.device
                bestRssi = result.rssi
            }
        }

        override fun onScanFailed(errorCode: Int) {
            fail("扫描失败（错误码 $errorCode）")
        }
    }

    private val gattCallback = object : BluetoothGattCallback() {

        override fun onConnectionStateChange(g: BluetoothGatt, status: Int, newState: Int) {
            Log.i(TAG, "连接状态变化 status=$status newState=$newState")
            when (newState) {
                BluetoothProfile.STATE_CONNECTED -> {
                    _status.value = _status.value.copy(phase = BlePhase.DISCOVERING, message = "已连接，正在发现服务…")
                    if (!g.discoverServices()) fail("发现 GATT 服务失败")
                }

                BluetoothProfile.STATE_DISCONNECTED -> {
                    val wasFailed = _status.value.phase == BlePhase.FAILED
                    closeGatt()
                    if (!wasFailed) {
                        val detail = if (status == BluetoothGatt.GATT_SUCCESS) "" else "（GATT status=$status，常见 133=GATT_ERROR，可重试）"
                        _status.value = _status.value.copy(phase = BlePhase.DISCONNECTED, message = "连接已断开$detail")
                    }
                }
            }
        }

        override fun onServicesDiscovered(g: BluetoothGatt, status: Int) {
            if (status != BluetoothGatt.GATT_SUCCESS) {
                fail("发现服务失败（status=$status）")
                return
            }
            val svc = g.getService(SERVICE)
            if (svc == null) {
                fail("未找到 TC-Tools 服务，请确认电脑端已启动解锁服务")
                return
            }
            challengeChar = svc.getCharacteristic(CHAR_CHALLENGE)
            commandChar = svc.getCharacteristic(CHAR_COMMAND)
            if (challengeChar == null || commandChar == null) {
                fail("服务缺少 challenge/command 特征")
                return
            }
            // 优先请求更大 MTU；失败也继续（默认 23 也能跑，靠分片重组）
            if (!g.requestMtu(517)) {
                enableNotificationsAndRead(g)
            } else {
                // 兜底：个别机型/协议栈不回调 onMtuChanged，3 秒后按默认 MTU 继续握手
                scope.launch {
                    delay(MTU_FALLBACK_MS)
                    if (_status.value.phase == BlePhase.DISCOVERING) {
                        Log.w(TAG, "onMtuChanged 未回调，按默认 MTU 继续")
                        enableNotificationsAndRead(g)
                    }
                }
            }
        }

        override fun onMtuChanged(g: BluetoothGatt, mtu: Int, status: Int) {
            _status.value = _status.value.copy(mtu = if (status == BluetoothGatt.GATT_SUCCESS) mtu else null)
            enableNotificationsAndRead(g)
        }

        override fun onDescriptorWrite(g: BluetoothGatt, descriptor: BluetoothGattDescriptor, status: Int) {
            pendingOp?.done?.complete(status == BluetoothGatt.GATT_SUCCESS)
            pendingOp = null
        }

        override fun onCharacteristicWrite(g: BluetoothGatt, ch: BluetoothGattCharacteristic, status: Int) {
            pendingOp?.done?.complete(status == BluetoothGatt.GATT_SUCCESS)
            pendingOp = null
        }

        // API 33+
        override fun onCharacteristicRead(
            g: BluetoothGatt,
            ch: BluetoothGattCharacteristic,
            value: ByteArray,
            status: Int,
        ) {
            handleChallenge(value, status)
        }

        // API < 33
        @Deprecated("Deprecated in Java")
        @Suppress("DEPRECATION")
        override fun onCharacteristicRead(g: BluetoothGatt, ch: BluetoothGattCharacteristic, status: Int) {
            handleChallenge(ch.value ?: ByteArray(0), status)
        }

        // API 33+
        override fun onCharacteristicChanged(
            g: BluetoothGatt,
            ch: BluetoothGattCharacteristic,
            value: ByteArray,
        ) {
            handleNotify(value)
        }

        // API < 33
        @Deprecated("Deprecated in Java")
        @Suppress("DEPRECATION")
        override fun onCharacteristicChanged(g: BluetoothGatt, ch: BluetoothGattCharacteristic) {
            handleNotify(ch.value ?: ByteArray(0))
        }
    }

    private fun enableNotificationsAndRead(g: BluetoothGatt) {
        // 保证握手只走一次（onMtuChanged 与 MTU 兜底定时器可能竞争）
        if (!handshakeStarted.compareAndSet(false, true)) return
        val ch = challengeChar ?: return
        if (!g.setCharacteristicNotification(ch, true)) {
            fail("开启 Notify 失败")
            return
        }
        scope.launch {
            val cccd = ch.getDescriptor(CCCD)
            if (cccd != null) {
                runOp { gg -> writeDescriptor(gg, cccd) }
            }

            // 协议 §3.3.1：认证前先写 36 字节 IDENT（PEER_ID 的 ASCII 小写 UUID），
            // 等 onCharacteristicWrite 回调确认后再读 challenge。IDENT 不含任何密钥。
            val ident = peerId?.trim()?.lowercase()
            if (ident == null || ident.length != 36) {
                fail("缺少本机 PEER_ID，无法完成 IDENT 握手（请在设置中重新配对）")
                return@launch
            }
            val cmd = commandChar
            if (cmd == null) {
                fail("命令特征（Characteristic B）不可用")
                return@launch
            }
            val identSent = runOp { gg ->
                writeChar(gg, cmd, TcProtocol.ascii(ident), BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT)
            }
            if (!identSent) {
                fail("写入 IDENT 帧失败")
                return@launch
            }

            _status.value = _status.value.copy(phase = BlePhase.READY, message = "已连接，等待指纹验证")
            if (!readChallenge()) {
                fail("读取 challenge 失败")
            }
        }
    }

    private fun handleChallenge(value: ByteArray, status: Int) {
        if (status != BluetoothGatt.GATT_SUCCESS) {
            fail("读取 challenge 返回错误（status=$status）")
            return
        }
        if (value.size != TcProtocol.NONCE_LEN) {
            fail("challenge 长度异常：${value.size} 字节（协议要求 ${TcProtocol.NONCE_LEN} 字节）")
            return
        }
        _events.tryEmit(BleEvent.Nonce(value))
    }

    private fun handleNotify(value: ByteArray) {
        if (value.isEmpty()) return
        val asm = assembler
        if (asm == null) {
            Log.w(TAG, "收到 Notify 但尚无会话密钥，丢弃 ${value.size} 字节")
            return
        }
        for (outcome in asm.feed(value)) {
            when (outcome) {
                is SealedFrameAssembler.Outcome.Message -> {
                    val type = runCatching {
                        MiniJson.parseObject(outcome.json)?.get("type") as? String
                    }.getOrNull()
                    _events.tryEmit(BleEvent.Message(outcome.counter, outcome.json, type, outcome.raw))
                }

                is SealedFrameAssembler.Outcome.Rejected -> _events.tryEmit(BleEvent.Rejected(outcome.reason))
                SealedFrameAssembler.Outcome.NeedMore -> Unit
            }
        }
    }

    private suspend fun runOp(action: (BluetoothGatt) -> Boolean): Boolean {
        val deferred = CompletableDeferred<Boolean>()
        opQueue.send(Op(action, deferred))
        return withTimeoutOrNull(OP_TIMEOUT_MS + 2_000L) { deferred.await() } ?: false
    }

    // ---- 兼容新旧 API 的薄封装 ---------------------------------------------

    @Suppress("DEPRECATION")
    private fun writeChar(
        g: BluetoothGatt,
        ch: BluetoothGattCharacteristic,
        value: ByteArray,
        writeType: Int,
    ): Boolean = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
        g.writeCharacteristic(ch, value, writeType) == BluetoothGatt.GATT_SUCCESS
    } else {
        ch.writeType = writeType
        ch.value = value
        g.writeCharacteristic(ch)
    }

    @Suppress("DEPRECATION")
    private fun writeDescriptor(g: BluetoothGatt, descriptor: BluetoothGattDescriptor): Boolean =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            g.writeDescriptor(descriptor, BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE) == BluetoothGatt.GATT_SUCCESS
        } else {
            descriptor.value = BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE
            g.writeDescriptor(descriptor)
        }

    /** 会话已建立（收到 ready）：此后再次解锁必须重新建连，避免复用 NONCE。 */
    fun markSessionEstablished() {
        sessionEstablished = true
    }
}
