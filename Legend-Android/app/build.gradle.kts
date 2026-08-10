import org.jetbrains.kotlin.gradle.dsl.JvmTarget
import org.gradle.api.tasks.Sync
import java.net.URLDecoder
import java.nio.charset.StandardCharsets
import java.util.Properties

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.kotlin.serialization)
}

if (file("google-services.json").isFile) {
    apply(plugin = "com.google.gms.google-services")
}

val legendProperties = Properties().apply {
    val configuration = rootProject.file("legend.properties")
    if (configuration.isFile) {
        configuration.inputStream().use(::load)
    }
}
fun legendValue(name: String): String = legendProperties.getProperty(name)?.trim().orEmpty()

val legendRuntimeRoot = layout.buildDirectory.dir("generated/legend-runtime")
val legendRuntimeAssets = legendRuntimeRoot.map { it.dir("assets") }
val legendRuntimeRes = legendRuntimeRoot.map { it.dir("res") }
val sharedLegendDesignSpec = rootProject.file("../Legend-Design/legend-design.tokens.json")
val legendDesignAssets = layout.buildDirectory.dir("generated/legend-design/assets")
val msalRedirectUri = legendValue("LEGEND_MSAL_REDIRECT_URI")
// MSAL's config/Entra URI uses a URL-encoded Base64 hash. Android's manifest intent filter
// needs that same hash decoded for its path matcher. Deriving both from one value prevents drift.
val msalSignatureHash = URLDecoder.decode(
    msalRedirectUri.substringAfterLast('/', missingDelimiterValue = ""),
    StandardCharsets.UTF_8,
)
    .takeIf { it.isNotBlank() }
    ?: "unconfigured"

/**
 * Packages non-secret environment values into generated runtime resources.  A Sync task keeps
 * this work configuration-cache compatible: it owns only immutable input values and declared
 * output directories, rather than a build-script closure captured by a DefaultTask action.
 */
val generateLegendRuntimeConfiguration by tasks.registering(Sync::class) {
    inputs.file(rootProject.file("legend.properties")).optional()
    from("src/main/legend-template")
    into(legendRuntimeRoot)
    expand(
        mapOf(
            "apiBaseUrl" to legendValue("LEGEND_API_BASE_URL"),
            "entraClientId" to legendValue("LEGEND_ENTRA_CLIENT_ID"),
            "entraAuthority" to legendValue("LEGEND_ENTRA_AUTHORITY"),
            "entraScope" to legendValue("LEGEND_ENTRA_SCOPE"),
            "entraTenantId" to legendValue("LEGEND_ENTRA_TENANT_ID"),
            "msalRedirectUri" to msalRedirectUri,
        ),
    )
}

/** Bundles the single cross-platform design authority without copying it into Android source. */
val bundleLegendDesignSpecification by tasks.registering(Sync::class) {
    from(sharedLegendDesignSpec)
    into(legendDesignAssets)
}

android {
    namespace = "com.mylegnd.legend.registered"
    compileSdk = 37

    defaultConfig {
        applicationId = "com.mylegnd.legend.registered"
        minSdk = 26
        targetSdk = 36
        versionCode = 1
        versionName = "1.0.0"

        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
        manifestPlaceholders["msalSignatureHash"] = msalSignatureHash
    }

    buildTypes {
        debug {
            versionNameSuffix = "-debug"
        }
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro"
            )
        }
    }

    buildFeatures {
        buildConfig = true
        compose = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_21
        targetCompatibility = JavaVersion.VERSION_21
    }

    kotlin {
        compilerOptions {
            jvmTarget.set(JvmTarget.JVM_21)
        }
    }

    packaging {
        resources.excludes += "/META-INF/{AL2.0,LGPL2.1}"
    }

    sourceSets.getByName("main") {
        assets.directories.add(legendRuntimeAssets.get().asFile.absolutePath)
        assets.directories.add(legendDesignAssets.get().asFile.absolutePath)
        res.directories.add(legendRuntimeRes.get().asFile.absolutePath)
    }
}

tasks.named("preBuild").configure {
    dependsOn(generateLegendRuntimeConfiguration)
    dependsOn(bundleLegendDesignSpecification)
}

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.lifecycle.runtime.ktx)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(libs.androidx.navigation.compose)
    implementation(libs.androidx.datastore.preferences)

    implementation(platform(libs.compose.bom))
    implementation(libs.compose.ui)
    implementation(libs.compose.ui.tooling.preview)
    implementation(libs.compose.foundation)
    implementation(libs.compose.material3)
    implementation(libs.compose.material.icons)

    implementation(libs.retrofit)
    implementation(libs.retrofit.kotlinx.serialization)
    implementation(libs.okhttp)
    implementation(libs.okhttp.logging)
    implementation(libs.kotlinx.serialization.json)
    implementation(libs.coil.compose)
    implementation(libs.coil.network.okhttp)
    implementation(libs.media3.exoplayer)
    implementation(libs.media3.ui)
    implementation(libs.msal)
    implementation(platform(libs.firebase.bom))
    implementation(libs.firebase.messaging)
    implementation(libs.firebase.installations)

    testImplementation(libs.junit)
    androidTestImplementation(libs.androidx.junit)
    androidTestImplementation(libs.espresso.core)
    androidTestImplementation(platform(libs.compose.bom))
    androidTestImplementation(libs.compose.ui.test.junit4)
    debugImplementation(libs.compose.ui.tooling)
    debugImplementation(libs.compose.ui.test.manifest)
}
