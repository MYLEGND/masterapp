package com.mylegnd.legend.registered.ui

import android.graphics.Bitmap
import android.graphics.Canvas as AndroidCanvas
import android.graphics.BitmapFactory
import android.graphics.Matrix
import android.media.ExifInterface
import android.content.Context
import android.graphics.Paint
import android.graphics.ColorMatrix
import android.graphics.ColorMatrixColorFilter
import android.net.Uri
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectTransformGestures
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.drawscope.drawIntoCanvas
import androidx.compose.ui.graphics.nativeCanvas
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.stateDescription
import com.mylegnd.legend.registered.core.design.*
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import kotlin.math.max

internal data class LegendPhotoTransform(val zoom: Float = 1f, val x: Float = 0f, val y: Float = 0f, val rotation: Float = 0f, val brightness: Float = 0f, val saturation: Float = 1f)

// The preview and exported file use this same transform and appearance pipeline.
internal fun drawLegendPhoto(canvas: AndroidCanvas, width: Float, height: Float, bitmap: Bitmap, edit: LegendPhotoTransform) {
    canvas.save()
    canvas.clipRect(0f, 0f, width, height)
    canvas.drawColor(android.graphics.Color.BLACK)
    val angle = Math.toRadians(edit.rotation.toDouble())
    val cosine = kotlin.math.abs(kotlin.math.cos(angle)).toFloat()
    val sine = kotlin.math.abs(kotlin.math.sin(angle)).toFloat()
    val scale = max((width * cosine + height * sine) / bitmap.width, (width * sine + height * cosine) / bitmap.height) * edit.zoom
    val colors = ColorMatrix().apply { setSaturation(edit.saturation) }
    colors.postConcat(ColorMatrix(floatArrayOf(1f,0f,0f,0f,edit.brightness*255, 0f,1f,0f,0f,edit.brightness*255, 0f,0f,1f,0f,edit.brightness*255, 0f,0f,0f,1f,0f)))
    val paint = Paint(Paint.ANTI_ALIAS_FLAG or Paint.FILTER_BITMAP_FLAG).apply { colorFilter = ColorMatrixColorFilter(colors) }
    canvas.translate(width / 2 + edit.x * width, height / 2 + edit.y * height)
    canvas.rotate(edit.rotation)
    canvas.scale(scale, scale)
    canvas.drawBitmap(bitmap, -bitmap.width / 2f, -bitmap.height / 2f, paint)
    canvas.restore()
}

/** Decode within the memory budget and honor camera orientation on every supported API. */
internal fun decodeLegendPhoto(context: Context, uri: Uri): Bitmap {
    val resolver = context.contentResolver
    val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
    resolver.openInputStream(uri).use { BitmapFactory.decodeStream(it, null, bounds) }
    require(bounds.outWidth > 0 && bounds.outHeight > 0) { "Unsupported photo" }
    var sample = 1
    while (max(bounds.outWidth, bounds.outHeight) / sample > 2048) sample *= 2
    val bitmap = resolver.openInputStream(uri).use {
        BitmapFactory.decodeStream(it, null, BitmapFactory.Options().apply { inSampleSize = sample; inPreferredConfig = Bitmap.Config.ARGB_8888 })
    } ?: error("Photo could not be decoded")
    val orientation = try {
        resolver.openInputStream(uri).use { stream ->
            if (stream == null) ExifInterface.ORIENTATION_NORMAL
            else ExifInterface(stream).getAttributeInt(ExifInterface.TAG_ORIENTATION, ExifInterface.ORIENTATION_NORMAL)
        }
    } catch (_: java.io.IOException) { ExifInterface.ORIENTATION_NORMAL }
    val transform = when (orientation) {
        ExifInterface.ORIENTATION_FLIP_HORIZONTAL -> floatArrayOf(-1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f)
        ExifInterface.ORIENTATION_ROTATE_180 -> floatArrayOf(-1f, 0f, 0f, 0f, -1f, 0f, 0f, 0f, 1f)
        ExifInterface.ORIENTATION_FLIP_VERTICAL -> floatArrayOf(1f, 0f, 0f, 0f, -1f, 0f, 0f, 0f, 1f)
        ExifInterface.ORIENTATION_TRANSPOSE -> floatArrayOf(0f, 1f, 0f, 1f, 0f, 0f, 0f, 0f, 1f)
        ExifInterface.ORIENTATION_ROTATE_90 -> floatArrayOf(0f, -1f, 0f, 1f, 0f, 0f, 0f, 0f, 1f)
        ExifInterface.ORIENTATION_TRANSVERSE -> floatArrayOf(0f, -1f, 0f, -1f, 0f, 0f, 0f, 0f, 1f)
        ExifInterface.ORIENTATION_ROTATE_270 -> floatArrayOf(0f, 1f, 0f, -1f, 0f, 0f, 0f, 0f, 1f)
        else -> return bitmap
    }
    val oriented = Bitmap.createBitmap(bitmap, 0, 0, bitmap.width, bitmap.height, Matrix().apply { setValues(transform) }, true)
    if (oriented !== bitmap) bitmap.recycle()
    return oriented
}

@Composable
internal fun LegendSocialPhotoEditor(uri: Uri, ratios: List<Double>, back: () -> Unit, done: (Uri) -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var image by remember(uri) { mutableStateOf<Bitmap?>(null) }
    var error by remember(uri) { mutableStateOf<String?>(null) }
    var edit by remember(uri) { mutableStateOf(LegendPhotoTransform()) }
    var aspect by remember(uri) { mutableStateOf(ratios.firstOrNull()?.toFloat() ?: 1f) }
    var saving by remember { mutableStateOf(false) }
    LaunchedEffect(uri) {
        try {
            image = withContext(Dispatchers.IO) {
                decodeLegendPhoto(context, uri)
            }
        } catch (cancelled: kotlinx.coroutines.CancellationException) { throw cancelled }
        catch (_: Exception) { error = "This photo could not be opened. Select it again." }
    }
    Column(Modifier.fillMaxWidth().fillMaxHeight(.94f).background(LegendColors.Midnight).padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Row {
            TextButton(onClick = back, enabled = !saving) { Text(legendLocalized("Back"), color = LegendColors.OnNavy) }
            Text(legendLocalized("Edit photo"), Modifier.weight(1f), color = LegendColors.OnNavy, style = LegendTypography.Title)
            TextButton(enabled = image != null && !saving, onClick = {
                val bitmap = image ?: return@TextButton
                saving = true
                scope.launch {
                    try {
                        val result = withContext(Dispatchers.IO) {
                            val width = if (aspect >= 1) 2048 else (2048 * aspect).toInt()
                            val height = (width / aspect).toInt()
                            val output = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
                            drawLegendPhoto(AndroidCanvas(output), width.toFloat(), height.toFloat(), bitmap, edit)
                            val file = File.createTempFile("legend-photo-", ".jpg", context.cacheDir)
                            try { file.outputStream().use { check(output.compress(Bitmap.CompressFormat.JPEG, 92, it)) }; Uri.fromFile(file) }
                            catch (failure: Throwable) { file.delete(); throw failure }
                            finally { output.recycle() }
                        }
                        done(result)
                    } catch (cancelled: kotlinx.coroutines.CancellationException) { throw cancelled }
                    catch (_: Exception) { error = "The photo could not be prepared. Try again." }
                    finally { saving = false }
                }
            }) { Text(legendLocalized(if (saving) "Saving…" else "Next"), color = LegendColors.GoldBright) }
        }
        Box(Modifier.weight(1f).fillMaxWidth(), contentAlignment = androidx.compose.ui.Alignment.Center) {
            image?.let { bitmap ->
                Canvas(Modifier.fillMaxWidth().aspectRatio(aspect, matchHeightConstraintsFirst = true).semantics { contentDescription = legendLocalized("Photo crop preview", "accessibility copy"); stateDescription = "${(edit.zoom * 100).toInt()}%" }.pointerInput(uri) {
                    detectTransformGestures { _, pan, zoom, _ ->
                        if (!saving) edit = edit.copy(zoom = (edit.zoom * zoom).coerceIn(.25f, 6f), x = edit.x + pan.x / size.width.coerceAtLeast(1), y = edit.y + pan.y / size.height.coerceAtLeast(1))
                    }
                }) { drawIntoCanvas { drawLegendPhoto(it.nativeCanvas, size.width, size.height, bitmap, edit) } }
            } ?: CircularProgressIndicator(color = LegendColors.GoldBright)
        }
        Text(legendLocalized("Pinch to zoom. Drag to reposition."), color = LegendColors.OnNavy)
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            ratios.forEach { ratio -> TextButton(onClick = { aspect = ratio.toFloat() }, enabled = !saving) { Text(if (ratio == 1.0) "1:1" else if (ratio == .8) "4:5" else "9:16", color = if (aspect == ratio.toFloat()) LegendColors.GoldBright else LegendColors.OnNavy) } }
            TextButton(onClick = { edit = edit.copy(rotation = (edit.rotation + 90) % 360) }, enabled = !saving) { Text(legendLocalized("Rotate"), color = LegendColors.GoldBright) }
            TextButton(onClick = { edit = LegendPhotoTransform() }, enabled = !saving) { Text(legendLocalized("Reset"), color = LegendColors.GoldBright) }
        }
        Text(legendLocalized("Brightness"), color = LegendColors.OnNavy)
        Slider(edit.brightness, { edit = edit.copy(brightness = it) }, valueRange = -.5f.. .5f, enabled = !saving)
        Text(legendLocalized("Color"), color = LegendColors.OnNavy)
        Slider(edit.saturation, { edit = edit.copy(saturation = it) }, valueRange = 0f..2f, enabled = !saving)
        error?.let { Text(legendLocalized(it), color = LegendColors.GoldBright) }
    }
}
