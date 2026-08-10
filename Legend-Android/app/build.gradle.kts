import org.jetbrains.kotlin.gradle.dsl.JvmTarget
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

val legendRuntimeAssets = layout.buildDirectory.dir("generated/legend-runtime/assets")
val legendRuntimeRes = layout.buildDirectory.dir("generated/legend-runtime/res")
val msalRedirectUri = legendValue("LEGEND_MSAL_REDIRECT_URI")
val msalSignatureHash = msalRedirectUri.substringAfterLast('/', missingDelimiterValue = "")
    .takeIf { it.isNotBlank() }
    ?: "unconfigured"

val generateLegendRuntimeConfiguration by tasks.registering {
    inputs.file(rootProject.file("legend.properties")).optional()
    outputs.dir(legendRuntimeAssets)
    outputs.dir(legendRuntimeRes)
    doLast {
        fun String.jsonEscaped() = replace("\\", "\\\\").replace("\"", "\\\"")
        val assetsDirectory = legendRuntimeAssets.get().asFile.apply { mkdirs() }
        assetsDirectory.resolve("legend-runtime.json").writeText(
            """{
              |  "apiBaseUrl": "${legendValue("LEGEND_API_BASE_URL").jsonEscaped()}",
              |  "entraClientId": "${legendValue("LEGEND_ENTRA_CLIENT_ID").jsonEscaped()}",
              |  "entraAuthority": "${legendValue("LEGEND_ENTRA_AUTHORITY").jsonEscaped()}",
              |  "entraScope": "${legendValue("LEGEND_ENTRA_SCOPE").jsonEscaped()}",
              |  "msalRedirectUri": "${msalRedirectUri.jsonEscaped()}"
              |}
            """.trimMargin(),
        )
        val rawDirectory = legendRuntimeRes.get().asFile.resolve("raw").apply { mkdirs() }
        rawDirectory.resolve("legend_msal_config.json").writeText(
            """{
              |  "client_id": "${legendValue("LEGEND_ENTRA_CLIENT_ID").jsonEscaped()}",
              |  "redirect_uri": "${msalRedirectUri.jsonEscaped()}",
              |  "broker_redirect_uri_registered": true,
              |  "account_mode": "SINGLE",
              |  "authorities": [{
              |    "type": "AAD",
              |    "audience": {
              |      "type": "AzureADMyOrg",
              |      "tenant_id": "${legendValue("LEGEND_ENTRA_TENANT_ID").jsonEscaped()}"
              |    }
              |  }]
              |}
            """.trimMargin(),
        )
    }
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
        assets.srcDir(legendRuntimeAssets.get().asFile)
        res.srcDir(legendRuntimeRes.get().asFile)
    }
}

tasks.named("preBuild").configure {
    dependsOn(generateLegendRuntimeConfiguration)
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

    testImplementation(libs.junit)
    androidTestImplementation(libs.androidx.junit)
    androidTestImplementation(libs.espresso.core)
    androidTestImplementation(platform(libs.compose.bom))
    androidTestImplementation(libs.compose.ui.test.junit4)
    debugImplementation(libs.compose.ui.tooling)
    debugImplementation(libs.compose.ui.test.manifest)
}
