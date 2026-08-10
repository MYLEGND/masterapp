package com.mylegnd.legend.registered.core.network

import com.mylegnd.legend.registered.core.model.*
import kotlinx.serialization.json.Json
import okhttp3.Interceptor
import okhttp3.OkHttpClient
import okhttp3.MultipartBody
import okhttp3.RequestBody
import okhttp3.logging.HttpLoggingInterceptor
import retrofit2.Response
import retrofit2.Retrofit
import retrofit2.converter.kotlinx.serialization.asConverterFactory
import retrofit2.http.*
import java.io.IOException
import java.util.UUID
import java.util.concurrent.TimeUnit
import okhttp3.MediaType.Companion.toMediaType
import kotlinx.serialization.json.JsonObject

interface AccessTokenProvider { suspend fun accessToken(): String? }
class LegendApiException(val status: Int, val problem: MobileApiProblem?, cause: Throwable? = null) : IOException(problem?.message ?: "Legend request failed.", cause)

interface LegendApi {
    @GET("api/v1/mobile/session") suspend fun session(@Header("X-Legend-Participant-Type") participantType: String? = null): Response<MobileSessionResponse>
    @POST("api/v1/mobile/session/select-role") suspend fun selectRole(@Body request: SelectRoleRequest): Response<MobileRoleSelectionResponse>
    @GET("api/v1/mobile/home") suspend fun home(@Header("X-Legend-Participant-Type") participantType: String): Response<MobileHomeResponse>
    @GET("api/v1/mobile/financial") suspend fun financial(@Header("X-Legend-Participant-Type") participantType: String): Response<FinancialSnapshot>
    @GET("api/v1/mobile/account") suspend fun account(@Header("X-Legend-Participant-Type") participantType: String): Response<MobileAccountProfile>
    @PUT("api/v1/mobile/account") suspend fun updateAccount(@Header("X-Legend-Participant-Type") participantType: String, @Body request: AccountUpdateRequest): Response<MobileAccountProfile>
    @PUT("api/v1/mobile/account/privacy") suspend fun updatePrivacy(@Header("X-Legend-Participant-Type") participantType: String, @Body request: AccountPrivacyUpdateRequest): Response<MobileAccountProfile>
    @GET("api/v1/mobile/account/lifecycle") suspend fun lifecycle(@Header("X-Legend-Participant-Type") participantType: String): Response<AccountLifecycle>
    @POST("api/v1/mobile/account/lifecycle/pause") suspend fun pauseAccount(@Header("X-Legend-Participant-Type") participantType: String): Response<AccountLifecycle>
    @POST("api/v1/mobile/account/lifecycle/resume") suspend fun resumeAccount(@Header("X-Legend-Participant-Type") participantType: String): Response<AccountLifecycle>
    @POST("api/v1/mobile/account/lifecycle/deletion-request") suspend fun deletionRequest(@Header("X-Legend-Participant-Type") participantType: String, @Body request: ConfirmationRequest): Response<AccountLifecycle>

    @GET("api/v1/mobile/messaging/conversations") suspend fun conversations(@Header("X-Legend-Participant-Type") participantType: String, @Query("take") take: Int = 24, @Query("skip") skip: Int = 0): Response<List<ConversationSummary>>
    @GET("api/v1/mobile/messaging/conversations/{id}/messages") suspend fun messages(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String, @Query("take") take: Int = 60): Response<List<ConversationMessage>>
    @POST("api/v1/mobile/messaging/conversations/{id}/messages") suspend fun sendMessage(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String, @Body request: SendMessageRequest): Response<ConversationMessage>
    @POST("api/v1/mobile/messaging/conversations/{id}/read") suspend fun markRead(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String): Response<Unit>
    @POST("api/v1/mobile/messaging/conversations") suspend fun startConversation(@Header("X-Legend-Participant-Type") participantType: String, @Body request: StartConversationRequest): Response<JsonObject>

    @GET("api/v1/mobile/social/feed") suspend fun socialFeed(@Header("X-Legend-Participant-Type") participantType: String): Response<SocialSnapshot>
    @POST("api/v1/mobile/social/posts") suspend fun createPost(@Header("X-Legend-Participant-Type") participantType: String, @Body request: CreateSocialPostRequest): Response<SocialPost>
    @Multipart @POST("api/v1/mobile/social/posts/media") suspend fun createMediaPost(@Header("X-Legend-Participant-Type") participantType: String, @Part files: List<MultipartBody.Part>, @Part("contentType") contentType: RequestBody, @Part("body") body: RequestBody, @Part("audience") audience: RequestBody, @Part("commentsEnabled") commentsEnabled: RequestBody): Response<SocialPost>
    @POST("api/v1/mobile/social/posts/{id}/reaction") suspend fun react(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String, @Body request: EmptyRequest = EmptyRequest()): Response<SocialPost>
    @POST("api/v1/mobile/social/posts/{id}/comments") suspend fun comment(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String, @Body request: CreateSocialCommentRequest): Response<SocialComment>
    @POST("api/v1/mobile/social/posts/{id}/view") suspend fun recordView(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String, @Body request: SocialViewRequest): Response<JsonObject>

    @GET("api/v1/mobile/notifications") suspend fun notifications(@Header("X-Legend-Participant-Type") participantType: String): Response<NotificationSnapshot>
    @PUT("api/v1/mobile/notifications/devices/fcm") suspend fun registerFcmDevice(@Header("X-Legend-Participant-Type") participantType: String, @Body request: FcmDeviceTokenRequest): Response<NotificationBadge>
    @HTTP(method = "DELETE", path = "api/v1/mobile/notifications/devices/fcm", hasBody = true) suspend fun deactivateFcmDevice(@Header("X-Legend-Participant-Type") participantType: String, @Body request: FcmDeviceTokenRequest): Response<Unit>
    @GET("api/v1/mobile/journey-circles") suspend fun journeyCircles(@Header("X-Legend-Participant-Type") participantType: String): Response<JourneyDashboard>
    @POST("api/v1/mobile/journey-circles/connections") suspend fun requestJourneyConnection(@Header("X-Legend-Participant-Type") participantType: String, @Body request: JourneyConnectionRequest): Response<Unit>
    @GET("api/v1/mobile/discovery/search") suspend fun discovery(@Header("X-Legend-Participant-Type") participantType: String, @Query("query") query: String? = null, @Query("offset") offset: Int = 0, @Query("pageSize") pageSize: Int = 24, @Query("sort") sort: String? = null): Response<DiscoveryPage>
    @POST("api/v1/mobile/community-safety/blocks") suspend fun block(@Header("X-Legend-Participant-Type") participantType: String, @Body request: CommunityBlockRequest): Response<Unit>
    @POST("api/v1/mobile/community-safety/reports") suspend fun report(@Header("X-Legend-Participant-Type") participantType: String, @Body request: CommunityReportRequest): Response<JsonObject>
}

@kotlinx.serialization.Serializable data class EmptyRequest(val value: String? = null)
@kotlinx.serialization.Serializable data class FcmDeviceTokenRequest(val deviceToken: String)
@kotlinx.serialization.Serializable data class SocialViewRequest(val watchDurationSeconds: Double? = null, val watchCompletionPercentage: Double? = null, val storyInteractionType: String? = null)
@kotlinx.serialization.Serializable data class NotificationSnapshot(val badge: NotificationBadge, val notifications: List<NotificationItem> = emptyList())
@kotlinx.serialization.Serializable data class NotificationBadge(val unreadCount: Int, val revision: Long, val updatedUtc: String)
@kotlinx.serialization.Serializable data class NotificationItem(val id: String, val kind: String, val title: String, val detail: String, val conversationId: String? = null, val occurredUtc: String, val isRead: Boolean, val isCleared: Boolean)
@kotlinx.serialization.Serializable data class JourneyDashboard(val profile: JourneyProfile? = null, val preferences: JourneyPreferences? = null, val recommendations: List<JourneyRecommendation> = emptyList(), val connections: List<JourneyConnection> = emptyList(), val requests: List<JourneyConnection> = emptyList())
@kotlinx.serialization.Serializable data class JourneyProfile(val clientProfileId: String, val displayName: String, val introduction: String? = null, val goals: List<String> = emptyList(), val interests: List<String> = emptyList())
@kotlinx.serialization.Serializable data class JourneyPreferences(val consentAffirmed: Boolean, val isOptedIn: Boolean, val isDiscoverable: Boolean, val allowSuggestions: Boolean, val allowConnectionRequests: Boolean)
@kotlinx.serialization.Serializable data class JourneyRecommendation(val profile: JourneyProfile, val explanation: String)
@kotlinx.serialization.Serializable data class JourneyConnection(val id: String, val profile: JourneyProfile, val status: String, val createdUtc: String)
@kotlinx.serialization.Serializable data class JourneyConnectionRequest(val targetClientProfileId: String, val connectionReason: String? = null, val introduction: String? = null)
@kotlinx.serialization.Serializable data class DiscoveryPage(val results: List<DiscoveryResult> = emptyList(), val totalCount: Int, val offset: Int, val pageSize: Int, val hasMore: Boolean, val sortMode: String, val scope: String)
@kotlinx.serialization.Serializable data class DiscoveryResult(val clientProfileId: String, val identity: MobileIdentity, val displayName: String, val headline: String? = null, val location: String? = null, val goals: List<String> = emptyList(), val interests: List<String> = emptyList(), val circleCodes: List<String> = emptyList(), val compatibilityScore: Int, val matchExplanation: String? = null, val relationship: DiscoveryRelationship, val avatar: MobileAvatar? = null, val username: String? = null, val isPrivate: Boolean = false, val isVerified: Boolean = false, val roleLabel: String? = null)
@kotlinx.serialization.Serializable data class DiscoveryRelationship(val followedByCurrentActor: Boolean, val followRequestPending: Boolean, val followsCurrentActor: Boolean, val connectionStatus: String, val connectionId: String? = null, val canRequestConnection: Boolean, val canFollow: Boolean)
@kotlinx.serialization.Serializable data class CommunityBlockRequest(val targetUserId: String, val targetParticipantType: String)
@kotlinx.serialization.Serializable data class CommunityReportRequest(val targetUserId: String, val targetParticipantType: String, val targetKind: String, val targetEntityId: String? = null, val category: String, val detail: String? = null)

class LegendApiClient private constructor(val api: LegendApi, val httpClient: OkHttpClient, val baseUrl: String) {
    companion object {
        fun create(baseUrl: String, tokenProvider: AccessTokenProvider, json: Json = Json { ignoreUnknownKeys = true; explicitNulls = false }): LegendApiClient {
            val auth = Interceptor { chain ->
                val token = kotlinx.coroutines.runBlocking { tokenProvider.accessToken() }
                val request = chain.request().newBuilder().header("Accept", "application/json").header("X-Correlation-ID", UUID.randomUUID().toString()).apply {
                    if (!token.isNullOrBlank()) header("Authorization", "Bearer $token")
                }.build()
                chain.proceed(request)
            }
            val logger = HttpLoggingInterceptor().apply { level = HttpLoggingInterceptor.Level.NONE }
            val client = OkHttpClient.Builder().addInterceptor(auth).addInterceptor(logger).connectTimeout(15, TimeUnit.SECONDS).readTimeout(30, TimeUnit.SECONDS).writeTimeout(60, TimeUnit.SECONDS).build()
            val retrofit = Retrofit.Builder().baseUrl(if (baseUrl.endsWith('/')) baseUrl else "$baseUrl/").client(client).addConverterFactory(json.asConverterFactory("application/json".toMediaType())).build()
            return LegendApiClient(retrofit.create(LegendApi::class.java), client, baseUrl.trimEnd('/'))
        }
    }
}

suspend fun <T> Response<T>.legendBody(json: Json = Json { ignoreUnknownKeys = true }): T {
    body()?.let { return it }
    val problem = errorBody()?.use { body -> body.string() }?.let { runCatching { json.decodeFromString(MobileApiProblem.serializer(), it) }.getOrNull() }
    throw LegendApiException(code(), problem)
}
