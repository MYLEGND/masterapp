package com.mylegnd.legend.registered.core.design

import android.content.Context
import com.mylegnd.legend.registered.core.auth.SecureSessionStore
import com.mylegnd.legend.registered.core.model.ApplicationLocalizationCatalog
import com.mylegnd.legend.registered.data.ApplicationLocalizationRepository
import com.mylegnd.legend.registered.data.LoadState
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import java.util.Locale
import java.util.concurrent.atomic.AtomicReference

data class LegendLocalizationState(
    val actorKey: String? = null,
    val languageCode: String = "en",
    val locale: Locale = Locale.ENGLISH,
    val revision: Long = 0,
    val isReady: Boolean = false,
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
        val cached = cache.localizationCatalog(actorKey)
        if (cached != null && cached.isPresentable() &&
            (preferredLanguageCode.isNullOrBlank() ||
                cached.languageCode.equals(preferredLanguageCode, ignoreCase = true))) {
            apply(actorKey, cached)
        }

        // Never block the authenticated shell on network/provider latency.
        // A complete source catalog is installed atomically until the complete
        // preferred-language catalog is available for a single state swap.
        if (_state.value.actorKey != actorKey) {
            installSource(actorKey)
        }

        when (val result = repository.catalog(participantType)) {
            is LoadState.Data -> {
                val catalog = result.value
                if (
                    catalog.isPresentable() &&
                    (preferredLanguageCode.isNullOrBlank() ||
                        catalog.languageCode.equals(preferredLanguageCode, ignoreCase = true))
                ) {
                    apply(actorKey, catalog)
                    cache.writeLocalizationCatalog(actorKey, catalog)
                    return
                }
            }
            else -> Unit
        }

        // Cached or packaged source copy already provides the fail-safe.
    }

    suspend fun refresh(actorKey: String, participantType: String) {
        when (val result = repository.catalog(participantType)) {
            is LoadState.Data -> {
                if (!result.value.isPresentable()) return
                apply(actorKey, result.value)
                cache.writeLocalizationCatalog(actorKey, result.value)
            }
            else -> Unit
        }
    }

    private fun ApplicationLocalizationCatalog.isPresentable(): Boolean =
        catalogVersion == sourceCatalog.catalogVersion &&
            entries.map { it.id }.toSet() == sourceCatalog.entries.map { it.id }.toSet() &&
            entries.none { entry ->
                entry.failureCode != null && entry.failureCode != "approved_translation_unavailable"
            }

    fun clearPresentation() {
        installSource(actorKey = null)
    }

    private fun apply(actorKey: String, catalog: ApplicationLocalizationCatalog) {
        val translations = sourceCatalog.entries.associate { source ->
            val translated = catalog.entries.firstOrNull { it.id == source.id }
            LegendLocalizationKey(source.source, source.context) to
                (translated?.text?.takeIf(String::isNotBlank) ?: source.source)
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
        LegendLocalizationRuntime.install(translations, locale)
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
    )
}

data class LegendLocalizationKey(val source: String, val context: String)

object LegendLocalizationRuntime {
    const val VisualContext = "visual interface copy"
    const val AccessibilityContext = "accessibility copy"
    private val translations = AtomicReference<Map<LegendLocalizationKey, String>>(emptyMap())
    private val activeLocale = AtomicReference(Locale.ENGLISH)

    fun install(values: Map<LegendLocalizationKey, String>, locale: Locale) {
        translations.set(values.toMap())
        activeLocale.set(locale)
    }

    fun text(source: String, context: String = VisualContext): String =
        translations.get()[LegendLocalizationKey(source, context)] ?: source

    fun locale(): Locale = activeLocale.get()
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
