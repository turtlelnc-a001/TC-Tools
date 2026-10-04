package com.tctools.unlock.ui

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Clear
import androidx.compose.material3.Icon
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tctools.unlock.UiState

/**
 * 配对页（协议第 4.1 节）：两条通道 —— 扫码 + 手动粘贴。
 * 手动粘贴通道是自动化测试与无障碍使用的必需路径，任何情况下都不得移除。
 */
@Composable
fun PairScreen(
    state: UiState,
    onBack: () -> Unit,
    onScanQr: () -> Unit,
    onSubmitText: (String) -> String?,
    onForget: () -> Unit,
) {
    val palette = LocalIosPalette.current
    var text by remember { mutableStateOf("") }
    var error by remember { mutableStateOf<String?>(null) }
    var okMessage by remember { mutableStateOf<String?>(null) }

    IosScreen(
        title = "配对电脑",
        subtitle = "一次性配对，PSK 只通过二维码传递",
        onBack = onBack,
    ) {
        if (state.hasPairing) {
            IosGroupedCard {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(16.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Icon(
                        Icons.Filled.Check,
                        contentDescription = null,
                        tint = palette.success,
                        modifier = Modifier.size(20.dp),
                    )
                    Spacer(Modifier.width(10.dp))
                    Column(modifier = Modifier.weight(1f)) {
                        Text("已配对：${state.hostName}", style = IosType.headline, color = palette.label)
                        Spacer(Modifier.height(2.dp))
                        Text(
                            "HOST_ID ${state.hostId}",
                            style = IosType.mono,
                            color = palette.secondaryLabel,
                        )
                    }
                }
                IosDivider()
                IosRow(
                    title = "忘记这台电脑",
                    subtitle = "清除本机保存的 PSK，需要重新配对",
                    destructive = true,
                    onClick = onForget,
                )
            }
            Spacer(Modifier.height(IosDimens.gap))
        }

        IosCardTitle("方式一 · 扫描电脑上的二维码")
        IosGroupedCard {
            Column(modifier = Modifier.padding(16.dp)) {
                IosPrimaryButton(text = "扫描二维码", onClick = onScanQr)
                Spacer(Modifier.height(10.dp))
                IosHint("电脑端「TC-Tools 蓝牙解锁」窗口会显示配对二维码，对准即可。无相机权限时请用下面的粘贴通道。")
            }
        }

        Spacer(Modifier.height(IosDimens.gap))

        IosCardTitle("方式二 · 手动粘贴配对信息")
        IosGroupedCard {
            Column(modifier = Modifier.padding(16.dp)) {
                OutlinedTextField(
                    value = text,
                    onValueChange = {
                        text = it
                        error = null
                        okMessage = null
                    },
                    modifier = Modifier
                        .fillMaxWidth()
                        .heightIn(min = 120.dp),
                    placeholder = {
                        Text(
                            "{\"v\":1,\"p\":\"tcunlock\",\"id\":\"…\",\"name\":\"…\",\"psk\":\"…\"}",
                            style = IosType.mono,
                            color = palette.secondaryLabel,
                        )
                    },
                    textStyle = IosType.mono,
                    shape = androidx.compose.foundation.shape.RoundedCornerShape(12.dp),
                    colors = OutlinedTextFieldDefaults.colors(
                        focusedBorderColor = palette.accent,
                        unfocusedBorderColor = palette.separator,
                        focusedContainerColor = palette.card,
                        unfocusedContainerColor = palette.card,
                        cursorColor = palette.accent,
                        focusedTextColor = palette.label,
                        unfocusedTextColor = palette.label,
                    ),
                )
                Spacer(Modifier.height(10.dp))
                IosPrimaryButton(
                    text = "确认配对",
                    onClick = {
                        val err = onSubmitText(text)
                        if (err == null) {
                            okMessage = "配对成功"
                            error = null
                            text = ""
                        } else {
                            error = err
                            okMessage = null
                        }
                    },
                    enabled = text.isNotBlank(),
                )
                if (error != null) {
                    Spacer(Modifier.height(10.dp))
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Icon(
                            Icons.Filled.Clear,
                            contentDescription = null,
                            tint = palette.destructive,
                            modifier = Modifier.size(16.dp),
                        )
                        Spacer(Modifier.width(6.dp))
                        Text(error!!, style = IosType.footnote, color = palette.destructive)
                    }
                }
                if (okMessage != null) {
                    Spacer(Modifier.height(10.dp))
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Icon(
                            Icons.Filled.Check,
                            contentDescription = null,
                            tint = palette.success,
                            modifier = Modifier.size(16.dp),
                        )
                        Spacer(Modifier.width(6.dp))
                        Text(okMessage!!, style = IosType.footnote, color = palette.success)
                    }
                }
            }
        }

        Spacer(Modifier.height(IosDimens.gap))

        IosCardTitle("本机身份")
        IosGroupedCard {
            IosKeyValue("PEER_ID", state.peerId ?: "—", mono = true)
            IosDivider()
            IosKeyValue("存储方式", state.storageMode.ifBlank { "—" })
        }

        Spacer(Modifier.height(24.dp))
    }
}
