package com.tctools.unlock.data

import android.content.Context
import android.content.SharedPreferences

/** 非机密的界面偏好（上次解锁时间、开关状态等）。PSK 绝不走这里。 */
class AppPrefs(context: Context) {

    private val prefs: SharedPreferences =
        context.applicationContext.getSharedPreferences("tc_unlock_prefs", Context.MODE_PRIVATE)

    var lastUnlockAt: Long
        get() = prefs.getLong(KEY_LAST_UNLOCK, 0L)
        set(value) {
            prefs.edit().putLong(KEY_LAST_UNLOCK, value).apply()
        }

    var lastResult: String
        get() = prefs.getString(KEY_LAST_RESULT, "") ?: ""
        set(value) {
            prefs.edit().putString(KEY_LAST_RESULT, value).apply()
        }

    /** 解锁成功后自动断开 BLE（省电；默认开）。 */
    var autoDisconnect: Boolean
        get() = prefs.getBoolean(KEY_AUTO_DISCONNECT, true)
        set(value) {
            prefs.edit().putBoolean(KEY_AUTO_DISCONNECT, value).apply()
        }

    /** 连接时自动读取 challenge 并弹出指纹（默认开：单击大按钮即可解锁）。 */
    var autoPrompt: Boolean
        get() = prefs.getBoolean(KEY_AUTO_PROMPT, true)
        set(value) {
            prefs.edit().putBoolean(KEY_AUTO_PROMPT, value).apply()
        }

    private companion object {
        const val KEY_LAST_UNLOCK = "last_unlock_at"
        const val KEY_LAST_RESULT = "last_result"
        const val KEY_AUTO_DISCONNECT = "auto_disconnect"
        const val KEY_AUTO_PROMPT = "auto_prompt"
    }
}
