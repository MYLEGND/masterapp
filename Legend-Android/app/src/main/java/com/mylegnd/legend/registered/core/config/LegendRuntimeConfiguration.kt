package com.mylegnd.legend.registered.core.config

import android.content.Context
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

@Serializable
data class LegendRuntimeConfiguration(
    val apiBaseUrl: String = "",
    val entraClientId: String = "",
    val entraAuthority: String = "",
    val entraScope: String = "",
    val msalRedirectUri: String = "",
) {
    val isReady: Boolean get() = listOf(apiBaseUrl, entraClientId, entraAuthority, entraScope, msalRedirectUri).all {
        it.isNotBlank() && !it.contains("${'$'}(")
    } && apiBaseUrl.startsWith("https://") && entraAuthority.startsWith("https://") && msalRedirectUri.startsWith("msauth://")
}

object LegendRuntimeConfigurationLoader {
    private val json = Json { ignoreUnknownKeys = false }

    fun load(context: Context): LegendRuntimeConfiguration = runCatching {
        context.assets.open("legend-runtime.json").bufferedReader().use {
            json.decodeFromString(LegendRuntimeConfiguration.serializer(), it.readText())
        }
    }.getOrDefault(LegendRuntimeConfiguration())
}
