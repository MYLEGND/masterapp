package com.mylegnd.legend.registered.core.media

import android.content.ContentResolver
import android.content.Context
import android.net.Uri
import android.provider.OpenableColumns
import com.mylegnd.legend.registered.core.model.SocialPost
import com.mylegnd.legend.registered.core.network.LegendApiClient
import com.mylegnd.legend.registered.core.network.legendBody
import okhttp3.MediaType.Companion.toMediaTypeOrNull
import okhttp3.MultipartBody
import okhttp3.RequestBody
import okhttp3.RequestBody.Companion.toRequestBody
import okio.BufferedSink
import okio.source

/** Streams selected Android media directly to the existing server upload contract. */
class SocialMediaUploader(private val client: LegendApiClient) {
    suspend fun upload(context: Context, role: String, uris: List<Uri>, contentType: String, body: String, audience: String): SocialPost {
        val resolver = context.contentResolver
        val files = uris.map { uri -> MultipartBody.Part.createFormData("files", resolver.displayName(uri), UriRequestBody(resolver, uri, resolver.getType(uri)?.toMediaTypeOrNull())) }
        return client.api.createMediaPost(role, files, contentType.toFormBody(), body.toFormBody(), audience.toFormBody(), "true".toFormBody()).legendBody()
    }
    private fun String.toFormBody() = toRequestBody("text/plain".toMediaTypeOrNull())
}
private class UriRequestBody(private val resolver: ContentResolver, private val uri: Uri, private val type: okhttp3.MediaType?) : RequestBody() {
    override fun contentType() = type
    override fun contentLength(): Long = resolver.openAssetFileDescriptor(uri, "r")?.use { it.length } ?: -1L
    override fun writeTo(sink: BufferedSink) { resolver.openInputStream(uri)?.source()?.use { source -> sink.writeAll(source) } ?: error("Selected media is unavailable.") }
}
private fun ContentResolver.displayName(uri: Uri): String = query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { cursor -> if (cursor.moveToFirst()) cursor.getString(0) else null } ?: "legend-media"
