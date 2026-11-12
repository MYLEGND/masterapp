package com.mylegnd.legend.registered.ui

import android.Manifest
import android.content.pm.PackageManager
import android.net.Uri
import android.webkit.*
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import coil3.compose.AsyncImage
import com.mylegnd.legend.registered.core.design.LegendSize
import com.mylegnd.legend.registered.core.network.AssistantSurfaceLaunch
import com.mylegnd.legend.registered.data.LoadState
import com.mylegnd.legend.registered.feature.FounderAiViewModel
import java.time.Instant
import java.util.concurrent.atomic.AtomicBoolean

// Android's WebView cookie jar is process-wide. There can be exactly one live
// assistant host; only its purpose-specific cookie is replaced/removed. Other
// embedded workflows and their cookies are never cleared.
internal val assistantHostInUse = AtomicBoolean(false)
private const val COOKIE = "__Secure-LegendAssistant"
private const val ROOT = "/founder/legend-ai"

@Composable
fun LegendFounderAiLauncherButton(onClick: () -> Unit, modifier: Modifier = Modifier) {
    IconButton(onClick = onClick, modifier = modifier.size(LegendSize.MinimumTapTarget).clip(CircleShape)) {
        AsyncImage(model = "file:///android_asset/legendai.png", contentDescription = "Open LEGEND AI",
            modifier = Modifier.fillMaxSize().clip(CircleShape))
    }
}

@Composable
fun FounderAiConversationDialog(viewModel: FounderAiViewModel, onDismiss: () -> Unit) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    var launch by remember(viewModel) { mutableStateOf<Pair<String, AssistantSurfaceLaunch>?>(null) }
    var failure by remember(viewModel) { mutableStateOf<String?>(null) }
    var attempt by remember { mutableIntStateOf(0) }
    LaunchedEffect(viewModel, attempt) {
        failure = null
        when (val result = viewModel.surfaceLaunch()) {
            is LoadState.Data -> launch = result.value
            is LoadState.Error -> failure = result.message
            else -> failure = "The assistant could not be opened."
        }
    }
    LaunchedEffect(state.availability) {
        if (state.availability is LoadState.Idle) onDismiss()
    }
    Dialog(onDismissRequest = onDismiss, properties = DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false)) {
        BackHandler(onBack = onDismiss)
        Box(Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding().imePadding()) {
            val current = launch
            if (failure != null) {
                Column(Modifier.padding(24.dp)) {
                    Text("LEGEND AI unavailable", style = MaterialTheme.typography.titleLarge)
                    Text(failure!!)
                    Button(onClick = { launch = null; attempt++ }) { Text("Retry") }
                    TextButton(onClick = onDismiss) { Text("Close") }
                }
            } else if (current != null) {
                AssistantWebSurface(current.first, current.second, onDismiss) { failure = it; launch = null }
            } else CircularProgressIndicator()
        }
    }
}

@Composable
private fun AssistantWebSurface(base: String, launch: AssistantSurfaceLaunch, close: () -> Unit, fail: (String) -> Unit) {
    val context = LocalContext.current
    val origin = remember(base) { Uri.parse(base) }
    fun trusted(url: Uri) = url.scheme == "https" && url.host == origin.host &&
        (if (url.port < 0) 443 else url.port) == (if (origin.port < 0) 443 else origin.port) && url.userInfo == null
    var audioRequest by remember { mutableStateOf<PermissionRequest?>(null) }
    var web by remember { mutableStateOf<WebView?>(null) }
    var ownsHost by remember { mutableStateOf(false) }
    var disposed by remember { mutableStateOf(false) }
    val microphone = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { allowed ->
        audioRequest?.let { request ->
            if (!disposed && allowed && trusted(request.origin) && request.resources.contentEquals(arrayOf(PermissionRequest.RESOURCE_AUDIO_CAPTURE)))
                request.grant(arrayOf(PermissionRequest.RESOURCE_AUDIO_CAPTURE)) else request.deny()
        }
        audioRequest = null
    }
    DisposableEffect(Unit) {
        onDispose {
            disposed = true
            audioRequest?.deny(); audioRequest = null
            web?.apply { stopLoading(); webChromeClient = null; removeAllViews(); destroy() }; web = null
            if (ownsHost) {
                CookieManager.getInstance().setCookie(base, "$COOKIE=; Path=$ROOT; Max-Age=0; Secure; HttpOnly; SameSite=Strict") { removed ->
                    if (removed) assistantHostInUse.set(false)
                }
            }
        }
    }
    AndroidView(modifier = Modifier.fillMaxSize(), factory = { ctx ->
        WebView(ctx).apply {
            web = this
            settings.javaScriptEnabled = true
            settings.domStorageEnabled = false
            settings.allowFileAccess = false
            settings.allowContentAccess = false
            settings.javaScriptCanOpenWindowsAutomatically = false
            settings.setSupportMultipleWindows(false)
            settings.mixedContentMode = WebSettings.MIXED_CONTENT_NEVER_ALLOW
            settings.cacheMode = WebSettings.LOAD_NO_CACHE
            CookieManager.getInstance().setAcceptThirdPartyCookies(this, false)
            webViewClient = object : WebViewClient() {
                override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest): Boolean {
                    val url = request.url
                    if (!request.isForMainFrame || !trusted(url) || !url.path.orEmpty().startsWith("$ROOT/")) return true
                    if (url.path == "$ROOT/native-close") { close(); return true }
                    if (url.path == "$ROOT/native-expired") { fail("Your session expired. Reopen the assistant; no action was retried."); return true }
                    return false
                }
                override fun shouldInterceptRequest(view: WebView, request: WebResourceRequest): WebResourceResponse? =
                    if (trusted(request.url)) null else WebResourceResponse("text/plain", "UTF-8", 403, "Blocked", emptyMap(), java.io.ByteArrayInputStream(byteArrayOf()))
                override fun onReceivedHttpError(view: WebView, request: WebResourceRequest, response: WebResourceResponse) {
                    if (request.isForMainFrame) fail("The assistant could not load. Reopen it or sign in again.")
                }
                override fun onReceivedError(view: WebView, request: WebResourceRequest, error: WebResourceError) {
                    if (request.isForMainFrame) fail("The assistant connection failed. Please retry.")
                }
            }
            webChromeClient = object : WebChromeClient() {
                override fun onPermissionRequest(request: PermissionRequest) {
                    if (disposed || !trusted(request.origin) || !request.resources.contentEquals(arrayOf(PermissionRequest.RESOURCE_AUDIO_CAPTURE))) { request.deny(); return }
                    if (ContextCompat.checkSelfPermission(context, Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED)
                        request.grant(arrayOf(PermissionRequest.RESOURCE_AUDIO_CAPTURE))
                    else { audioRequest?.deny(); audioRequest = request; microphone.launch(Manifest.permission.RECORD_AUDIO) }
                }
                override fun onPermissionRequestCanceled(request: PermissionRequest) { if (audioRequest == request) audioRequest = null }
            }
            val valid = runCatching {
                trusted(origin) && launch.launchPath == "$ROOT/surface" && launch.cookieName == COOKIE && launch.cookiePath == ROOT &&
                    launch.cookieValue.isNotEmpty() && launch.cookieValue.none { it == ';' || it == '\r' || it == '\n' } &&
                    Instant.parse(launch.expiresUtc).isAfter(Instant.now()) && !Instant.parse(launch.expiresUtc).isAfter(Instant.now().plusSeconds(1205))
            }.getOrDefault(false)
            if (!valid || !assistantHostInUse.compareAndSet(false, true)) {
                post { fail("The secure assistant session could not be opened. Close another assistant window and retry.") }
            } else {
                ownsHost = true
                CookieManager.getInstance().setCookie(base, "$COOKIE=${launch.cookieValue}; Path=$ROOT; Secure; HttpOnly; SameSite=Strict") { accepted ->
                    if (!disposed && accepted) loadUrl(base.trimEnd('/') + launch.launchPath)
                    else if (!disposed) fail("The secure assistant session could not be established.")
                }
            }
        }
    })
}
