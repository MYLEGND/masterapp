package com.mylegnd.legend.registered.core.media

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import android.util.Base64
import java.io.ByteArrayOutputStream

/** Converts an Android-picked profile image to the existing account avatar payload. */
object ProfileAvatarPreparer {
    private const val PROFILE_MAXIMUM_BYTES = 3 * 1024 * 1024
    private const val PROFILE_MAXIMUM_DIMENSION = 1_280

    fun base64Jpeg(
        context: Context,
        uri: Uri,
        maximumBytes: Int = PROFILE_MAXIMUM_BYTES,
        maximumDimension: Int = PROFILE_MAXIMUM_DIMENSION,
        label: String = "profile picture",
    ): String {
        val source = context.contentResolver.openInputStream(uri)?.use(BitmapFactory::decodeStream)
            ?: throw IllegalArgumentException("Selected $label is unavailable.")
        val image = source.scaleToFit(maximumDimension)
        val bytes = ByteArrayOutputStream().use { output ->
            var quality = 90
            do {
                output.reset()
                image.compress(Bitmap.CompressFormat.JPEG, quality, output)
                quality -= 10
            } while (output.size() > maximumBytes && quality >= 40)
            output.toByteArray()
        }
        require(bytes.size <= maximumBytes) { "Choose a smaller $label and try again." }
        return Base64.encodeToString(bytes, Base64.NO_WRAP)
    }

    private fun Bitmap.scaleToFit(maxDimension: Int): Bitmap {
        val longest = maxOf(width, height)
        if (longest <= maxDimension) return this
        val ratio = maxDimension.toFloat() / longest
        return Bitmap.createScaledBitmap(this, (width * ratio).toInt(), (height * ratio).toInt(), true)
    }
}
