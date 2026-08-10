package com.mylegnd.legend.registered.core.media

import android.content.Context
import android.net.Uri
import com.mylegnd.legend.registered.core.model.SocialMediaPublishOptions
import com.mylegnd.legend.registered.core.model.SocialPost
import com.mylegnd.legend.registered.core.network.LegendApiClient
import com.mylegnd.legend.registered.core.network.legendBody
import okhttp3.MediaType.Companion.toMediaTypeOrNull
import okhttp3.MultipartBody
import okhttp3.RequestBody
import okhttp3.RequestBody.Companion.toRequestBody

/** Streams selected Android media directly to the existing server upload contract. */
class SocialMediaUploader(private val client: LegendApiClient) {
    suspend fun upload(context: Context, role: String, uris: List<Uri>, options: SocialMediaPublishOptions, previewUri: Uri? = null): SocialPost {
        val resolver = context.contentResolver
        val files = uris.map { uri -> MultipartBody.Part.createFormData("files", resolver.legendDisplayName(uri), AndroidUriRequestBody(resolver, uri, resolver.getType(uri)?.toMediaTypeOrNull())) }
        val preview = previewUri?.let { uri ->
            MultipartBody.Part.createFormData("preview", resolver.legendDisplayName(uri), AndroidUriRequestBody(resolver, uri, resolver.getType(uri)?.toMediaTypeOrNull()))
        }
        val music = options.music
        return client.api.createMediaPost(
            participantType = role,
            files = files,
            preview = preview,
            contentType = options.contentType.toFormBody(),
            body = options.body.toFormBody(),
            audience = options.audience.toFormBody(),
            location = options.location?.takeIf(String::isNotBlank)?.toFormBody(),
            commentsEnabled = options.commentsEnabled.toString().toFormBody(),
            accessibilityText = options.accessibilityText?.takeIf(String::isNotBlank)?.toFormBody(),
            musicProviderId = music?.providerId?.toFormBody(),
            musicTrackId = music?.providerTrackId?.toFormBody(),
            musicTrimStartSeconds = music?.trimStartSeconds?.toString()?.toFormBody(),
            musicTrimEndSeconds = music?.trimEndSeconds?.toString()?.toFormBody(),
            musicVolume = music?.musicVolume?.toString()?.toFormBody(),
            originalAudioVolume = music?.originalAudioVolume?.toString()?.toFormBody(),
        ).legendBody()
    }
    private fun String.toFormBody() = toRequestBody("text/plain".toMediaTypeOrNull())
}
