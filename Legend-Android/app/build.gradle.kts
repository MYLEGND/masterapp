import org.jetbrains.kotlin.gradle.dsl.JvmTarget
import org.gradle.api.tasks.Sync
import java.net.URLDecoder
import java.net.URLEncoder
import java.nio.charset.StandardCharsets
import java.security.KeyStore
import java.security.MessageDigest
import java.util.Base64
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

val legendApplicationId = "com.mylegnd.legend.registered"
val legendDebugRuntimeRoot = layout.buildDirectory.dir("generated/legend-runtime/debug")
val legendReleaseRuntimeRoot = layout.buildDirectory.dir("generated/legend-runtime/release")
val legendDebugRuntimeAssets = legendDebugRuntimeRoot.map { it.dir("assets") }
val legendDebugRuntimeRes = legendDebugRuntimeRoot.map { it.dir("res") }
val legendReleaseRuntimeAssets = legendReleaseRuntimeRoot.map { it.dir("assets") }
val legendReleaseRuntimeRes = legendReleaseRuntimeRoot.map { it.dir("res") }
val sharedLegendDesignSpec = rootProject.file("../Legend-Design/legend-design.tokens.json")
val legendDesignAssets = layout.buildDirectory.dir("generated/legend-design/assets")
// The iOS asset catalog owns the brand artwork. Android packages that source
// at build time instead of keeping a second, drift-prone copy in res/.
val sharedLegendBrandLogo = rootProject.file(
    "../Legend-ios/Legend/Resources/Assets.xcassets/LegendLogo.imageset/legend-logo.png",
)
val sharedLegendFounderAiLogo = rootProject.file(
    "../Legend-ios/Legend/Resources/Assets.xcassets/LegendAiIcon.imageset/legendai.png",
)
val sharedLegendAppIcon = rootProject.file(
    "../Legend-ios/Legend/Resources/Assets.xcassets/AppIcon.appiconset/AppIcon-1024.png",
)
val legendBrandAssets = layout.buildDirectory.dir("generated/legend-brand/assets")
val legendBrandRes = layout.buildDirectory.dir("generated/legend-brand/res")
val productionMsalRedirectUri = legendValue("LEGEND_MSAL_REDIRECT_URI")

fun signingCertificateHash(keyStoreFile: File): String? = runCatching {
    val keyStore = KeyStore.getInstance("JKS")
    keyStoreFile.inputStream().use { keyStore.load(it, "android".toCharArray()) }
    val certificate = keyStore.getCertificate("androiddebugkey") ?: return@runCatching null
    Base64.getEncoder().encodeToString(
        MessageDigest.getInstance("SHA-1").digest(certificate.encoded),
    )
}.getOrNull()

val debugSigningHash = signingCertificateHash(
    file(System.getProperty("user.home")).resolve(".android/debug.keystore"),
)
val debugMsalRedirectUri = legendValue("LEGEND_MSAL_DEBUG_REDIRECT_URI").ifBlank {
    debugSigningHash?.let { signatureHash ->
        "msauth://$legendApplicationId/${URLEncoder.encode(signatureHash, StandardCharsets.UTF_8)}"
    }.orEmpty()
}

// MSAL's config/Entra URI uses a URL-encoded Base64 hash. Android's manifest intent filter
// needs that same hash decoded for its path matcher. Deriving both from one value prevents drift.
fun msalSignatureHash(redirectUri: String): String = URLDecoder.decode(
    redirectUri.substringAfterLast('/', missingDelimiterValue = ""),
    StandardCharsets.UTF_8,
).takeIf { it.isNotBlank() } ?: "unconfigured"

val productionMsalSignatureHash = msalSignatureHash(productionMsalRedirectUri)
val debugMsalSignatureHash = msalSignatureHash(debugMsalRedirectUri)

/**
 * Packages non-secret environment values into generated runtime resources.  A Sync task keeps
 * this work configuration-cache compatible: it owns only immutable input values and declared
 * output directories, rather than a build-script closure captured by a DefaultTask action.
 */
val generateLegendDebugRuntimeConfiguration by tasks.registering(Sync::class) {
    inputs.file(rootProject.file("legend.properties")).optional()
    from("src/main/legend-template")
    into(legendDebugRuntimeRoot)
    expand(
        mapOf(
            "apiBaseUrl" to legendValue("LEGEND_API_BASE_URL"),
            "entraClientId" to legendValue("LEGEND_ENTRA_CLIENT_ID"),
            "entraAuthority" to legendValue("LEGEND_ENTRA_AUTHORITY"),
            "entraScope" to legendValue("LEGEND_ENTRA_SCOPE"),
            "entraTenantId" to legendValue("LEGEND_ENTRA_TENANT_ID"),
            "msalRedirectUri" to debugMsalRedirectUri,
        ),
    )
}

val generateLegendReleaseRuntimeConfiguration by tasks.registering(Sync::class) {
    inputs.file(rootProject.file("legend.properties")).optional()
    from("src/main/legend-template")
    into(legendReleaseRuntimeRoot)
    expand(
        mapOf(
            "apiBaseUrl" to legendValue("LEGEND_API_BASE_URL"),
            "entraClientId" to legendValue("LEGEND_ENTRA_CLIENT_ID"),
            "entraAuthority" to legendValue("LEGEND_ENTRA_AUTHORITY"),
            "entraScope" to legendValue("LEGEND_ENTRA_SCOPE"),
            "entraTenantId" to legendValue("LEGEND_ENTRA_TENANT_ID"),
            "msalRedirectUri" to productionMsalRedirectUri,
        ),
    )
}

/** Bundles the single cross-platform design authority without copying it into Android source. */
val bundleLegendDesignSpecification by tasks.registering(Sync::class) {
    from(sharedLegendDesignSpec)
    into(legendDesignAssets)
}

/** Packages the exact iOS-owned LEGEND® and Founder AI artwork without an Android copy. */
val bundleLegendBrandArtwork by tasks.registering(Sync::class) {
    from(sharedLegendBrandLogo)
    from(sharedLegendFounderAiLogo)
    into(legendBrandAssets)
}

/**
 * Android packages the exact iOS AppIcon source as the artwork layer for its
 * adaptive icon. The adaptive wrapper prevents launchers from shrinking the
 * full square inside a second platform-generated circle.
 */
val bundleLegendLauncherArtwork by tasks.registering(Sync::class) {
    from(sharedLegendAppIcon) {
        rename { "ic_legend_launcher.png" }
    }
    into(legendBrandRes.map { it.dir("drawable-nodpi") })
}

android {
    namespace = legendApplicationId
    compileSdk = 37

    defaultConfig {
        applicationId = legendApplicationId
        minSdk = 26
        targetSdk = 37
        versionCode = 3
        versionName = "1.0.0"

        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
        manifestPlaceholders["msalSignatureHash"] = productionMsalSignatureHash
    }

    buildTypes {
        debug {
            versionNameSuffix = "-debug"
            manifestPlaceholders["msalSignatureHash"] = debugMsalSignatureHash
        }
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            manifestPlaceholders["msalSignatureHash"] = productionMsalSignatureHash
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
        assets.directories.add(legendDesignAssets.get().asFile.absolutePath)
        assets.directories.add(legendBrandAssets.get().asFile.absolutePath)
        res.directories.add(legendBrandRes.get().asFile.absolutePath)
    }
    sourceSets.getByName("debug") {
        assets.directories.add(legendDebugRuntimeAssets.get().asFile.absolutePath)
        res.directories.add(legendDebugRuntimeRes.get().asFile.absolutePath)
    }
    sourceSets.getByName("release") {
        assets.directories.add(legendReleaseRuntimeAssets.get().asFile.absolutePath)
        res.directories.add(legendReleaseRuntimeRes.get().asFile.absolutePath)
    }
}

tasks.matching { it.name == "preDebugBuild" }.configureEach {
    dependsOn(generateLegendDebugRuntimeConfiguration)
}

tasks.matching { it.name == "preReleaseBuild" }.configureEach {
    dependsOn(generateLegendReleaseRuntimeConfiguration)
}

tasks.named("preBuild").configure {
    dependsOn(bundleLegendDesignSpecification)
    dependsOn(bundleLegendBrandArtwork)
    dependsOn(bundleLegendLauncherArtwork)
}

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.lifecycle.runtime.ktx)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(libs.androidx.navigation.compose)
    implementation(libs.androidx.datastore.preferences)
    implementation(libs.androidx.biometric)

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
    implementation(libs.firebase.installations)
    implementation(libs.firebase.messaging)

    testImplementation(libs.junit)
    androidTestImplementation(libs.androidx.junit)
    androidTestImplementation(libs.espresso.core)
    androidTestImplementation(platform(libs.compose.bom))
    androidTestImplementation(libs.compose.ui.test.junit4)
    debugImplementation(libs.compose.ui.tooling)
    debugImplementation(libs.compose.ui.test.manifest)
}
