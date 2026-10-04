package com.tctools.unlock.ui

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import com.tctools.unlock.UiState

/**
 * 设置页：分组式列表（inset grouped）。
 * 包含「重新配对 / 忘记这台电脑 / 回环自测 / 关于」，以及行为开关。
 */
@Composable
fun SettingsScreen(
    state: UiState,
    appVersion: String,
    protocolVersion: String,
    androidInfo: String,
    peerId: String,
    onBack: () -> Unit,
    onRepair: () -> Unit,
    onForget: () -> Unit,
    onRunSelfTest: () -> Unit,
    onSetAutoDisconnect: (Boolean) -> Unit,
    onOpenAbout: () -> Unit,
) {
    val palette = LocalIosPalette.current
    var confirmForget by remember { mutableStateOf(false) }
    var showSelfTest by remember { mutableStateOf(false) }

    if (confirmForget) {
        IosAlertDialog(
            title = "忘记这台电脑？",
            message = "本机保存的 PSK 将被删除，需要重新扫码配对。",
            confirmText = "忘记",
            destructive = true,
            onConfirm = {
                confirmForget = false
                onForget()
            },
            onDismiss = { confirmForget = false },
        )
    }

    if (showSelfTest) {
        SelfTestDialog(
            report = state.selfTestReport,
            ok = state.selfTestOk,
            running = state.selfTestRunning,
            onDismiss = { showSelfTest = false },
        )
    }

    IosScreen(title = "设置", subtitle = null, onBack = onBack) {
        state.banner?.let {
            IosBanner(text = it, ok = !state.bannerIsError)
            Spacer(Modifier.height(IosDimens.gap))
        }

        IosCardTitle("目标电脑")
        IosGroupedCard {
            IosRow(
                title = state.hostName ?: "未配对",
                subtitle = if (state.hasPairing) "已配对 · ${formatTimestamp(state.pairedAt)}" else "请先扫码配对",
                icon = { Icon(Icons.Filled.Info, null, tint = androidx.compose.ui.graphics.Color.White, modifier = Modifier.height(16.dp)) },
            )
            IosDivider()
            IosKeyValue("HOST_ID", state.hostId ?: "—", mono = true)
            IosDivider()
            IosKeyValue("PEER_ID", peerId, mono = true)
            IosDivider()
            IosKeyValue("PSK 存储", state.storageMode.ifBlank { "—" })
        }

        Spacer(Modifier.height(IosDimens.gap))

        IosCardTitle("配对")
        IosGroupedCard {
            IosRow(
                title = "重新配对",
                subtitle = "扫描新的二维码，生成新的 PSK（旧 PSK 立即失效）",
                showChevron = true,
                onClick = onRepair,
            )
            IosDivider()
            IosRow(
                title = "忘记这台电脑",
                subtitle = "删除本机保存的 PSK",
                destructive = true,
                onClick = { confirmForget = true },
            )
        }

        Spacer(Modifier.height(IosDimens.gap))

        IosCardTitle("解锁行为")
        IosGroupedCard {
            IosSwitchRow(
                title = "解锁成功后自动断开",
                subtitle = "省电；下次解锁会重新建立连接",
                checked = state.autoDisconnect,
                onCheckedChange = onSetAutoDisconnect,
            )
        }

        Spacer(Modifier.height(IosDimens.gap))

        IosCardTitle("诊断")
        IosGroupedCard {
            IosRow(
                title = "协议回环自测（§8 向量）",
                subtitle = "在手机内验证 HMAC / AES-256-GCM / base64url / 计数器 IV",
                value = when (state.selfTestOk) {
                    true -> "通过"
                    false -> "失败"
                    null -> null
                },
                showChevron = true,
                onClick = {
                    showSelfTest = true
                    onRunSelfTest()
                },
            )
            IosDivider()
            IosRow(
                title = "关于",
                showChevron = true,
                onClick = onOpenAbout,
            )
        }

        Spacer(Modifier.height(24.dp))
    }
}

/** 协议回环自测结果弹窗（等宽字体，可直接截图给验证负责人比对）。 */
@Composable
fun SelfTestDialog(report: String?, ok: Boolean?, running: Boolean, onDismiss: () -> Unit) {
    val palette = LocalIosPalette.current
    Dialog(onDismissRequest = onDismiss) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .heightIn(max = 520.dp),
        ) {
            IosGroupedCard {
                Column(modifier = Modifier.padding(16.dp)) {
                    Text(
                        text = if (running) "正在运行回环自测…" else if (ok == true) "回环自测：全部通过 ✅" else "回环自测：存在失败 ❌",
                        style = IosType.headline,
                        color = if (ok == false) palette.destructive else palette.label,
                    )
                    Spacer(Modifier.height(10.dp))
                    Column(
                        modifier = Modifier
                            .heightIn(max = 380.dp)
                            .verticalScroll(rememberScrollState()),
                    ) {
                        Text(
                            text = report ?: "…",
                            style = IosType.mono.copy(fontFamily = FontFamily.Monospace, fontSize = 11.sp),
                            color = palette.label,
                        )
                    }
                    Spacer(Modifier.height(12.dp))
                    IosSecondaryButton(text = "关闭", onClick = onDismiss)
                }
            }
        }
    }
}

/** 关于页。 */
@Composable
fun AboutScreen(
    appVersion: String,
    protocolVersion: String,
    androidInfo: String,
    onBack: () -> Unit,
) {
    IosScreen(title = "关于", subtitle = null, onBack = onBack) {
        IosGroupedCard {
            IosKeyValue("应用名称", "TC-Tools 解锁电脑")
            IosDivider()
            IosKeyValue("应用版本", appVersion)
            IosDivider()
            IosKeyValue("协议版本", protocolVersion)
            IosDivider()
            IosKeyValue("运行环境", androidInfo)
            IosDivider()
            IosKeyValue("电脑端", "Windows 10 1709+（GATT Server）")
        }

        Spacer(Modifier.height(IosDimens.gap))

        IosCardTitle("安全说明")
        IosGroupedCard {
            Column(modifier = Modifier.padding(16.dp)) {
                IosHint("· PSK 永不通过蓝牙传输，只经二维码/文本传递（协议第 1 节）")
                IosHint("· 每次连接使用新的 32 字节 challenge，会话密钥一次性")
                IosHint("· PROOF 只在指纹验证通过后才计算并发送")
                IosHint("· 所有 unlock 指令均为 AES-256-GCM 封装，counter 严格递增防重放")
                IosHint("· 配对信息保存在系统加密存储中（EncryptedSharedPreferences / Keystore）")
            }
        }

        Spacer(Modifier.height(IosDimens.gap))

        IosCardTitle("协议要点")
        IosGroupedCard {
            IosKeyValue("Service UUID", "7a1c9e40-…-6c5d3e8f2b01", mono = true)
            IosDivider()
            IosKeyValue("Challenge", "7a1c9e41-…（Read+Notify）", mono = true)
            IosDivider()
            IosKeyValue("Command", "7a1c9e42-…（Write）", mono = true)
            IosDivider()
            IosKeyValue("SEAL 线格式", "IV(12)+ct+tag(16)", mono = true)
            IosDivider()
            IosKeyValue("IV", "counter(8B BE)+00000000", mono = true)
        }

        Spacer(Modifier.height(24.dp))
    }
}
