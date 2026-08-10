package com.mylegnd.legend.registered.core.media

import android.content.ContentResolver
import android.net.Uri
import android.provider.OpenableColumns
import okhttp3.MediaType
import okhttp3.RequestBody
import okio.BufferedSink
import okio.source

/**
 * One streaming Android URI body used by the server-owned social and messaging
 * upload contracts. It never copies a selected file into a client media store.
 */
internal class AndroidUriRequestBody(
    private val resolver: ContentResolver,
    private val uri: Uri,
    private val type: MediaType?,
) : RequestBody() {
    override fun contentType() = type
    override fun contentLength(): Long = resolver.openAssetFileDescriptor(uri, "r")?.use { it.length } ?: -1L
    override fun writeTo(sink: BufferedSink) {
        resolver.openInputStream(uri)?.source()?.use(sink::writeAll)
            ?: error("Selected media is unavailable.")
    }
}

internal fun ContentResolver.legendDisplayName(uri: Uri): String =
    query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { cursor ->
        if (cursor.moveToFirst()) cursor.getString(0) else null
    } ?: "legend-upload"

internal fun ContentResolver.legendContentLength(uri: Uri): Long =
    openAssetFileDescriptor(uri, "r")?.use { it.length } ?: -1L
