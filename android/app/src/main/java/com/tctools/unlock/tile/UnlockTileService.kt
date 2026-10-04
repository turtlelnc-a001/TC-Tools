package com.tctools.unlock.tile

import android.app.PendingIntent
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.graphics.drawable.Icon
import android.os.Build
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import android.util.Log
import com.tctools.unlock.MainActivity
import com.tctools.unlock.R

/**
 * 「解锁电脑」快捷设置磁贴（Android 7.0+ / API 24+）。
 *
 * 点击行为（**厂商无关的健壮路径**）：
 *   磁贴点击 → `startActivityAndCollapse` 拉起 [MainActivity]（携带 EXTRA_UNLOCK）
 *   → Activity 位于前台后再弹出 BiometricPrompt。
 *
 * 为什么**不**在磁贴里直接弹指纹：realme UI / ColorOS / MIUI(HyperOS) / EMUI 等国产 ROM 对
 * 「后台启动 Activity」与「后台弹窗」有额外限制，TileService 属于后台上下文，
 * 直接 `BiometricPrompt.authenticate()` 在部分机型上会静默失败或不显示。
 * 先拉起前台 Activity 再验证是这些机型上最稳的做法（也正是本实现采用的路径）。
 *
 * 解锁流程本身完全复用 App 内同一条路径：扫描 → 连接 → IDENT → 读 challenge →
 * **指纹通过后**才计算 PROOF → 写 PROOF → 收 ready → 发 unlock → 显示结果。
 */
class UnlockTileService : TileService() {

    companion object {
        private const val TAG = "TcTile"
        private const val REQUEST_CODE_UNLOCK = 1001

        /** 请求系统重新回调 [onStartListening]，用于刷新磁贴文案。 */
        fun refresh(context: Context) {
            try {
                requestListeningState(
                    context.applicationContext,
                    ComponentName(context.applicationContext, UnlockTileService::class.java),
                )
            } catch (t: Throwable) {
                Log.w(TAG, "requestListeningState 失败：${t.message}")
            }
        }
    }

    override fun onTileAdded() {
        super.onTileAdded()
        Log.i(TAG, "磁贴已添加到控制中心")
        render()
    }

    override fun onStartListening() {
        super.onStartListening()
        render()
    }

    override fun onTileRemoved() {
        super.onTileRemoved()
        Log.i(TAG, "磁贴已从控制中心移除")
    }

    override fun onClick() {
        super.onClick()
        val snapshot = UnlockTileState.ensureLoaded(this)
        Log.i(TAG, "磁贴点击：paired=${snapshot.paired} phase=${snapshot.phase}")
        // 正在进行中：只把界面拉到前台，避免重复发起
        val withUnlock = snapshot.paired && snapshot.phase != UnlockTileState.Phase.UNLOCKING
        launchApp(withUnlock = withUnlock)
    }

    private fun launchApp(withUnlock: Boolean) {
        val intent = Intent(this, MainActivity::class.java).apply {
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
            putExtra(MainActivity.EXTRA_FROM_TILE, true)
            if (withUnlock) putExtra(MainActivity.EXTRA_UNLOCK, true)
        }
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
                // Android 14+：必须用 PendingIntent（旧的 Intent 重载已废弃）
                val pending = PendingIntent.getActivity(
                    this,
                    REQUEST_CODE_UNLOCK,
                    intent,
                    PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
                )
                startActivityAndCollapse(pending)
            } else {
                @Suppress("DEPRECATION")
                startActivityAndCollapse(intent)
            }
        } catch (t: Throwable) {
            // 极少数 ROM 在后台拉了 Activity 被拒时会抛异常：记录即可，用户可手动打开 App
            Log.e(TAG, "startActivityAndCollapse 失败（可能被系统后台启动限制拦截）", t)
        }
    }

    private fun render() {
        val tile = qsTile ?: return
        val snapshot = UnlockTileState.ensureLoaded(this)

        tile.label = getString(R.string.tile_label)
        tile.stateDescription = snapshot.shortStatus()
        tile.contentDescription = snapshot.fullStatus()
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            tile.subtitle = snapshot.subtitle()
        }
        tile.state = if (snapshot.phase == UnlockTileState.Phase.UNLOCKING) {
            Tile.STATE_ACTIVE
        } else {
            Tile.STATE_INACTIVE
        }
        try {
            tile.icon = Icon.createWithResource(this, R.drawable.ic_qs_unlock)
        } catch (t: Throwable) {
            Log.w(TAG, "设置磁贴图标失败：${t.message}")
        }
        try {
            tile.updateTile()
        } catch (t: Throwable) {
            Log.w(TAG, "updateTile 失败：${t.message}")
        }
    }
}
