package com.mylegnd.legend.registered.core.design

import android.content.Context
import com.mylegnd.legend.registered.core.auth.SecureSessionStore
import com.mylegnd.legend.registered.core.model.ApplicationLocalizationCatalog
import com.mylegnd.legend.registered.core.model.ApplicationLocalizedCopy
import com.mylegnd.legend.registered.data.ApplicationLocalizationRepository
import com.mylegnd.legend.registered.data.LoadState
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import java.util.Locale
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.snapshots.Snapshot
import kotlinx.coroutines.delay
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlinx.coroutines.CancellationException
import com.mylegnd.legend.registered.core.model.ApplicationLocalizationContinuation

data class LegendLocalizationState(
    val actorKey: String? = null,
    val languageCode: String = "en",
    val locale: Locale = Locale.ENGLISH,
    val revision: Long = 0,
    val isReady: Boolean = false,
    val status: String? = null,
)

/**
 * Device presentation adapter for the server-authoritative localization
 * catalog. It owns no translation rules or provider calls. The encrypted
 * account launch cache is only an offline/performance tier; the durable
 * LegendTranslationAlignment store remains the reuse authority.
 */
class LegendApplicationLocalization(
    context: Context,
    private val repository: ApplicationLocalizationRepository,
    private val cache: SecureSessionStore,
) {
    private val json = Json { ignoreUnknownKeys = true }
    private val sourceCatalog = context.assets.open("legend-application-copy.json")
        .bufferedReader()
        .use { json.decodeFromString(BundledApplicationCopyManifest.serializer(), it.readText()) }
    private var requestGeneration = 0L
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private var fetchJob: Job? = null
    private var foreground = true
    private var activeRole: String? = null
    private var activeRequest: Pair<String, String>? = null
    private val warmCatalogs = linkedMapOf<String, ApplicationLocalizationCatalog>()
    private var notBeforeMillis = 0L
    private var continuation: ApplicationLocalizationContinuation? = null
    private val _state = MutableStateFlow(LegendLocalizationState())
    val state: StateFlow<LegendLocalizationState> = _state.asStateFlow()

    init {
        install(
            sourceCatalog.entries.associate { entry ->
                LegendLocalizationKey(entry.source, entry.context) to entry.source
            },
            "en",
        )
    }

    suspend fun activate(actorKey: String, participantType: String, preferredLanguageCode: String?) {
        stopFetch()
        val generation = requestGeneration
        if (_state.value.actorKey != actorKey || activeRole != participantType) {
            warmCatalogs.clear()
            continuation = null
            notBeforeMillis = 0
            installSource(actorKey)
        }
        activeRole = participantType
        activeRequest = actorKey to participantType
        val saved = cache.localizationCatalogs(actorKey, participantType)
        if (generation != requestGeneration) return
        saved.filter { it.isPresentable() }.forEach { remember(it) }
        applyWarm(actorKey, preferredLanguageCode)
        startFetch()
    }

    suspend fun refresh(actorKey: String, participantType: String, preferredLanguageCode: String? = null) {
        // A delayed save callback cannot select an account. Only the authenticated activation owns that transition.
        if (activeRequest != (actorKey to participantType)) return
        stopFetch()
        if (preferredLanguageCode != null) { continuation = null; notBeforeMillis = 0 }
        applyWarm(actorKey, preferredLanguageCode)
        startFetch()
    }

    fun setForeground(isForeground: Boolean) {
        if (foreground == isForeground) return
        foreground = isForeground
        if (isForeground) startFetch() else stopFetch()
    }

    private fun stopFetch() { requestGeneration++; fetchJob?.cancel(); fetchJob = null }

    private fun startFetch() {
        val request = activeRequest ?: return
        if (!foreground || fetchJob?.isActive == true) return
        val generation = requestGeneration
        fetchJob = scope.launch { fetchCatalog(request.first, request.second, generation) }
    }

    private fun applyWarm(actorKey: String, language: String?) {
        val cached = warmCatalogs.values.lastOrNull { language.isNullOrBlank() || it.languageCode.equals(language, true) }
        if (cached != null) apply(actorKey, cached)
        else if (language != null && !_state.value.languageCode.equals(language, true)) installSource(actorKey)
    }

    private fun remember(value: ApplicationLocalizationCatalog): ApplicationLocalizationCatalog {
        val key = value.catalogVersion + "\n" + value.languageCode.lowercase(Locale.ROOT)
        warmCatalogs.remove(key)
        warmCatalogs[key] = value
        while (warmCatalogs.size > 8) warmCatalogs.remove(warmCatalogs.keys.first())
        return value
    }

    private suspend fun fetchCatalog(actorKey: String, participantType: String, generation: Long) {
        var started = System.nanoTime()
        var requests = 0
        var unchanged = 0
        var identity: String? = null
        var remaining = Int.MAX_VALUE
        var transportFailures = 0
        while (foreground && generation == requestGeneration) {
            val wait = notBeforeMillis - System.currentTimeMillis()
            if (wait > 0) delay(wait)
            if (!foreground || generation != requestGeneration) return
            val result = try { repository.catalog(participantType) } catch (cancelled: CancellationException) { throw cancelled }
            if (generation != requestGeneration) return
            if (result is LoadState.Error && result.status in setOf(401, 403)) { clearPresentation(); return }
            if (result is LoadState.Data && !result.value.isPresentable()) {
                continuation = null
                _state.value = _state.value.copy(status = LegendDesignAuthority.copy("localization.unavailable"))
                return
            }
            if (result is LoadState.Data) {
                transportFailures = 0
                val received = result.value
                apply(actorKey, remember(received))
                cache.writeLocalizationCatalog(actorKey, participantType, warmCatalogs.values.last())
                if (generation != requestGeneration) return
                val next = received.continuation
                continuation = next
                if (next?.isResumable() != true) {
                    _state.value = _state.value.copy(status = if (received.isComplete) null else LegendDesignAuthority.copy("localization.unavailable"))
                    return
                }
                val key = received.catalogVersion + "\n" + received.languageCode
                unchanged = if (identity == key && next.remainingEntries >= remaining) unchanged + 1 else 0
                identity = key; remaining = next.remainingEntries; requests++
                val exhausted = unchanged >= next.maximumConsecutiveNoProgress || requests >= next.maximumRequestsPerPass ||
                    (System.nanoTime() - started) / 1_000_000_000 >= next.maximumDurationSeconds
                val delaySeconds = maxOf(next.retryAfterSeconds!!, if (exhausted) next.cooldownSeconds else 0)
                notBeforeMillis = System.currentTimeMillis() + delaySeconds.toLong() * 1_000
                _state.value = _state.value.copy(status = LegendDesignAuthority.copy("localization.updating"))
                if (exhausted) { started = System.nanoTime(); requests = 0; unchanged = 0; remaining = Int.MAX_VALUE }
            } else {
                _state.value = _state.value.copy(status = LegendDesignAuthority.copy("localization.unavailable"))
                if (result is LoadState.Error && result.status != null && result.status in 400..499 && result.status !in setOf(408, 429)) { continuation = null; return }
                transportFailures++
                val delaySeconds = minOf(60, 1 shl minOf(transportFailures, 6))
                notBeforeMillis = System.currentTimeMillis() + maxOf(delaySeconds, continuation?.retryAfterSeconds ?: 0, continuation?.cooldownSeconds ?: 0).toLong() * 1_000
                started = System.nanoTime(); requests = 0; unchanged = 0; remaining = Int.MAX_VALUE
            }
        }
    }

    private fun ApplicationLocalizationCatalog.isPresentable(): Boolean =
        sourceLanguageCode == sourceCatalog.sourceLanguageCode && languageCode.isNotBlank() &&
            entries.isNotEmpty() && entries.map { it.id }.toSet().size == entries.size

    fun clearPresentation() {
        stopFetch()
        activeRequest = null; activeRole = null; continuation = null; notBeforeMillis = 0
        warmCatalogs.clear()
        installSource(actorKey = null)
    }

    private fun apply(actorKey: String, catalog: ApplicationLocalizationCatalog) {
        val byId = catalog.entries.associateBy { it.id }
        val translations = sourceCatalog.entries.associate { source ->
            val translated = byId[source.id]
            LegendLocalizationKey(source.source, source.context) to
                (translated?.validatedText(source.source, source.context, source.sourceRevision, source.placeholders) ?: source.source)
        }
        install(translations, catalog.locale, actorKey)
    }

    private fun installSource(actorKey: String?) {
        install(
            sourceCatalog.entries.associate { entry ->
                LegendLocalizationKey(entry.source, entry.context) to entry.source
            },
            "en",
            actorKey,
        )
    }

    private fun install(
        translations: Map<LegendLocalizationKey, String>,
        languageCode: String,
        actorKey: String? = null,
    ) {
        val locale = Locale.forLanguageTag(languageCode.ifBlank { "en" })
        val presentationChanged = LegendLocalizationRuntime.install(translations, locale)
        // Identical cache/server catalogs must not recreate the shell, close
        // its realtime connection, or restart its loading effects.
        if (!presentationChanged && _state.value.actorKey == actorKey &&
            _state.value.languageCode == languageCode) return
        _state.value = LegendLocalizationState(
            actorKey = actorKey,
            languageCode = languageCode,
            locale = locale,
            revision = _state.value.revision + 1,
            isReady = actorKey != null,
        )
    }

    @Serializable
    private data class BundledApplicationCopyManifest(
        val catalogVersion: String,
        val sourceLanguageCode: String,
        val entries: List<BundledApplicationCopy>,
    )

    @Serializable
    private data class BundledApplicationCopy(
        val id: String,
        val source: String,
        val context: String,
        val sourceRevision: String,
        val placeholders: List<String>,
    )
}

data class LegendLocalizationKey(val source: String, val context: String)

object LegendLocalizationRuntime {
    const val VisualContext = "visual interface copy"
    const val AccessibilityContext = "accessibility copy"
    private val translations = mutableStateOf<Map<LegendLocalizationKey, String>>(emptyMap())
    private val activeLocale = mutableStateOf(Locale.ENGLISH)

    @Synchronized
    fun install(values: Map<LegendLocalizationKey, String>, locale: Locale): Boolean {
        if (translations.value == values && activeLocale.value == locale) return false
        Snapshot.withMutableSnapshot {
            translations.value = values.toMap()
            activeLocale.value = locale
        }
        return true
    }

    fun text(source: String, context: String = VisualContext): String =
        translations.value[LegendLocalizationKey(source, context)] ?: source

    fun locale(): Locale = activeLocale.value
}

fun legendLocalized(
    source: String,
    context: String = LegendLocalizationRuntime.VisualContext,
): String = LegendLocalizationRuntime.text(source, context)

fun legendLocalized(
    source: String,
    arguments: Map<String, Any>,
    context: String = LegendLocalizationRuntime.VisualContext,
): String = arguments.entries.fold(LegendLocalizationRuntime.text(source, context)) { text, entry ->
    text.replace("{${entry.key}}", entry.value.toString())
}

fun legendLocalized(
    source: String,
    context: String,
    arguments: Map<String, Any>,
): String = legendLocalized(source, arguments, context)

/** A release may add copy without invalidating unchanged retained translations. */
fun ApplicationLocalizedCopy.validatedText(source: String, context: String, revision: String, placeholders: List<String>): String? =
    text.takeIf { failureCode == null && this.source == source && this.context == context &&
        sourceRevision == revision && this.placeholders.sorted() == placeholders.sorted() && it.isNotBlank() }
