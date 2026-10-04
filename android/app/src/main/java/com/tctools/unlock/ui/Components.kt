package com.tctools.unlock.ui

import androidx.compose.animation.animateColorAsState
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.KeyboardArrowRight
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlin.math.max
import kotlin.math.min

// ---------------------------------------------------------------- 页面骨架

/**
 * iOS 式页面骨架：grouped 背景 + 大标题(Large Title) + 可滚动内容 + 底部主按钮区。
 * 已处理系统栏 insets（配合 Activity 的 edge-to-edge）。
 */
@Composable
fun IosScreen(
    title: String,
    subtitle: String? = null,
    onBack: (() -> Unit)? = null,
    trailing: @Composable (() -> Unit)? = null,
    bottomBar: @Composable (() -> Unit)? = null,
    content: @Composable ColumnScope.() -> Unit,
) {
    val palette = LocalIosPalette.current
    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(palette.groupedBg)
            .statusBarsPadding()
            .imePadding(),
    ) {
        Column(modifier = Modifier.padding(horizontal = IosDimens.screenPadding)) {
            Spacer(Modifier.height(12.dp))
            Row(verticalAlignment = Alignment.CenterVertically) {
                if (onBack != null) {
                    Text(
                        text = "‹ 返回",
                        style = IosType.body,
                        color = palette.accent,
                        modifier = Modifier
                            .clip(RoundedCornerShape(8.dp))
                            .clickable { onBack() }
                            .padding(vertical = 6.dp, horizontal = 2.dp),
                    )
                    Spacer(Modifier.width(12.dp))
                }
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                        text = title,
                        style = IosType.largeTitle,
                        color = palette.label,
                    )
                    if (subtitle != null) {
                        Spacer(Modifier.height(2.dp))
                        Text(text = subtitle, style = IosType.subhead, color = palette.secondaryLabel)
                    }
                }
                trailing?.invoke()
            }
            Spacer(Modifier.height(18.dp))
        }
        Column(
            modifier = Modifier
                .weight(1f)
                .verticalScroll(rememberScrollState())
                .padding(horizontal = IosDimens.screenPadding),
            content = content,
        )
        if (bottomBar != null) {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .background(palette.groupedBg)
                    .padding(horizontal = IosDimens.screenPadding)
                    .padding(top = 8.dp)
                    .navigationBarsPadding()
                    .padding(bottom = 12.dp),
            ) {
                bottomBar()
            }
        } else {
            Spacer(Modifier.navigationBarsPadding().height(12.dp))
        }
    }
}

// ---------------------------------------------------------------- 分组卡片

/** iOS inset grouped 卡片：白/深灰底、14dp 圆角、行内缩进分割线。 */
@Composable
fun IosGroupedCard(
    modifier: Modifier = Modifier,
    content: @Composable ColumnScope.() -> Unit,
) {
    val palette = LocalIosPalette.current
    Column(
        modifier = modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(IosDimens.cardRadius))
            .background(palette.card),
        content = content,
    )
}

@Composable
fun IosCardTitle(text: String) {
    val palette = LocalIosPalette.current
    Text(
        text = text.uppercase(),
        style = IosType.footnote.copy(fontSize = 13.sp, letterSpacing = 0.4.sp),
        color = palette.secondaryLabel,
        modifier = Modifier.padding(start = 16.dp, bottom = 6.dp, top = 2.dp),
    )
}

@Composable
fun IosDivider(startIndent: Dp = 16.dp) {
    val palette = LocalIosPalette.current
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .padding(start = startIndent)
            .height(0.7.dp)
            .background(palette.separator),
    )
}

/** 分组列表行（可选图标、右侧值、箭头、点击、红字）。 */
@Composable
fun IosRow(
    title: String,
    subtitle: String? = null,
    value: String? = null,
    icon: (@Composable () -> Unit)? = null,
    destructive: Boolean = false,
    showChevron: Boolean = false,
    onClick: (() -> Unit)? = null,
    trailing: @Composable (() -> Unit)? = null,
) {
    val palette = LocalIosPalette.current
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .heightIn(min = IosDimens.rowHeight)
            .let { if (onClick != null) it.clickable { onClick() } else it }
            .padding(horizontal = 16.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (icon != null) {
            Box(
                modifier = Modifier
                    .size(29.dp)
                    .clip(RoundedCornerShape(7.dp))
                    .background(if (destructive) palette.destructive else palette.accent),
                contentAlignment = Alignment.Center,
            ) { icon() }
            Spacer(Modifier.width(12.dp))
        }
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = title,
                style = IosType.body,
                color = if (destructive) palette.destructive else palette.label,
            )
            if (subtitle != null) {
                Spacer(Modifier.height(2.dp))
                Text(text = subtitle, style = IosType.footnote, color = palette.secondaryLabel)
            }
        }
        if (value != null) {
            Spacer(Modifier.width(8.dp))
            Text(text = value, style = IosType.body, color = palette.secondaryLabel)
        }
        trailing?.let {
            Spacer(Modifier.width(8.dp))
            it()
        }
        if (showChevron) {
            Spacer(Modifier.width(6.dp))
            Icon(
                imageVector = Icons.Filled.KeyboardArrowRight,
                contentDescription = null,
                tint = palette.secondaryLabel,
                modifier = Modifier.size(20.dp),
            )
        }
    }
}

/** iOS 风格开关行。 */
@Composable
fun IosSwitchRow(
    title: String,
    subtitle: String? = null,
    checked: Boolean,
    onCheckedChange: (Boolean) -> Unit,
) {
    val palette = LocalIosPalette.current
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .heightIn(min = IosDimens.rowHeight)
            .clickable { onCheckedChange(!checked) }
            .padding(horizontal = 16.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(modifier = Modifier.weight(1f)) {
            Text(title, style = IosType.body, color = palette.label)
            if (subtitle != null) {
                Spacer(Modifier.height(2.dp))
                Text(subtitle, style = IosType.footnote, color = palette.secondaryLabel)
            }
        }
        Spacer(Modifier.width(12.dp))
        Switch(
            checked = checked,
            onCheckedChange = onCheckedChange,
            colors = SwitchDefaults.colors(
                checkedThumbColor = Color.White,
                checkedTrackColor = IosColors.Green,
                uncheckedThumbColor = Color.White,
                uncheckedTrackColor = palette.fill,
                uncheckedBorderColor = Color.Transparent,
            ),
        )
    }
}

// ---------------------------------------------------------------- 按钮

/** iOS 底部大圆角主按钮（56dp 高、14dp 圆角、强调色填充）。 */
@Composable
fun IosPrimaryButton(
    text: String,
    onClick: () -> Unit,
    enabled: Boolean = true,
    loading: Boolean = false,
    tint: Color? = null,
    modifier: Modifier = Modifier,
) {
    val palette = LocalIosPalette.current
    val bg = when {
        !enabled -> palette.fill
        tint != null -> tint
        else -> palette.accent
    }
    val fg = if (!enabled) palette.secondaryLabel else Color.White
    Box(
        modifier = modifier
            .fillMaxWidth()
            .height(56.dp)
            .clip(RoundedCornerShape(IosDimens.buttonRadius))
            .background(bg)
            .clickable(enabled = enabled && !loading) { onClick() },
        contentAlignment = Alignment.Center,
    ) {
        if (loading) {
            CircularProgressIndicator(
                color = fg,
                strokeWidth = 2.5.dp,
                modifier = Modifier.size(22.dp),
            )
        } else {
            Text(text = text, style = IosType.button, color = fg)
        }
    }
}

/** iOS 次级按钮：白底描边或纯文字。 */
@Composable
fun IosSecondaryButton(
    text: String,
    onClick: () -> Unit,
    enabled: Boolean = true,
    modifier: Modifier = Modifier,
) {
    val palette = LocalIosPalette.current
    Box(
        modifier = modifier
            .fillMaxWidth()
            .height(50.dp)
            .clip(RoundedCornerShape(IosDimens.buttonRadius))
            .background(palette.card)
            .border(0.8.dp, palette.separator, RoundedCornerShape(IosDimens.buttonRadius))
            .clickable(enabled = enabled) { onClick() },
        contentAlignment = Alignment.Center,
    ) {
        Text(text = text, style = IosType.button, color = if (enabled) palette.accent else palette.secondaryLabel)
    }
}

/** 顶部圆形图标按钮（设置入口）。 */
@Composable
fun IosCircleIconButton(onClick: () -> Unit, content: @Composable () -> Unit) {
    val palette = LocalIosPalette.current
    Box(
        modifier = Modifier
            .size(36.dp)
            .clip(CircleShape)
            .background(palette.card)
            .clickable { onClick() },
        contentAlignment = Alignment.Center,
    ) { content() }
}

// ---------------------------------------------------------------- 指标组件

/** 信号强度柱（4 格）。 */
@Composable
fun SignalBars(rssi: Int?, modifier: Modifier = Modifier) {
    val palette = LocalIosPalette.current
    val level = when {
        rssi == null -> 0
        rssi >= -60 -> 4
        rssi >= -70 -> 3
        rssi >= -80 -> 2
        else -> 1
    }
    Row(modifier = modifier, verticalAlignment = Alignment.Bottom) {
        for (i in 1..4) {
            val active = i <= level
            Box(
                modifier = Modifier
                    .padding(end = 3.dp)
                    .width(4.dp)
                    .height((5 + i * 4).dp)
                    .clip(RoundedCornerShape(1.5.dp))
                    .background(if (active) palette.accent else palette.fill),
            )
        }
    }
}

/** 状态圆点 + 文案。 */
@Composable
fun StatusPill(text: String, color: Color) {
    val palette = LocalIosPalette.current
    Row(
        modifier = Modifier
            .clip(RoundedCornerShape(20.dp))
            .background(palette.fill)
            .padding(horizontal = 10.dp, vertical = 5.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(
            modifier = Modifier
                .size(8.dp)
                .clip(CircleShape)
                .background(color),
        )
        Spacer(Modifier.width(6.dp))
        Text(text = text, style = IosType.footnote, color = palette.label)
    }
}

/** 指纹图标（Canvas 绘制同心弧，避免引入 material-icons-extended）。 */
@Composable
fun FingerprintGlyph(
    color: Color,
    modifier: Modifier = Modifier,
    strokeWidth: Dp = 3.dp,
) {
    Canvas(modifier = modifier) {
        val w = size.width
        val h = size.height
        val cx = w / 2f
        val cy = h * 0.74f
        val unit = min(w, h)
        val stroke = Stroke(width = strokeWidth.toPx(), cap = StrokeCap.Round)
        val radii = listOf(0.13f, 0.25f, 0.37f, 0.49f)
        radii.forEachIndexed { index, r ->
            val radius = unit * r
            drawArc(
                color = color,
                startAngle = 200f,
                sweepAngle = 140f,
                useCenter = false,
                topLeft = Offset(cx - radius, cy - radius),
                size = Size(radius * 2, radius * 2),
                style = stroke,
            )
            if (index == radii.lastIndex) {
                // 外侧两条短弧，模拟指纹纹路
                drawArc(
                    color = color,
                    startAngle = 165f,
                    sweepAngle = 30f,
                    useCenter = false,
                    topLeft = Offset(cx - radius * 1.18f, cy - radius * 1.18f),
                    size = Size(radius * 2.36f, radius * 2.36f),
                    style = stroke,
                )
                drawArc(
                    color = color,
                    startAngle = 345f,
                    sweepAngle = 30f,
                    useCenter = false,
                    topLeft = Offset(cx - radius * 1.18f, cy - radius * 1.18f),
                    size = Size(radius * 2.36f, radius * 2.36f),
                    style = stroke,
                )
            }
        }
        // 核心竖纹
        drawLine(
            color = color,
            start = Offset(cx, cy - unit * 0.02f),
            end = Offset(cx, cy + unit * 0.16f),
            strokeWidth = strokeWidth.toPx(),
            cap = StrokeCap.Round,
        )
    }
}

/** 大号信息卡（首页连接状态）。 */
@Composable
fun IosInfoCard(content: @Composable ColumnScope.() -> Unit) {
    IosGroupedCard {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(IosDimens.cardPadding),
            content = content,
        )
    }
}

/** 空态/提示文本。 */
@Composable
fun IosHint(text: String, warning: Boolean = false) {
    val palette = LocalIosPalette.current
    Text(
        text = text,
        style = IosType.footnote,
        color = if (warning) palette.warning else palette.secondaryLabel,
        modifier = Modifier.padding(horizontal = 4.dp, vertical = 6.dp),
    )
}

/** 结果横幅（成功/失败）。 */
@Composable
fun IosBanner(text: String, ok: Boolean, onDismiss: (() -> Unit)? = null) {
    val palette = LocalIosPalette.current
    val bg = if (ok) palette.success.copy(alpha = 0.14f) else palette.destructive.copy(alpha = 0.14f)
    val fg = if (ok) palette.success else palette.destructive
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(12.dp))
            .background(bg)
            .clickable(enabled = onDismiss != null) { onDismiss?.invoke() }
            .padding(horizontal = 14.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(
            imageVector = if (ok) Icons.Filled.Check else Icons.Filled.Close,
            contentDescription = null,
            tint = fg,
            modifier = Modifier.size(18.dp),
        )
        Spacer(Modifier.width(8.dp))
        Text(
            text = text,
            style = IosType.subhead,
            color = palette.label,
            modifier = Modifier.weight(1f),
        )
    }
}

/** 键值行（用于「关于」页）。 */
@Composable
fun IosKeyValue(key: String, value: String, mono: Boolean = false, selectable: Boolean = false) {
    val palette = LocalIosPalette.current
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 16.dp, vertical = 9.dp),
        verticalAlignment = Alignment.Top,
    ) {
        Text(key, style = IosType.subhead, color = palette.secondaryLabel, modifier = Modifier.weight(0.42f))
        Spacer(Modifier.width(10.dp))
        Text(
            text = value,
            style = if (mono) IosType.mono else IosType.subhead,
            color = palette.label,
            textAlign = TextAlign.End,
            fontWeight = if (mono) FontWeight.Normal else FontWeight.Medium,
            modifier = Modifier.weight(0.58f),
        )
    }
}

/** 简易确认弹窗（iOS Alert 风格）。 */
@Composable
fun IosAlertDialog(
    title: String,
    message: String,
    confirmText: String = "确定",
    dismissText: String = "取消",
    destructive: Boolean = false,
    onConfirm: () -> Unit,
    onDismiss: () -> Unit,
) {
    val palette = LocalIosPalette.current
    androidx.compose.ui.window.Dialog(onDismissRequest = onDismiss) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .clip(RoundedCornerShape(16.dp))
                .background(palette.card)
                .padding(top = 20.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Text(title, style = IosType.headline, color = palette.label, modifier = Modifier.padding(horizontal = 20.dp))
            Spacer(Modifier.height(8.dp))
            Text(
                message,
                style = IosType.footnote,
                color = palette.secondaryLabel,
                textAlign = TextAlign.Center,
                modifier = Modifier.padding(horizontal = 20.dp),
            )
            Spacer(Modifier.height(18.dp))
            IosDivider(startIndent = 0.dp)
            Row(modifier = Modifier.height(46.dp)) {
                Box(
                    modifier = Modifier
                        .weight(1f)
                        .fillMaxSize()
                        .clickable { onDismiss() },
                    contentAlignment = Alignment.Center,
                ) { Text(dismissText, style = IosType.body, color = palette.accent) }
                Box(
                    modifier = Modifier
                        .width(0.7.dp)
                        .fillMaxSize()
                        .background(palette.separator),
                )
                Box(
                    modifier = Modifier
                        .weight(1f)
                        .fillMaxSize()
                        .clickable { onConfirm() },
                    contentAlignment = Alignment.Center,
                ) {
                    Text(
                        confirmText,
                        style = IosType.body.copy(fontWeight = FontWeight.SemiBold),
                        color = if (destructive) palette.destructive else palette.accent,
                    )
                }
            }
        }
    }
}

/** 让动画颜色在深浅色切换时平滑过渡（细节润色）。 */
@Composable
fun animatedAccent(target: Color): Color =
    animateColorAsState(targetValue = target, label = "accent").value
