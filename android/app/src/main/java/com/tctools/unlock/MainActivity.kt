package com.tctools.unlock

import android.content.Intent
import android.os.Bundle
import android.provider.Settings
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.appcompat.app.AppCompatActivity
import androidx.biometric.BiometricManager
import androidx.biometric.BiometricPrompt
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.core.content.ContextCompat
import androidx.fragment.app.FragmentActivity
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import com.tctools.unlock.ble.BlePermissions
import com.tctools.unlock.ui.AboutScreen
import com.tctools.unlock.ui.HomeScreen
import com.tctools.unlock.ui.PairScreen
import com.tctools.unlock.ui.SettingsScreen
import com.tctools.unlock.ui.TcUnlockTheme

/** 应用唯一 Activity（AppCompatActivity：androidx.biometric 1.1.0 的 BiometricPrompt 需要 FragmentActivity）。 */
class MainActivity : AppCompatActivity() {

    private val viewModel: UnlockViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()

        // ---- debug 构建的自动化入口（供 Lead/verify 用 adb 跑全链路） ----
        //   adb shell am start -n com.tctools.unlock/.MainActivity \
        //       --es pairing_json '{"v":1,"p":"tcunlock",...}'   # 直接注入配对文本
        //   adb shell am start -n com.tctools.unlock/.MainActivity --ez selftest true   # 跑回环自测并打 logcat
        //   adb shell am start -n com.tctools.unlock/.MainActivity --ez unlock true     # 直接发起解锁（仍会弹指纹）
        var autoUnlock = false
        if (BuildConfig.DEBUG) {
            intent?.getStringExtra("pairing_json")?.takeIf { it.isNotBlank() }?.let { json ->
                val error = viewModel.pairFromText(json)
                android.util.Log.i("TcAutomation", "pairing_json 注入结果: ${error ?: "OK"}")
            }
            if (intent?.getBooleanExtra("selftest", false) == true) {
                viewModel.runSelfTest()
            }
            autoUnlock = intent?.getBooleanExtra("unlock", false) == true
        }

        setContent {
            TcUnlockApp(viewModel, autoUnlockOnStart = autoUnlock)
        }
    }
}

private sealed interface Screen {
    data object Home : Screen
    data object Pair : Screen
    data object Settings : Screen
    data object About : Screen
}

@Composable
private fun TcUnlockApp(vm: UnlockViewModel, autoUnlockOnStart: Boolean = false) {
    val state by vm.ui.collectAsStateWithLifecycle()
    val context = LocalContext.current
    var screen by remember { mutableStateOf<Screen>(Screen.Home) }
    var unlockAfterPermission by remember { mutableStateOf(false) }

    // ---- BLE 运行时权限 ----
    val permissionLauncher = androidx.activity.compose.rememberLauncherForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions()
    ) { result ->
        val granted = result.values.all { it } || BlePermissions.granted(context)
        vm.blePermissionHint = granted
        if (!granted) {
            unlockAfterPermission = false
            vm.onBiometricError("未授予「附近的设备」权限，无法连接电脑")
        } else if (unlockAfterPermission) {
            unlockAfterPermission = false
            vm.startUnlock()
        }
    }

    // ---- 扫码配对（ZXing embedded） ----
    val scanLauncher = androidx.activity.compose.rememberLauncherForActivityResult(ScanContract()) { result ->
        val contents = result.contents
        if (!contents.isNullOrBlank()) {
            vm.pairFromText(contents)
        }
    }

    fun requestUnlock() {
        vm.blePermissionHint = BlePermissions.granted(context)
        if (!BlePermissions.granted(context)) {
            unlockAfterPermission = true
            permissionLauncher.launch(BlePermissions.required)
            return
        }
        if (!vm.isBluetoothEnabled()) {
            vm.onBiometricError("蓝牙未开启，请在系统设置中打开蓝牙后重试")
            return
        }
        vm.startUnlock()
    }

    // ---- 指纹验证（PROOF 只在成功后由 ViewModel 计算） ----
    LaunchedEffect(state.awaitingBiometric) {
        if (!state.awaitingBiometric) return@LaunchedEffect
        val activity = context as? FragmentActivity
        if (activity == null) {
            vm.onBiometricError("无法启动指纹验证")
            return@LaunchedEffect
        }
        val authenticators = BiometricManager.Authenticators.BIOMETRIC_WEAK or
            BiometricManager.Authenticators.DEVICE_CREDENTIAL
        when (BiometricManager.from(activity).canAuthenticate(authenticators)) {
            BiometricManager.BIOMETRIC_SUCCESS -> Unit

            BiometricManager.BIOMETRIC_ERROR_NONE_ENROLLED -> {
                vm.onBiometricError("尚未录入指纹或锁屏密码。请先在系统设置中录入指纹后重试")
                return@LaunchedEffect
            }

            BiometricManager.BIOMETRIC_ERROR_NO_HARDWARE,
            BiometricManager.BIOMETRIC_ERROR_HW_UNAVAILABLE,
            BiometricManager.BIOMETRIC_ERROR_UNSUPPORTED,
            -> {
                vm.onBiometricError("本机没有可用的指纹识别硬件，请改用设备凭据（图案/密码）或在系统设置中开启")
                return@LaunchedEffect
            }

            else -> {
                vm.onBiometricError("本机不支持生物识别验证，请在系统设置中检查指纹与锁屏密码")
                return@LaunchedEffect
            }
        }

        val prompt = BiometricPrompt(
            activity,
            ContextCompat.getMainExecutor(activity),
            object : BiometricPrompt.AuthenticationCallback() {
                override fun onAuthenticationSucceeded(result: BiometricPrompt.AuthenticationResult) {
                    vm.onBiometricSuccess()
                }

                override fun onAuthenticationError(errorCode: Int, errString: CharSequence) {
                    val message = when (errorCode) {
                        BiometricPrompt.ERROR_USER_CANCELED,
                        BiometricPrompt.ERROR_NEGATIVE_BUTTON,
                        BiometricPrompt.ERROR_CANCELED,
                        -> "已取消指纹验证"

                        BiometricPrompt.ERROR_LOCKOUT -> "尝试次数过多，请稍后再试"
                        BiometricPrompt.ERROR_LOCKOUT_PERMANENT -> "指纹已被锁定，请先用锁屏密码解锁手机"
                        BiometricPrompt.ERROR_NO_BIOMETRICS -> "尚未录入指纹，请先在系统设置中录入"
                        BiometricPrompt.ERROR_HW_NOT_PRESENT -> "本机没有指纹识别硬件"
                        else -> errString.toString()
                    }
                    vm.onBiometricError(message)
                }

                override fun onAuthenticationFailed() {
                    // 单次比对失败：系统会自动允许重试，这里不打断流程
                }
            },
        )

        val info = BiometricPrompt.PromptInfo.Builder()
            .setTitle("指纹解锁")
            .setSubtitle("验证通过后解锁「${state.hostName ?: "电脑"}」")
            .setAllowedAuthenticators(authenticators)
            .build()
        prompt.authenticate(info)
    }

    // ---- 自测结果打到 logcat（adb logcat -s TcSelfTest） ----
    LaunchedEffect(state.selfTestReport) {
        state.selfTestReport?.lines()?.forEach { android.util.Log.i("TcSelfTest", it) }
    }

    // ---- debug: --ez unlock true 直接发起一次解锁（指纹仍然必须通过） ----
    LaunchedEffect(Unit) {
        if (autoUnlockOnStart) {
            requestUnlock()
        }
    }

    TcUnlockTheme {
        when (val s = screen) {
            Screen.Home -> HomeScreen(
                state = state,
                onUnlock = { requestUnlock() },
                onConnect = { vm.connectOnly() },
                onDisconnect = { vm.disconnect() },
                onOpenPairing = { screen = Screen.Pair },
                onOpenSettings = { screen = Screen.Settings },
                onDismissBanner = { vm.clearBanner() },
            )

            Screen.Pair -> PairScreen(
                state = state,
                onBack = { screen = Screen.Home },
                onScanQr = {
                    scanLauncher.launch(
                        ScanOptions()
                            .setPrompt("对准电脑上的配对二维码")
                            .setBeepEnabled(false)
                            .setOrientationLocked(false)
                            .setDesiredBarcodeFormats(ScanOptions.QR_CODE)
                    )
                },
                onSubmitText = { text -> vm.pairFromText(text) },
                onForget = { vm.forgetHost() },
            )

            Screen.Settings -> SettingsScreen(
                state = state,
                appVersion = vm.appVersion,
                protocolVersion = vm.protocolVersion,
                androidInfo = vm.androidInfo,
                peerId = vm.peerIdValue,
                onBack = { screen = Screen.Home },
                onRepair = { screen = Screen.Pair },
                onForget = { vm.forgetHost() },
                onRunSelfTest = { vm.runSelfTest() },
                onSetAutoDisconnect = { vm.setAutoDisconnect(it) },
                onOpenAbout = { screen = Screen.About },
            )

            Screen.About -> AboutScreen(
                appVersion = vm.appVersion,
                protocolVersion = vm.protocolVersion,
                androidInfo = vm.androidInfo,
                onBack = { screen = Screen.Settings },
            )
        }
    }
}

/** 用系统设置页打开应用详情（权限被永久拒绝时的兜底入口，当前 UI 未直接使用）。 */
@Suppress("unused")
private fun openAppSettings(activity: androidx.activity.ComponentActivity) {
    val intent = Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS).apply {
        data = android.net.Uri.fromParts("package", activity.packageName, null)
    }
    activity.startActivity(intent)
}
