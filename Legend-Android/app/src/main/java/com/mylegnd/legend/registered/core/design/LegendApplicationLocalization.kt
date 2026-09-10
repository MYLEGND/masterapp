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

    suspend fun activate(
        actorKey: String,
        participantType: String,
        preferredLanguageCode: String?,
    ) {
        val generation = ++requestGeneration
        val cached = cache.localizationCatalog(actorKey)
        if (cached != null && cached.isPresentable() &&
            (preferredLanguageCode.isNullOrBlank() ||
                cached.languageCode.equals(preferredLanguageCode, ignoreCase = true))) {
            apply(actorKey, cached)
        }

        // Never block the authenticated shell on network/provider latency.
        // A complete source catalog is installed atomically until validated
        // preferred-language entries are available for a single state swap.
        if (_state.value.actorKey != actorKey) {
            installSource(actorKey)
        }

        fetchCatalog(actorKey, participantType, generation)
    }

    suspend fun refresh(actorKey: String, participantType: String) {
        fetchCatalog(actorKey, participantType, ++requestGeneration)
    }

    private suspend fun fetchCatalog(actorKey: String, participantType: String, generation: Long) {
        repeat(60) { attempt ->
            if (generation != requestGeneration) return
            when (val result = repository.catalog(participantType)) {
                is LoadState.Data -> {
                    if (generation != requestGeneration) return
                    if (!result.value.isPresentable()) {
                        _state.value = _state.value.copy(status = LegendDesignAuthority.copy("localization.unavailable"))
                        return
                    }
                    apply(actorKey, result.value)
                    cache.writeLocalizationCatalog(actorKey, result.value)
                    val blocked = result.value.entries.any { it.failureCode != null && it.failureCode !in setOf("translation_pending", "approved_translation_unavailable", "translation_output_invalid", "translation_provider_failed") }
                    if (!blocked && result.value.entries.any { it.failureCode == "translation_pending" }) {
                        _state.value = _state.value.copy(status = LegendDesignAuthority.copy("localization.updating"))
                        delay(1_000)
                    } else {
                        _state.value = _state.value.copy(status = if (result.value.entries.any { it.failureCode != null && it.failureCode != "approved_translation_unavailable" }) LegendDesignAuthority.copy("localization.unavailable") else null)
                        return
                    }
                }
                else -> {
                    if (generation != requestGeneration) return
                    if (attempt < 2) delay(2_000) else {
                        _state.value = _state.value.copy(status = LegendDesignAuthority.copy("localization.unavailable"))
                        return
                    }
                }
            }
        }
        if (generation == requestGeneration) _state.value = _state.value.copy(status = LegendDesignAuthority.copy("localization.unavailable"))
    }

    private fun ApplicationLocalizationCatalog.isPresentable(): Boolean =
        sourceLanguageCode == sourceCatalog.sourceLanguageCode && languageCode.isNotBlank() &&
            entries.isNotEmpty() && entries.map { it.id }.toSet().size == entries.size

    fun clearPresentation() {
        requestGeneration++
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
