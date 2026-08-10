package com.mylegnd.legend.registered.core.media

import android.content.Context
import android.net.Uri
import com.mylegnd.legend.registered.core.model.MessageAttachment
import com.mylegnd.legend.registered.core.network.LegendApiClient
import com.mylegnd.legend.registered.core.network.legendBody
import okhttp3.MediaType.Companion.toMediaTypeOrNull
import okhttp3.MultipartBody

/** Streams Android-selected files into the existing 10 MiB server attachment lifecycle. */
class MessagingAttachmentUploader(private val client: LegendApiClient) {
    suspend fun upload(
        context: Context,
        role: String,
        conversationId: String,
        messageId: String,
        uri: Uri,
    ): MessageAttachment {
        val resolver = context.contentResolver
        val length = resolver.legendContentLength(uri)
        require(length < 0L || length <= MAX_ATTACHMENT_BYTES) {
            "Choose an attachment smaller than 10 MB."
        }
        val file = MultipartBody.Part.createFormData(
            name = "file",
            filename = resolver.legendDisplayName(uri),
            body = AndroidUriRequestBody(resolver, uri, resolver.getType(uri)?.toMediaTypeOrNull()),
        )
        return client.api.uploadMessageAttachment(role, conversationId, messageId, file).legendBody()
    }

    private companion object {
        const val MAX_ATTACHMENT_BYTES = 10L * 1024L * 1024L
    }
}
