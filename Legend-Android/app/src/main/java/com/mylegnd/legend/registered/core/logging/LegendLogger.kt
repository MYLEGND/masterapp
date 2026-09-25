package com.mylegnd.legend.registered.core.logging

import android.util.Log
import com.microsoft.identity.client.exception.MsalException
import com.mylegnd.legend.registered.BuildConfig

/**
 * One deliberately narrow diagnostics authority. It never accepts tokens, bodies, URLs with
 * query parameters, or server payloads. Logcat remains debug-only; runtime capture emits
 * a fixed authentication-failure observation through the shared diagnostic queue.
 */
object LegendLogger {
    private const val tag = "Legend"

    fun authenticationFailure(phase: String, throwable: Throwable) {
        com.mylegnd.legend.registered.core.diagnostics.RuntimeDiagnostics.recordAuthenticationFailure()
        if (!BuildConfig.DEBUG) return
        val category = (throwable as? MsalException)?.errorCode ?: throwable::class.java.simpleName
        Log.w(tag, "auth.$phase failed ($category)")
    }
}
