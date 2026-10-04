package com.tctools.unlock.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tctools.unlock.UiState
import com.tctools.unlock.ble.BlePhase
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * 首页：连接状态卡片 + 目标电脑 + 信号强度 + 上次解锁时间 + 底部大按钮「指纹解锁」。
 */
@Composable
fun HomeScreen(
    state: UiState,
    onUnlock: () -> Unit,
    onConnect: () -> Unit,
    onDisconnect: () -> Unit,
    onOpenPairing: () -> Unit,
    onOpenSettings: () -> Unit,
    onDismissBanner: () -> Unit,
) {
    val palette = LocalIosPalette.current
    val connected = state.phase == BlePhase.READY
    val connecting = state.phase == BlePhase.SCANNING || state.phase == BlePhase.CONNECTING || state.phase == BlePhase.DISCOVERING

    IosScreen(
        title = "解锁电脑",
        subtitle = "TC-Tools · ${
            when {
                !state.hasPairing -> "尚未配对"
                connected -> "已连接"
                else -> state.hostName ?: "未连接"
            }
        }",
        trailing = {
            IosCircleIconButton(onClick = onOpenSettings) {
                Icon(
                    imageVector = Icons.Filled.Settings,
                    contentDescription = "设置",
                    tint = palette.accent,
                    modifier = Modifier.size(20.dp),
                )
            }
        },
        bottomBar = {
            Column {
                state.banner?.let {
                    IosBanner(text = it, ok = !state.bannerIsError, onDismiss = onDismissBanner)
                    Spacer(Modifier.height(10.dp))
                }
                IosPrimaryButton(
                    text = if (connected) "指纹解锁" else "连接并解锁",
                    onClick = onUnlock,
                    enabled = state.hasPairing,
                    loading = state.busy,
                )
                Spacer(Modifier.height(10.dp))
                when {
                    !state.hasPairing -> IosSecondaryButton(text = "去配对电脑", onClick = onOpenPairing)
                    connected -> IosSecondaryButton(text = "断开连接", onClick = onDisconnect)
                    else -> IosSecondaryButton(
                        text = if (connecting) "正在搜索…" else "重新连接",
                        onClick = onConnect,
                        enabled = !connecting,
                    )
                }
            }
        },
    ) {
        // ---- 指纹主视觉 ----
        IosGroupedCard {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(vertical = 24.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                Box(
                    modifier = Modifier
                        .size(96.dp)
                        .clip(CircleShape)
                        .background(palette.accent.copy(alpha = if (connected) 0.16f else 0.08f))
                        .clickable(enabled = state.hasPairing && !state.busy && !state.awaitingBiometric) { onUnlock() },
                    contentAlignment = Alignment.Center,
                ) {
                    FingerprintGlyph(
                        color = if (connected) palette.accent else palette.secondaryLabel,
                        modifier = Modifier.size(54.dp),
                        strokeWidth = 3.dp,
                    )
                }
                Spacer(Modifier.height(14.dp))
                Text(
                    text = state.hostName ?: "未配对电脑",
                    style = IosType.title,
                    color = palette.label,
                )
                Spacer(Modifier.height(6.dp))
                val (statusText, statusColor) = statusPresentation(state)
                StatusPill(text = statusText, color = statusColor)
            }
        }

        Spacer(Modifier.height(IosDimens.gap))

        // ---- 连接详情 ----
        IosCardTitle("连接状态")
        IosGroupedCard {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 16.dp, vertical = 14.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Column(modifier = Modifier.weight(1f)) {
                    Text("信号强度", style = IosType.subhead, color = palette.secondaryLabel)
                    Spacer(Modifier.height(4.dp))
                    Text(
                        text = state.rssi?.let { "$it dBm" } ?: "—",
                        style = IosType.body,
                        color = palette.label,
                    )
                }
                SignalBars(rssi = state.rssi)
            }
            IosDivider()
            IosKeyValue("蓝牙 MTU", state.mtu?.toString() ?: "—")
            IosDivider()
            IosKeyValue("会话编号", state.sessionId ?: "—", mono = true)
            IosDivider()
            IosKeyValue("上次解锁", formatTimestamp(state.lastUnlockAt))
            if (state.lastResult.isNotBlank()) {
                IosDivider()
                IosKeyValue("上次结果", state.lastResult)
            }
        }

        Spacer(Modifier.height(IosDimens.gap))

        IosCardTitle("使用提示")
        IosGroupedCard {
            Column(modifier = Modifier.padding(16.dp)) {
                IosHint("① 在电脑上打开「TC-Tools 蓝牙解锁」并显示配对二维码")
                IosHint("② 手机首次使用请先扫码或粘贴配对信息（PSK 只经二维码传递）")
                IosHint("③ 日常解锁：点亮屏幕 → 打开本应用 → 按指纹，认证通过后才会计算并发送 PROOF")
            }
        }
        Spacer(Modifier.height(24.dp))
    }
}

@Composable
private fun statusPresentation(state: UiState): Pair<String, Color> {
    val palette = LocalIosPalette.current
    return when (state.phase) {
        BlePhase.IDLE -> "未连接" to palette.gray
        BlePhase.SCANNING -> "正在搜索电脑…" to palette.warning
        BlePhase.CONNECTING -> "正在连接…" to palette.warning
        BlePhase.DISCOVERING -> "正在发现服务…" to palette.warning
        BlePhase.READY -> "已连接，等待指纹" to palette.success
        BlePhase.DISCONNECTED -> "已断开" to palette.gray
        BlePhase.FAILED -> (state.statusMessage ?: "连接失败") to palette.destructive
    }
}

/** 友好时间：刚刚 / N 分钟前 / MM-dd HH:mm:ss。 */
fun formatTimestamp(ts: Long): String {
    if (ts <= 0L) return "从未"
    val now = System.currentTimeMillis()
    val delta = now - ts
    return when {
        delta in 0..59_000 -> "刚刚"
        delta in 60_000..3_599_000 -> "${delta / 60_000} 分钟前"
        else -> SimpleDateFormat("MM-dd HH:mm:ss", Locale.getDefault()).format(Date(ts))
    }
}

private val IosPalette.gray: Color get() = IosColors.Gray
