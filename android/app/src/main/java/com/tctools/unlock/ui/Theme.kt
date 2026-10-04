package com.tctools.unlock.ui

import android.os.Build
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

/**
 * iOS 风格色板与字体。
 *
 * - 强调色：iOS 系统蓝 #0A84FF
 * - 浅色背景：#F2F2F7（iOS grouped background）/ 卡片 #FFFFFF
 * - 深色背景：#000000 / 卡片 #1C1C1E（跟随系统）
 * - 字体：优先使用系统 SF/思源黑体（Android 自带 Roboto/Noto Sans CJK），按 iOS 的字号/字重层级排布
 */
object IosColors {
    val Blue = Color(0xFF0A84FF)
    val BlueDark = Color(0xFF0A84FF)
    val Green = Color(0xFF34C759)
    val Orange = Color(0xFFFF9F0A)
    val Red = Color(0xFFFF3B30)
    val Gray = Color(0xFF8E8E93)
    val GrayLight = Color(0xFFC7C7CC)

    val LightGroupedBg = Color(0xFFF2F2F7)
    val LightCard = Color(0xFFFFFFFF)
    val LightLabel = Color(0xFF000000)
    val LightSecondary = Color(0x993C3C43)
    val LightSeparator = Color(0x293C3C43)
    val LightFill = Color(0x1F767680)

    val DarkGroupedBg = Color(0xFF000000)
    val DarkCard = Color(0xFF1C1C1E)
    val DarkLabel = Color(0xFFFFFFFF)
    val DarkSecondary = Color(0x99EBEBF5)
    val DarkSeparator = Color(0x4A545458)
    val DarkFill = Color(0x1F767680)
}

/** 语义色，随深浅色切换。 */
data class IosPalette(
    val accent: Color,
    val groupedBg: Color,
    val card: Color,
    val label: Color,
    val secondaryLabel: Color,
    val separator: Color,
    val fill: Color,
    val destructive: Color,
    val success: Color,
    val warning: Color,
    val isDark: Boolean,
)

val LocalIosPalette = androidx.compose.runtime.staticCompositionLocalOf {
    IosPalette(
        accent = IosColors.Blue,
        groupedBg = IosColors.LightGroupedBg,
        card = IosColors.LightCard,
        label = IosColors.LightLabel,
        secondaryLabel = IosColors.LightSecondary,
        separator = IosColors.LightSeparator,
        fill = IosColors.LightFill,
        destructive = IosColors.Red,
        success = IosColors.Green,
        warning = IosColors.Orange,
        isDark = false,
    )
}

@Composable
fun iosPalette(dark: Boolean = isSystemInDarkTheme()): IosPalette = if (dark) {
    IosPalette(
        accent = IosColors.BlueDark,
        groupedBg = IosColors.DarkGroupedBg,
        card = IosColors.DarkCard,
        label = IosColors.DarkLabel,
        secondaryLabel = IosColors.DarkSecondary,
        separator = IosColors.DarkSeparator,
        fill = IosColors.DarkFill,
        destructive = IosColors.Red,
        success = IosColors.Green,
        warning = IosColors.Orange,
        isDark = true,
    )
} else {
    IosPalette(
        accent = IosColors.Blue,
        groupedBg = IosColors.LightGroupedBg,
        card = IosColors.LightCard,
        label = IosColors.LightLabel,
        secondaryLabel = IosColors.LightSecondary,
        separator = IosColors.LightSeparator,
        fill = IosColors.LightFill,
        destructive = IosColors.Red,
        success = IosColors.Green,
        warning = IosColors.Orange,
        isDark = false,
    )
}

/** iOS 字体层级（Large Title 34 / Title2 22 / Headline 17Semibold / Body 17 / Subhead 15 / Footnote 13）。 */
object IosType {
    val largeTitle = TextStyle(fontSize = 34.sp, lineHeight = 41.sp, fontWeight = FontWeight.Bold)
    val title = TextStyle(fontSize = 22.sp, lineHeight = 28.sp, fontWeight = FontWeight.SemiBold)
    val headline = TextStyle(fontSize = 17.sp, lineHeight = 22.sp, fontWeight = FontWeight.SemiBold)
    val body = TextStyle(fontSize = 17.sp, lineHeight = 22.sp, fontWeight = FontWeight.Normal)
    val callout = TextStyle(fontSize = 16.sp, lineHeight = 21.sp, fontWeight = FontWeight.Normal)
    val subhead = TextStyle(fontSize = 15.sp, lineHeight = 20.sp, fontWeight = FontWeight.Normal)
    val footnote = TextStyle(fontSize = 13.sp, lineHeight = 18.sp, fontWeight = FontWeight.Normal)
    val caption = TextStyle(fontSize = 12.sp, lineHeight = 16.sp, fontWeight = FontWeight.Normal)
    val button = TextStyle(fontSize = 17.sp, lineHeight = 22.sp, fontWeight = FontWeight.SemiBold)
    val mono = TextStyle(fontSize = 12.sp, lineHeight = 16.sp, fontFamily = FontFamily.Monospace)
}

@Composable
fun TcUnlockTheme(darkTheme: Boolean = isSystemInDarkTheme(), content: @Composable () -> Unit) {
    val palette = iosPalette(darkTheme)
    val scheme = if (darkTheme) {
        darkColorScheme(
            primary = palette.accent,
            onPrimary = Color.White,
            background = palette.groupedBg,
            onBackground = palette.label,
            surface = palette.card,
            onSurface = palette.label,
            error = palette.destructive,
        )
    } else {
        lightColorScheme(
            primary = palette.accent,
            onPrimary = Color.White,
            background = palette.groupedBg,
            onBackground = palette.label,
            surface = palette.card,
            onSurface = palette.label,
            error = palette.destructive,
        )
    }
    androidx.compose.runtime.CompositionLocalProvider(LocalIosPalette provides palette) {
        MaterialTheme(
            colorScheme = scheme,
            typography = Typography(
                bodyLarge = IosType.body,
                bodyMedium = IosType.subhead,
                labelLarge = IosType.button,
                titleLarge = IosType.title,
                headlineSmall = IosType.headline,
            ),
            content = content,
        )
    }
}

/** 供 UI 显示 Android 版本信息。 */
val androidRelease: String get() = "Android ${Build.VERSION.RELEASE} (API ${Build.VERSION.SDK_INT})"

/** 圆角/间距常量（iOS inset grouped 规范）。 */
object IosDimens {
    val cardRadius = 14.dp
    val buttonRadius = 14.dp
    val screenPadding = 20.dp
    val cardPadding = 16.dp
    val rowHeight = 52.dp
    val gap = 16.dp
}
