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
    @GET("api/v1/mobile/agent/clients") suspend fun agentClients(@Header("X-Legend-Participant-Type") participantType: String): Response<List<MobileAgentClient>>
    @GET("api/v1/mobile/agent/leads") suspend fun agentLeads(@Header("X-Legend-Participant-Type") participantType: String): Response<List<MobileAgentLead>>
    @GET("api/v1/mobile/account") suspend fun account(@Header("X-Legend-Participant-Type") participantType: String): Response<MobileAccountProfile>
    @PUT("api/v1/mobile/account") suspend fun updateAccount(@Header("X-Legend-Participant-Type") participantType: String, @Body request: AccountUpdateRequest): Response<MobileAccountProfile>
    @PUT("api/v1/mobile/account/privacy") suspend fun updatePrivacy(@Header("X-Legend-Participant-Type") participantType: String, @Body request: AccountPrivacyUpdateRequest): Response<MobileAccountProfile>
    @PUT("api/v1/mobile/account/translation-learning-consent") suspend fun updateTranslationLearningConsent(@Header("X-Legend-Participant-Type") participantType: String, @Body request: TranslationLearningConsentUpdateRequest): Response<MobileAccountProfile>
    @PUT("api/v1/mobile/account/avatar") suspend fun updateAvatar(@Header("X-Legend-Participant-Type") participantType: String, @Body request: AccountAvatarUpdateRequest): Response<MobileAccountProfile>
    @GET("api/v1/mobile/account/username-availability") suspend fun usernameAvailability(@Header("X-Legend-Participant-Type") participantType: String, @Query("username") username: String?): Response<MobileUsernameAvailability>
    @GET("api/v1/mobile/account/lifecycle") suspend fun lifecycle(@Header("X-Legend-Participant-Type") participantType: String): Response<AccountLifecycle>
    @POST("api/v1/mobile/account/lifecycle/pause") suspend fun pauseAccount(@Header("X-Legend-Participant-Type") participantType: String, @Body request: ConfirmationRequest): Response<AccountLifecycle>
    @POST("api/v1/mobile/account/lifecycle/resume") suspend fun resumeAccount(@Header("X-Legend-Participant-Type") participantType: String): Response<AccountLifecycle>
    @POST("api/v1/mobile/account/lifecycle/deletion-request") suspend fun deletionRequest(@Header("X-Legend-Participant-Type") participantType: String, @Body request: ConfirmationRequest): Response<AccountLifecycle>
    @GET("api/v1/mobile/founder/accounts") suspend fun founderAccounts(@Header("X-Legend-Participant-Type") participantType: String, @Query("search") search: String? = null, @Query("take") take: Int = 50, @Query("scope") scope: String? = null): Response<List<FounderManagedAccount>>
    @POST("api/v1/mobile/founder/accounts/remove") suspend fun removeFounderAccount(@Header("X-Legend-Participant-Type") participantType: String, @Body request: FounderAccountRemovalRequest): Response<FounderAccountRemovalResponse>
    @POST("api/v1/mobile/founder/accounts/remove-batch") suspend fun removeFounderAccounts(@Header("X-Legend-Participant-Type") participantType: String, @Body request: FounderAccountBatchRequest): Response<FounderAccountBatchResponse>
    @POST("api/v1/mobile/founder/accounts/archive/purge") suspend fun purgeFounderAccounts(@Header("X-Legend-Participant-Type") participantType: String, @Body request: FounderAccountBatchRequest): Response<FounderAccountBatchResponse>
    @GET("api/v1/mobile/daily-scripture/management") suspend fun dailyScriptureManagement(@Header("X-Legend-Participant-Type") participantType: String): Response<DailyScriptureManagementSnapshot>
    @POST("api/v1/mobile/daily-scripture/overrides") suspend fun createDailyScriptureOverride(@Header("X-Legend-Participant-Type") participantType: String, @Body request: DailyScriptureOverrideRequest): Response<DailyScriptureOverride>
    @PUT("api/v1/mobile/daily-scripture/overrides/{id}") suspend fun updateDailyScriptureOverride(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String, @Body request: DailyScriptureOverrideRequest): Response<DailyScriptureOverride>
    @DELETE("api/v1/mobile/daily-scripture/overrides/{id}") suspend fun deleteDailyScriptureOverride(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String): Response<Unit>

    @GET("api/v1/mobile/messaging/conversations") suspend fun conversations(@Header("X-Legend-Participant-Type") participantType: String, @Query("take") take: Int = 24, @Query("skip") skip: Int = 0): Response<List<ConversationSummary>>
    @GET("api/v1/mobile/messaging/recipients") suspend fun recipients(@Header("X-Legend-Participant-Type") participantType: String, @Query("search") search: String? = null, @Query("scope") scope: String? = null): Response<List<MessagingRecipient>>
    @GET("api/v1/mobile/messaging/conversations/{id}") suspend fun conversation(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String, @Query("beforeUtc") beforeUtc: String? = null, @Query("take") take: Int = 60): Response<ConversationDetail>
    @GET("api/v1/mobile/messaging/conversations/{id}/messages") suspend fun messages(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String, @Query("beforeUtc") beforeUtc: String? = null, @Query("take") take: Int = 60): Response<List<ConversationMessage>>
    @POST("api/v1/mobile/messaging/conversations/{id}/messages") suspend fun sendMessage(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String, @Body request: SendMessageRequest): Response<ConversationMessage>
    @Multipart @POST("api/v1/mobile/messaging/conversations/{conversationId}/messages/{messageId}/attachments") suspend fun uploadMessageAttachment(@Header("X-Legend-Participant-Type") participantType: String, @Path("conversationId") conversationId: String, @Path("messageId") messageId: String, @Part file: MultipartBody.Part): Response<MessageAttachment>
    @POST("api/v1/mobile/messaging/conversations/{id}/read") suspend fun markRead(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String): Response<Unit>
    @POST("api/v1/mobile/messaging/conversations") suspend fun startConversation(@Header("X-Legend-Participant-Type") participantType: String, @Body request: StartConversationRequest): Response<ConversationDetail>
    @POST("api/v1/mobile/messaging/groups") suspend fun createMessagingGroup(@Header("X-Legend-Participant-Type") participantType: String, @Body request: CreateMessagingGroupRequest): Response<ConversationDetail>
    @PUT("api/v1/mobile/messaging/groups/{id}") suspend fun updateMessagingGroup(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String, @Body request: UpdateMessagingGroupRequest): Response<Unit>
    @POST("api/v1/mobile/messaging/conversations/{id}/participants") suspend fun addGroupParticipant(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String, @Body request: MessagingGroupParticipantRequest): Response<Unit>
    @PUT("api/v1/mobile/messaging/groups/{id}/collaborators") suspend fun setGroupCollaborator(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String, @Body request: MessagingGroupCollaboratorRequest): Response<Unit>
    @DELETE("api/v1/mobile/messaging/groups/{id}") suspend fun deleteMessagingGroup(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String): Response<Unit>
    @PUT("api/v1/mobile/messaging/groups/{id}/promotion") suspend fun setGroupPromotion(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String, @Body request: MessagingGroupPromotionRequest): Response<ConversationDetail>
    @PUT("api/v1/mobile/messaging/conversations/{id}/pin") suspend fun setConversationPinned(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String, @Body request: ConversationPinnedRequest): Response<Unit>
    @PUT("api/v1/mobile/messaging/conversations/{id}/mute") suspend fun setConversationMuted(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String, @Body request: ConversationMutedRequest): Response<Unit>
    @DELETE("api/v1/mobile/messaging/conversations/{id}") suspend fun removeConversation(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String): Response<Unit>
    @DELETE("api/v1/mobile/messaging/conversations/{conversationId}/messages/{messageId}") suspend fun deleteMessage(@Header("X-Legend-Participant-Type") participantType: String, @Path("conversationId") conversationId: String, @Path("messageId") messageId: String): Response<Unit>
    @GET("api/v1/mobile/messaging/conversations/{id}/call-options") suspend fun conversationCallOptions(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String): Response<ConversationCallOptions>
    @POST("api/v1/mobile/messaging/groups/{id}/join") suspend fun joinPromotedGroup(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String): Response<ConversationDetail>
    @POST("api/v1/mobile/messaging/verification-requests") suspend fun requestVerification(@Header("X-Legend-Participant-Type") participantType: String): Response<MessagingVerificationRequest>
    @POST("api/v1/mobile/messaging/verification-requests/{id}/resolution") suspend fun resolveVerification(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String, @Body request: VerificationResolutionRequest): Response<Unit>
    @POST("api/v1/mobile/messaging/controlled-resources/{resourceType}/requests") suspend fun requestControlledResource(@Header("X-Legend-Participant-Type") participantType: String, @Path("resourceType") resourceType: String): Response<MessagingVerificationRequest>
    @POST("api/v1/mobile/messaging/controlled-resource-requests/{id}/resolution") suspend fun resolveControlledResourceRequest(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String, @Body request: VerificationResolutionRequest): Response<Unit>
    @GET("api/v1/mobile/messaging/controlled-resources/{resourceType}/recipients") suspend fun controlledResourceRecipients(@Header("X-Legend-Participant-Type") participantType: String, @Path("resourceType") resourceType: String, @Query("search") search: String? = null): Response<List<MessagingRecipient>>
    @PUT("api/v1/mobile/messaging/controlled-resources/{resourceType}/recipients") suspend fun setControlledResourceGrant(@Header("X-Legend-Participant-Type") participantType: String, @Path("resourceType") resourceType: String, @Body request: ControlledResourceGrantRequest): Response<Unit>
    @GET("api/v1/mobile/messaging/activity") suspend fun messagingActivity(@Header("X-Legend-Participant-Type") participantType: String, @Query("take") take: Int = 50): Response<List<MessagingActivityNotification>>
    @GET("api/v1/mobile/messaging/controlled-resources/languages") suspend fun communicationLanguages(@Header("X-Legend-Participant-Type") participantType: String): Response<List<CommunicationLanguage>>

    @GET("api/v1/mobile/social/feed") suspend fun socialFeed(@Header("X-Legend-Participant-Type") participantType: String): Response<SocialSnapshot>
    @GET("api/v1/mobile/social/profile/posts") suspend fun currentProfilePosts(@Header("X-Legend-Participant-Type") participantType: String): Response<List<SocialPost>>
    @GET("api/v1/mobile/social/profiles/posts") suspend fun publicProfilePosts(@Header("X-Legend-Participant-Type") participantType: String, @Query("userId") userId: String, @Query("participantType") profileParticipantType: String, @Query("profileId") profileId: String? = null): Response<List<SocialPost>>
    @GET("api/v1/mobile/social/profile/follows") suspend fun profileFollows(@Header("X-Legend-Participant-Type") participantType: String, @Query("list") list: String): Response<List<SocialFollowListEntry>>
    @GET("api/v1/mobile/social/profile/follow-requests") suspend fun incomingFollowRequests(@Header("X-Legend-Participant-Type") participantType: String): Response<List<SocialFollowRequestItem>>
    @POST("api/v1/mobile/social/posts") suspend fun createPost(@Header("X-Legend-Participant-Type") participantType: String, @Body request: CreateSocialPostRequest): Response<SocialPost>
    @PUT("api/v1/mobile/social/posts/{id}") suspend fun updatePost(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String, @Body request: UpdateSocialPostRequest): Response<SocialPost>
    @DELETE("api/v1/mobile/social/posts/{id}") suspend fun deletePost(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String): Response<Unit>
    @Multipart @POST("api/v1/mobile/social/posts/media") suspend fun createMediaPost(
        @Header("X-Legend-Participant-Type") participantType: String,
        @Part files: List<MultipartBody.Part>,
        @Part preview: MultipartBody.Part? = null,
        @Part("contentType") contentType: RequestBody,
        @Part("body") body: RequestBody,
        @Part("audience") audience: RequestBody,
        @Part("location") location: RequestBody? = null,
        @Part("commentsEnabled") commentsEnabled: RequestBody,
        @Part("accessibilityText") accessibilityText: RequestBody? = null,
        @Part("musicProviderId") musicProviderId: RequestBody? = null,
        @Part("musicTrackId") musicTrackId: RequestBody? = null,
        @Part("musicTrimStartSeconds") musicTrimStartSeconds: RequestBody? = null,
        @Part("musicTrimEndSeconds") musicTrimEndSeconds: RequestBody? = null,
        @Part("musicVolume") musicVolume: RequestBody? = null,
        @Part("originalAudioVolume") originalAudioVolume: RequestBody? = null,
    ): Response<SocialPost>
    @POST("api/v1/mobile/social/posts/{id}/reaction") suspend fun react(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String, @Body request: EmptyRequest = EmptyRequest()): Response<SocialPost>
    @POST("api/v1/mobile/social/posts/{id}/comments") suspend fun comment(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String, @Body request: CreateSocialCommentRequest): Response<SocialComment>
    @POST("api/v1/mobile/social/follows/toggle") suspend fun toggleFollow(@Header("X-Legend-Participant-Type") participantType: String, @Body request: SocialFollowRequest): Response<SocialFollowResult>
    @POST("api/v1/mobile/social/profile/follow-requests/{id}/decision") suspend fun decideFollowRequest(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String, @Body request: FollowRequestDecision): Response<SocialFollowResult>
    @POST("api/v1/mobile/social/posts/{id}/save") suspend fun savePost(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String, @Body request: EmptyRequest = EmptyRequest()): Response<SocialStateResult>
    @POST("api/v1/mobile/social/posts/{id}/repost") suspend fun repost(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String, @Body request: EmptyRequest = EmptyRequest()): Response<SocialStateResult>
    @POST("api/v1/mobile/social/posts/{id}/share") suspend fun recordShare(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String, @Body request: EmptyRequest = EmptyRequest()): Response<SocialStateResult>
    @POST("api/v1/mobile/social/posts/{id}/view") suspend fun recordView(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String, @Body request: SocialViewRequest): Response<SocialPostMetrics>
    @GET("api/v1/mobile/social/insights/creator") suspend fun creatorInsights(@Header("X-Legend-Participant-Type") participantType: String): Response<CreatorInsights>
    @GET("api/v1/mobile/social/posts/{id}/insights") suspend fun postInsights(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String): Response<SocialPostInsight>
    @GET("api/v1/mobile/social/profiles/metrics") suspend fun profileMetrics(@Header("X-Legend-Participant-Type") participantType: String, @Query("userId") userId: String? = null, @Query("participantType") profileParticipantType: String? = null, @Query("profileId") profileId: String? = null): Response<SocialProfileMetrics>
    @POST("api/v1/mobile/social/profiles/visit") suspend fun recordProfileVisit(@Header("X-Legend-Participant-Type") participantType: String, @Body request: SocialProfileVisitRequest): Response<SocialStateResult>
    @GET("api/v1/mobile/notifications") suspend fun notifications(@Header("X-Legend-Participant-Type") participantType: String): Response<NotificationSnapshot>
    @POST("api/v1/mobile/notifications/{id}/read") suspend fun markNotificationRead(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String): Response<NotificationBadge>
    @POST("api/v1/mobile/notifications/clear-badges") suspend fun clearNotificationBadges(@Header("X-Legend-Participant-Type") participantType: String): Response<NotificationBadge>
    @PUT("api/v1/mobile/notifications/devices/fcm") suspend fun registerFcmDevice(@Header("X-Legend-Participant-Type") participantType: String, @Body request: FcmDeviceTokenRequest): Response<NotificationBadge>
    @HTTP(method = "DELETE", path = "api/v1/mobile/notifications/devices/fcm", hasBody = true) suspend fun deactivateFcmDevice(@Header("X-Legend-Participant-Type") participantType: String, @Body request: FcmDeviceTokenRequest): Response<Unit>
    @GET("api/v1/mobile/journey-circles") suspend fun journeyCircles(@Header("X-Legend-Participant-Type") participantType: String): Response<JourneyDashboard>
    @PUT("api/v1/mobile/journey-circles/profile") suspend fun saveJourneyProfile(@Header("X-Legend-Participant-Type") participantType: String, @Body request: JourneyProfileInput): Response<Unit>
    @POST("api/v1/mobile/journey-circles/connections") suspend fun requestJourneyConnection(@Header("X-Legend-Participant-Type") participantType: String, @Body request: JourneyConnectionRequest): Response<Unit>
    @POST("api/v1/mobile/journey-circles/connections/{id}/response") suspend fun respondJourneyConnection(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String, @Body request: JourneyConnectionResponse): Response<Unit>
    @POST("api/v1/mobile/journey-circles/connections/{id}/disconnect") suspend fun disconnectJourneyConnection(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String): Response<Unit>
    @POST("api/v1/mobile/journey-circles/profiles/{id}/block") suspend fun blockJourneyProfile(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String): Response<Unit>
    @POST("api/v1/mobile/journey-circles/profiles/{id}/report") suspend fun reportJourneyProfile(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String, @Body request: JourneyProfileReportRequest): Response<Unit>
    @GET("api/v1/mobile/discovery/search") suspend fun discovery(@Header("X-Legend-Participant-Type") participantType: String, @Query("query") query: String? = null, @Query("offset") offset: Int = 0, @Query("pageSize") pageSize: Int = 24, @Query("sort") sort: String? = null): Response<DiscoveryPage>
    @GET("api/v1/mobile/discovery/profiles/{id}") suspend fun discoveryProfile(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String): Response<DiscoveryProfile>
    @POST("api/v1/mobile/community-safety/blocks") suspend fun block(@Header("X-Legend-Participant-Type") participantType: String, @Body request: CommunityBlockRequest): Response<Unit>
    @POST("api/v1/mobile/community-safety/reports") suspend fun report(@Header("X-Legend-Participant-Type") participantType: String, @Body request: CommunityReportRequest): Response<JsonObject>
    @GET("api/v1/mobile/community-safety/reports") suspend fun openCommunityReports(@Header("X-Legend-Participant-Type") participantType: String, @Query("take") take: Int = 100): Response<List<CommunitySafetyReport>>
    @POST("api/v1/mobile/community-safety/reports/{id}/resolution") suspend fun resolveCommunityReport(@Header("X-Legend-Participant-Type") participantType: String, @Path("id") id: String, @Body request: CommunitySafetyReportResolutionRequest): Response<Unit>
}

@kotlinx.serialization.Serializable data class EmptyRequest(val value: String? = null)
@kotlinx.serialization.Serializable data class FcmDeviceTokenRequest(val deviceToken: String)
@kotlinx.serialization.Serializable data class SocialViewRequest(val watchDurationSeconds: Double? = null, val watchCompletionPercentage: Double? = null, val storyInteractionType: String? = null)
@kotlinx.serialization.Serializable data class SocialProfileVisitRequest(val targetUserId: String, val targetParticipantType: String, val sourcePostId: String? = null)
@kotlinx.serialization.Serializable data class NotificationSnapshot(val badge: NotificationBadge, val notifications: List<NotificationItem> = emptyList())
@kotlinx.serialization.Serializable data class NotificationBadge(val unreadCount: Int, val revision: Long, val updatedUtc: String)
@kotlinx.serialization.Serializable data class NotificationItem(val id: String, val kind: String, val title: String, val detail: String, val conversationId: String? = null, val occurredUtc: String, val isRead: Boolean, val isCleared: Boolean)
@kotlinx.serialization.Serializable data class JourneyDashboard(val profile: JourneyProfile? = null, val preferences: JourneyPreferences? = null, val recommendations: List<JourneyRecommendation> = emptyList(), val connections: List<JourneyConnection> = emptyList(), val requests: List<JourneyConnection> = emptyList(), val taxonomy: JourneyTaxonomy = JourneyTaxonomy())
@kotlinx.serialization.Serializable data class JourneyProfile(val clientProfileId: String, val displayName: String, val introduction: String? = null, val lifeStages: List<String> = emptyList(), val locations: List<String> = emptyList(), val goals: List<String> = emptyList(), val interests: List<String> = emptyList(), val circleCodes: List<String> = emptyList(), val connectionTypes: List<String> = emptyList(), val communicationStyles: List<String> = emptyList(), val accountabilityFrequencies: List<String> = emptyList(), val avatar: MobileAvatar? = null)
@kotlinx.serialization.Serializable data class JourneyPreferences(val consentAffirmed: Boolean, val isOptedIn: Boolean, val isDiscoverable: Boolean, val allowSuggestions: Boolean, val allowConnectionRequests: Boolean)
@kotlinx.serialization.Serializable data class JourneyRecommendation(val profile: JourneyProfile, val explanation: String)
@kotlinx.serialization.Serializable data class JourneyConnection(val id: String, val profile: JourneyProfile, val status: String, val connectionReason: String? = null, val introduction: String? = null, val createdUtc: String)
@kotlinx.serialization.Serializable data class JourneyConnectionRequest(val targetClientProfileId: String, val connectionReason: String? = null, val introduction: String? = null)
@kotlinx.serialization.Serializable data class JourneyConnectionResponse(val accept: Boolean)
@kotlinx.serialization.Serializable data class JourneyProfileReportRequest(val category: String, val detail: String? = null)
@kotlinx.serialization.Serializable data class JourneyTaxonomy(val goals: List<String> = emptyList(), val circles: List<String> = emptyList(), val lifeStages: List<String> = emptyList(), val locations: List<String> = emptyList(), val interests: List<String> = emptyList(), val connectionTypes: List<String> = emptyList(), val communicationStyles: List<String> = emptyList(), val accountabilityFrequencies: List<String> = emptyList())
@kotlinx.serialization.Serializable data class JourneyProfileInput(val consentAffirmed: Boolean, val isOptedIn: Boolean, val isDiscoverable: Boolean, val allowSuggestions: Boolean, val allowConnectionRequests: Boolean, val introduction: String? = null, val lifeStages: List<String> = emptyList(), val locations: List<String> = emptyList(), val goals: List<String> = emptyList(), val interests: List<String> = emptyList(), val circleCodes: List<String> = emptyList(), val connectionTypes: List<String> = emptyList(), val communicationStyles: List<String> = emptyList(), val accountabilityFrequencies: List<String> = emptyList())
@kotlinx.serialization.Serializable data class DiscoveryPage(val results: List<DiscoveryResult> = emptyList(), val totalCount: Int, val offset: Int, val pageSize: Int, val hasMore: Boolean, val sortMode: String, val scope: String)
@kotlinx.serialization.Serializable data class DiscoveryResult(val clientProfileId: String, val identity: MobileIdentity, val displayName: String, val headline: String? = null, val location: String? = null, val goals: List<String> = emptyList(), val interests: List<String> = emptyList(), val circleCodes: List<String> = emptyList(), val compatibilityScore: Int, val matchExplanation: String? = null, val relationship: DiscoveryRelationship, val avatar: MobileAvatar? = null, val username: String? = null, val bio: String? = null, val website: String? = null, val publicEmail: String? = null, val publicPhone: String? = null, val isPrivate: Boolean = false, val isVerified: Boolean = false, val roleLabel: String? = null)
@kotlinx.serialization.Serializable data class DiscoveryRelationship(val followedByCurrentActor: Boolean, val followRequestPending: Boolean, val followsCurrentActor: Boolean, val connectionStatus: String, val connectionId: String? = null, val canRequestConnection: Boolean, val canFollow: Boolean)
@kotlinx.serialization.Serializable data class DiscoveryProfile(val summary: DiscoveryResult, val introduction: String? = null, val lifeStages: List<String> = emptyList(), val connectionTypes: List<String> = emptyList(), val contentVisibleToCurrentActor: Boolean, val followerCount: Int, val followingCount: Int, val postCount: Int, val reelCount: Int, val storyCount: Int)
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
