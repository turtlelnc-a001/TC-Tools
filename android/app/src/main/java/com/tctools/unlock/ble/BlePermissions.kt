package com.tctools.unlock.ble

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.content.ContextCompat

/** BLE 运行时权限（Android 12 前后不同）。 */
object BlePermissions {

    val required: Array<String> = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
        arrayOf(
            Manifest.permission.BLUETOOTH_SCAN,
            Manifest.permission.BLUETOOTH_CONNECT,
        )
    } else {
        arrayOf(
            Manifest.permission.ACCESS_FINE_LOCATION,
        )
    }

    fun granted(context: Context): Boolean = required.all {
        ContextCompat.checkSelfPermission(context, it) == PackageManager.PERMISSION_GRANTED
    }

    fun missing(context: Context): List<String> = required.filter {
        ContextCompat.checkSelfPermission(context, it) != PackageManager.PERMISSION_GRANTED
    }

    /** 面向用户的中文说明（权限被永久拒绝时提示去系统设置）。 */
    val rationale: String =
        "需要「附近的设备」权限才能扫描并连接电脑的蓝牙解锁服务（本应用不会用蓝牙定位）。"
}
