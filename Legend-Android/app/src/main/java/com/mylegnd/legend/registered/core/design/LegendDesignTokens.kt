package com.mylegnd.legend.registered.core.design

import android.content.Context
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/**
 * Compose's native mapping of the single platform-neutral LEGEND® token file.
 * No visual value in this file is an independent Android design decision.
 */
object LegendDesignAuthority {
    private var specification: LegendDesignSpecification? = null
    private val decoder = Json { ignoreUnknownKeys = true }

    fun initialize(context: Context) {
        if (specification != null) return
        synchronized(this) {
            if (specification == null) {
                val source = context.assets.open("legend-design.tokens.json")
                    .bufferedReader()
                    .use { it.readText() }
                specification = decoder.decodeFromString(LegendDesignSpecification.serializer(), source)
            }
        }
    }

    internal fun color(name: String): Color = required().colors.required(name).asColor()
    internal fun semanticColor(name: String): Color = required().platformSemanticColors.required(name).android.asColor()
    internal fun spacing(name: String) = required().spacing.required(name).dp
    internal fun radius(name: String) = required().radii.required(name).dp
    internal fun size(name: String) = required().sizes.required(name).dp
    internal fun socialFormat(name: String) = required().socialFormats.required(name)
    internal fun accountSession() = required().accountSession
    internal fun navigation() = required().navigation
    internal fun gradient(name: String): Brush = Brush.linearGradient(
        required().gradients.required(name).map(::color),
    )
    internal fun opacity(name: String): Float = required().opacity.required(name)
    internal fun typography(name: String): TextStyle {
        val token = required().typography.required(name)
        return TextStyle(
            fontSize = token.size.sp,
            fontWeight = token.weight.asFontWeight(),
            letterSpacing = (token.tracking ?: 0f).sp,
        )
    }
    internal fun copy(key: String): String = required().copy.required(key)

    private fun required(): LegendDesignSpecification = checkNotNull(specification) {
        "LEGEND design authority must be initialized before Compose renders."
    }
}

object LegendColors {
    val Midnight get() = LegendDesignAuthority.color("midnight")
    val Navy get() = LegendDesignAuthority.color("navy")
    val NavyElevated get() = LegendDesignAuthority.color("navyElevated")
    val Royal get() = LegendDesignAuthority.color("royal")
    val AiResponseRoyal get() = LegendDesignAuthority.color("aiResponseRoyal")
    val Gold get() = LegendDesignAuthority.color("gold")
    val GoldBright get() = LegendDesignAuthority.color("goldBright")
    val GoldSoft get() = LegendDesignAuthority.color("goldSoft")
    val OnNavy get() = LegendDesignAuthority.color("onNavy")
    val OnGold get() = LegendDesignAuthority.color("onGold")
    val Canvas get() = LegendDesignAuthority.color("canvas")
    val CanvasSecondary get() = LegendDesignAuthority.color("canvasSecondary")
    val Surface get() = LegendDesignAuthority.color("surface")
    val SurfaceElevated get() = LegendDesignAuthority.color("surfaceElevated")
    val SurfaceInset get() = LegendDesignAuthority.color("surfaceInset")
    val BrandBlueSurface get() = LegendDesignAuthority.color("brandBlueSurface")
    val BrandBlueInset get() = LegendDesignAuthority.color("brandBlueInset")
    val ContactNavy get() = LegendDesignAuthority.color("contactNavy")
    val ContactConnected get() = LegendDesignAuthority.color("contactConnected")
    val Verified get() = LegendDesignAuthority.color("verified")
    val TextPrimary get() = LegendDesignAuthority.color("textPrimary")
    val TextSecondary get() = LegendDesignAuthority.color("textSecondary")
    val TextTertiary get() = LegendDesignAuthority.color("textTertiary")
    val Divider get() = LegendDesignAuthority.color("separator")
    val Success get() = LegendDesignAuthority.semanticColor("success")
    val Warning get() = LegendDesignAuthority.semanticColor("warning")
    val Error get() = LegendDesignAuthority.semanticColor("danger")
    val Info get() = LegendDesignAuthority.semanticColor("information")
}

/** Canonical gradients from the shared iOS-authored design specification. */
object LegendGradients {
    val Hero get() = LegendDesignAuthority.gradient("hero")
    val Gold get() = LegendDesignAuthority.gradient("gold")
    val Finance get() = LegendDesignAuthority.gradient("finance")
    val FinancialSheet get() = LegendDesignAuthority.gradient("financialSheet")
    val PageWashDark get() = LegendDesignAuthority.gradient("pageWashDark")
}

/** Named opacity values keep cross-platform component treatments in lockstep. */
object LegendOpacity {
    val ContactBorder get() = LegendDesignAuthority.opacity("contactBorder")
    val ContactSupporting get() = LegendDesignAuthority.opacity("contactSupporting")
    val ContactDetail get() = LegendDesignAuthority.opacity("contactDetail")
    val ContactAction get() = LegendDesignAuthority.opacity("contactAction")
}

object LegendSpacing {
    val Hairline get() = LegendDesignAuthority.spacing("hairline")
    val Micro get() = LegendDesignAuthority.spacing("micro")
    val Tiny get() = LegendDesignAuthority.spacing("tiny")
    val Xs get() = LegendDesignAuthority.spacing("xs")
    val Sm get() = LegendDesignAuthority.spacing("sm")
    val Md get() = LegendDesignAuthority.spacing("md")
    val Intermediate get() = LegendDesignAuthority.spacing("intermediate")
    val Lg get() = LegendDesignAuthority.spacing("lg")
    val Xl get() = LegendDesignAuthority.spacing("xl")
    val Xxl get() = LegendDesignAuthority.spacing("xxl")
    val PageHorizontal get() = LegendDesignAuthority.spacing("pageHorizontal")
    val PageTop get() = LegendDesignAuthority.spacing("pageTop")
    val PageBottom get() = LegendDesignAuthority.spacing("pageBottom")
    val CardContent get() = LegendDesignAuthority.spacing("cardContent")
}

object LegendShapes {
    val Compact get() = RoundedCornerShape(LegendDesignAuthority.radius("compact"))
    val Control get() = RoundedCornerShape(LegendDesignAuthority.radius("control"))
    val Card get() = RoundedCornerShape(LegendDesignAuthority.radius("card"))
    val ProminentCard get() = RoundedCornerShape(LegendDesignAuthority.radius("prominentCard"))
    val Hero get() = RoundedCornerShape(LegendDesignAuthority.radius("hero"))
}

object LegendSize {
    val MinimumTapTarget get() = LegendDesignAuthority.size("minimumTapTarget")
    val CompactControlHeight get() = LegendDesignAuthority.size("compactControlHeight")
    val ControlHeight get() = LegendDesignAuthority.size("controlHeight")
    val ProminentControlHeight get() = LegendDesignAuthority.size("prominentControlHeight")
    val AvatarSmall get() = LegendDesignAuthority.size("avatarSmall")
    val AvatarMedium get() = LegendDesignAuthority.size("avatarMedium")
    val AvatarLarge get() = LegendDesignAuthority.size("avatarLarge")
    val AvatarHero get() = LegendDesignAuthority.size("avatarHero")
    val ProfileAvatar get() = LegendDesignAuthority.size("profileAvatar")
    val ProfileAvatarCamera get() = LegendDesignAuthority.size("profileAvatarCamera")
    val ProfileSettingsIcon get() = LegendDesignAuthority.size("profileSettingsIcon")
    val ProfileControlHeight get() = LegendDesignAuthority.size("profileControlHeight")
    val HacAction get() = LegendDesignAuthority.size("hacActionSize")
}

internal object LegendTypography {
    val Display get() = LegendDesignAuthority.typography("display")
    val Wordmark get() = LegendDesignAuthority.typography("wordmark")
    val Hero get() = LegendDesignAuthority.typography("hero")
    val Title get() = LegendDesignAuthority.typography("title")
    val Section get() = LegendDesignAuthority.typography("section")
    val CardTitle get() = LegendDesignAuthority.typography("cardTitle")
    val Body get() = LegendDesignAuthority.typography("body")
    val BodyEmphasis get() = LegendDesignAuthority.typography("bodyEmphasis")
    val Supporting get() = LegendDesignAuthority.typography("supporting")
    val Caption get() = LegendDesignAuthority.typography("caption")
    val Label get() = LegendDesignAuthority.typography("label")
    val Eyebrow get() = LegendDesignAuthority.typography("eyebrow")
}

@Serializable
private data class LegendDesignSpecification(
    val colors: Map<String, LegendColorToken>,
    val platformSemanticColors: Map<String, LegendPlatformSemanticColor>,
    val gradients: Map<String, List<String>>,
    val opacity: Map<String, Float>,
    val spacing: Map<String, Float>,
    val radii: Map<String, Float>,
    val sizes: Map<String, Float>,
    val typography: Map<String, LegendTypographyToken>,
    val socialFormats: Map<String, LegendSocialFormatToken>,
    val navigation: LegendNavigationToken,
    val accountSession: LegendAccountSessionToken,
    val copy: Map<String, String>,
)

object LegendCopy {
    fun value(key: String): String = LegendDesignAuthority.copy(key)
}

/** The shared iOS-authored account-retention authority. */
internal object LegendAccountSessionPolicy {
    val InteractiveSignInRetentionDays get() = LegendDesignAuthority.accountSession().interactiveSignInRetentionDays
    val ProfileDoubleTapCyclesAccount get() = LegendDesignAuthority.accountSession().profileDoubleTapCyclesAccount
    val AllowsAdditionalSignedInAccounts get() = LegendDesignAuthority.accountSession().allowsAdditionalSignedInAccounts
}

/**
 * The shared iOS-authored primary-navigation contract. Compose owns only the
 * native renderer; tab order, role visibility, surface behavior, and brand
 * identity come from the same platform-neutral authority as iOS and web.
 */
internal object LegendNavigationPolicy {
    val Tabs get() = LegendDesignAuthority.navigation().tabs
    val AgentOnlyTab get() = LegendDesignAuthority.navigation().agentOnlyTab
    val DiscoverUsesNavySurface get() = LegendDesignAuthority.navigation().discoverUsesNavySurface
    val MessagesSuppressesBottomNavigationInThread get() =
        LegendDesignAuthority.navigation().messagesSuppressesBottomNavigationInThread
    val Brand get() = LegendDesignAuthority.navigation().brand
}

/** Platform-neutral social canvas and picker rules extracted from iOS. */
internal object LegendSocialFormats {
    fun named(name: String): LegendSocialFormatToken = LegendDesignAuthority.socialFormat(name)
}

@Serializable
private data class LegendColorToken(
    val light: String,
    val dark: String? = null,
    val lightOpacity: Float? = null,
    val darkOpacity: Float? = null,
) {
    fun asColor(): Color = light.asColor(lightOpacity ?: 1f)
}

@Serializable
private data class LegendPlatformSemanticColor(val android: String)

@Serializable
private data class LegendTypographyToken(
    val size: Float,
    val weight: String,
    val tracking: Float? = null,
)

@Serializable
internal data class LegendSocialFormatToken(
    val maximumMediaItems: Int,
    val allowsTextOnlyPublication: Boolean,
    val acceptsImages: Boolean,
    val acceptsVideos: Boolean,
    val maximumVideoDurationSeconds: Double? = null,
    val mediaAspectRatio: Double,
    val selectionThumbnailSide: Double,
    val emptyPreviewHeight: Double,
    val editorMaximumWidth: Double,
    val usesFixedCanvasAspectRatio: Boolean,
    val supportedCanvasAspectRatios: List<Double>,
)

@Serializable
internal data class LegendAccountSessionToken(
    val interactiveSignInRetentionDays: Int,
    val profileDoubleTapCyclesAccount: Boolean,
    val allowsAdditionalSignedInAccounts: Boolean,
)

@Serializable
internal data class LegendNavigationToken(
    val tabs: List<String>,
    val agentOnlyTab: String,
    val discoverUsesNavySurface: Boolean,
    val messagesSuppressesBottomNavigationInThread: Boolean,
    val brand: String,
)

private fun String.asColor(alpha: Float = 1f): Color {
    val rgb = removePrefix("#").toLongOrNull(16) ?: error("Invalid LEGEND color token.")
    return Color(
        red = ((rgb shr 16) and 0xFF).toFloat() / 255f,
        green = ((rgb shr 8) and 0xFF).toFloat() / 255f,
        blue = (rgb and 0xFF).toFloat() / 255f,
        alpha = alpha,
    )
}

private fun String.asFontWeight(): FontWeight = when (this) {
    "regular" -> FontWeight.Normal
    "medium" -> FontWeight.Medium
    "semibold" -> FontWeight.SemiBold
    "bold" -> FontWeight.Bold
    else -> error("Unsupported LEGEND typography weight.")
}

private fun <T> Map<String, T>.required(name: String): T = get(name)
    ?: error("Missing LEGEND design token: $name")
