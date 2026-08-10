package com.mylegnd.legend.registered.core.logging

import android.util.Log
import com.microsoft.identity.client.exception.MsalException
import com.mylegnd.legend.registered.BuildConfig

/**
 * One deliberately narrow diagnostics authority. It never accepts tokens, bodies, URLs with
 * query parameters, or server payloads. Release builds emit no diagnostic details.
 */
object LegendLogger {
    private const val tag = "Legend"

    fun authenticationFailure(phase: String, throwable: Throwable) {
        if (!BuildConfig.DEBUG) return
        val category = (throwable as? MsalException)?.errorCode ?: throwable::class.java.simpleName
        Log.w(tag, "auth.$phase failed ($category)")
    }
}
