package com.mylegnd.legend.registered.core.model

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/** Kotlin transport mirrors of AgentPortal/Mobile. ISO-8601 UTC values remain strings until display. */
@Serializable data class MobileIdentity(
    @SerialName("userId") val userId: String,
    @SerialName("participantType") val participantType: String,
)

@Serializable data class MobileAvatar(
    val kind: String,
    @SerialName("contentType") val contentType: String,
    @SerialName("resourcePath") val resourcePath: String,
)

@Serializable data class MobileActor(
    val identity: MobileIdentity,
    @SerialName("profileId") val profileId: String,
    @SerialName("displayName") val displayName: String,
    val avatar: MobileAvatar? = null,
)

@Serializable data class MobileCapabilities(
    val messaging: Boolean = false,
    val isFounder: Boolean = false,
    val canManageScripture: Boolean = false,
    val canManageCommunity: Boolean = false,
)

@Serializable data class MobileSessionResponse(
    val authenticated: Boolean,
    val actor: MobileActor? = null,
    @SerialName("permittedParticipantTypes") val permittedParticipantTypes: List<String> = emptyList(),
    @SerialName("requiresParticipantSelection") val requiresParticipantSelection: Boolean = false,
    val capabilities: MobileCapabilities,
    @SerialName("correlationId") val correlationId: String,
)

@Serializable data class SelectRoleRequest(@SerialName("participantType") val participantType: String)
@Serializable data class MobileRoleSelectionResponse(
    val actor: MobileActor,
    @SerialName("permittedParticipantTypes") val permittedParticipantTypes: List<String>,
    @SerialName("correlationId") val correlationId: String,
    val capabilities: MobileCapabilities? = null,
)

@Serializable data class MobileHomeResponse(
    val identity: MobileHomeIdentity,
    val messaging: MobileMessagingSummary,
    val journey: MobileJourneySummary? = null,
    @SerialName("upcomingAppointments") val upcomingAppointments: List<MobileUpcomingAppointment> = emptyList(),
    val actions: List<MobileActionItem> = emptyList(),
    @SerialName("dailyScripture") val dailyScripture: MobileDailyScripture,
    @SerialName("activeClientCount") val activeClientCount: Int,
)
@Serializable data class MobileHomeIdentity(@SerialName("userId") val userId: String, @SerialName("participantType") val participantType: String, @SerialName("profileId") val profileId: String, @SerialName("displayName") val displayName: String)
@Serializable data class MobileMessagingSummary(@SerialName("unreadCount") val unreadCount: Int, @SerialName("conversationCount") val conversationCount: Int)
@Serializable data class MobileJourneySummary(@SerialName("hasProfile") val hasProfile: Boolean, @SerialName("recommendationCount") val recommendationCount: Int, @SerialName("connectedPeerCount") val connectedPeerCount: Int, @SerialName("pendingRequestCount") val pendingRequestCount: Int)
@Serializable data class MobileUpcomingAppointment(val id: String, @SerialName("startUtc") val startUtc: String, @SerialName("endUtc") val endUtc: String? = null, val status: String)
@Serializable data class MobileActionItem(val id: String, val title: String, val status: String, val priority: String, @SerialName("dueDateUtc") val dueDateUtc: String? = null)
@Serializable data class MobileDailyScripture(val date: String, val reference: String, val translation: String, val verses: List<String> = emptyList(), val text: String = "", val source: String = "DailyCatalog", @SerialName("passageText") val passageText: String = text)

@Serializable data class FinancialSnapshot(
    val position: FinancialPosition? = null,
    val intelligence: FinancialIntelligence? = null,
    @SerialName("upcomingBills") val upcomingBills: List<UpcomingBill> = emptyList(),
    val presentation: FinancialPresentation? = null,
    @SerialName("healthSnapshot") val healthSnapshot: FinancialHealthSnapshot? = null,
)
@Serializable data class FinancialPosition(@SerialName("cashOnHandCents") val cashOnHandCents: Long? = null, @SerialName("monthlyIncomeCents") val monthlyIncomeCents: Long? = null, @SerialName("monthlyExpenseCents") val monthlyExpenseCents: Long? = null)
@Serializable data class FinancialIntelligence(val summary: String? = null, val status: String? = null)
@Serializable data class UpcomingBill(val id: String, val name: String, @SerialName("dueDate") val dueDate: String? = null, @SerialName("amountCents") val amountCents: Long? = null)
@Serializable data class FinancialPresentation(@SerialName("prioritySections") val prioritySections: List<FinancialPrioritySection> = emptyList())
@Serializable data class FinancialPrioritySection(val key: String, val eyebrow: String, val title: String, val status: String, val reason: String, @SerialName("discussionPrompt") val discussionPrompt: String)
@Serializable data class FinancialHealthSnapshot(@SerialName("updatedUtc") val updatedUtc: String, val sections: List<FinancialHealthSection>)
@Serializable data class FinancialHealthSection(val key: String, val title: String, val semantic: String, val period: String? = null, val groups: List<FinancialHealthGroup> = emptyList())
@Serializable data class FinancialHealthGroup(val key: String, val title: String? = null, val metrics: List<FinancialMetric> = emptyList())
@Serializable data class FinancialMetric(val key: String, val label: String, @SerialName("valueType") val valueType: String, @SerialName("amountCents") val amountCents: Long? = null, @SerialName("numericValue") val numericValue: Double? = null, @SerialName("textValue") val textValue: String? = null, val status: String? = null)

@Serializable data class MobileParticipant(val identity: MobileIdentity, @SerialName("profileId") val profileId: String, @SerialName("displayName") val displayName: String, val avatar: MobileAvatar? = null, val roleLabel: String? = null, val isVerified: Boolean = false)
@Serializable data class ConversationSummary(val id: String, @SerialName("conversationType") val conversationType: String, val title: String, val counterparty: MobileParticipant, @SerialName("lastMessagePreview") val lastMessagePreview: String? = null, @SerialName("lastMessageUtc") val lastMessageUtc: String? = null, @SerialName("unreadCount") val unreadCount: Int, @SerialName("isClosed") val isClosed: Boolean, val purpose: String? = null, @SerialName("groupAvatar") val groupAvatar: MobileAvatar? = null, @SerialName("isPinned") val isPinned: Boolean = false, @SerialName("isMuted") val isMuted: Boolean = false)
@Serializable data class ConversationMessage(val id: String, @SerialName("conversationId") val conversationId: String, val sender: MobileParticipant, val body: String, @SerialName("sentUtc") val sentUtc: String, val attachments: List<MessageAttachment> = emptyList(), @SerialName("isMine") val isMine: Boolean, @SerialName("isDeleted") val isDeleted: Boolean, val translation: MessageTranslation? = null, @SerialName("originalBody") val originalBody: String? = null)
@Serializable data class MessageAttachment(val id: String, @SerialName("originalFileName") val originalFileName: String, @SerialName("contentType") val contentType: String, @SerialName("sizeBytes") val sizeBytes: Long, @SerialName("scanStatus") val scanStatus: String, @SerialName("createdUtc") val createdUtc: String, @SerialName("canDownload") val canDownload: Boolean)
@Serializable data class MessageTranslation(@SerialName("originalLanguage") val originalLanguage: String, @SerialName("targetLanguage") val targetLanguage: String, val provider: String)
@Serializable data class SendMessageRequest(val body: String, @SerialName("replyToMessageId") val replyToMessageId: String? = null)
@Serializable data class StartConversationRequest(@SerialName("targetUserId") val targetUserId: String, @SerialName("targetParticipantType") val targetParticipantType: String, @SerialName("initialMessageBody") val initialMessageBody: String? = null)

@Serializable data class SocialSnapshot(
    val stories: List<SocialPost> = emptyList(), val posts: List<SocialPost> = emptyList(), val hacs: List<SocialPost> = emptyList(),
    val activity: List<SocialActivity> = emptyList(), @SerialName("activityCount") val activityCount: Int = 0,
    @SerialName("currentProfileMetrics") val currentProfileMetrics: SocialProfileMetrics? = null,
    @SerialName("creatorInsights") val creatorInsights: CreatorInsights? = null,
)
@Serializable data class SocialAuthor(val identity: MobileIdentity, @SerialName("profileId") val profileId: String, @SerialName("displayName") val displayName: String, val avatar: MobileAvatar? = null, val username: String? = null, val bio: String? = null, val location: String? = null, val isPrivate: Boolean = false, val isVerified: Boolean = false, val roleLabel: String? = null)
@Serializable data class SocialPost(val id: String, val author: SocialAuthor, @SerialName("contentType") val contentType: String, val body: String, val audience: String, val location: String? = null, @SerialName("commentsEnabled") val commentsEnabled: Boolean, @SerialName("postedUtc") val postedUtc: String, @SerialName("expiresUtc") val expiresUtc: String? = null, @SerialName("reactionCount") val reactionCount: Int, @SerialName("commentCount") val commentCount: Int, @SerialName("reactedByCurrentActor") val reactedByCurrentActor: Boolean, @SerialName("followedByCurrentActor") val followedByCurrentActor: Boolean, @SerialName("followRequestPending") val followRequestPending: Boolean, @SerialName("savedByCurrentActor") val savedByCurrentActor: Boolean, @SerialName("repostedByCurrentActor") val repostedByCurrentActor: Boolean, val media: List<SocialMedia> = emptyList(), val comments: List<SocialComment> = emptyList())
@Serializable data class SocialMedia(val id: String, @SerialName("displayOrder") val displayOrder: Int, @SerialName("mediaKind") val mediaKind: String, @SerialName("mimeType") val mimeType: String, @SerialName("fileSizeBytes") val fileSizeBytes: Long, val width: Int? = null, val height: Int? = null, @SerialName("aspectRatio") val aspectRatio: Double? = null, @SerialName("durationSeconds") val durationSeconds: Double? = null, @SerialName("processingState") val processingState: String, @SerialName("accessibilityText") val accessibilityText: String? = null, @SerialName("hasPreviewImage") val hasPreviewImage: Boolean)
@Serializable data class SocialComment(val id: String, val author: SocialAuthor, @SerialName("parentCommentId") val parentCommentId: String? = null, val body: String, @SerialName("createdUtc") val createdUtc: String)
@Serializable data class SocialActivity(val id: String, val kind: String, val actor: SocialAuthor, @SerialName("postId") val postId: String? = null, @SerialName("occurredUtc") val occurredUtc: String)
@Serializable data class SocialProfileMetrics(val profile: SocialAuthor, @SerialName("postCount") val postCount: Int, @SerialName("videoCount") val videoCount: Int, @SerialName("storyCount") val storyCount: Int, @SerialName("followerCount") val followerCount: Int, @SerialName("followingCount") val followingCount: Int)
@Serializable data class CreatorInsights(@SerialName("totalViews") val totalViews: Int, @SerialName("totalReach") val totalReach: Int, @SerialName("followerCount") val followerCount: Int, @SerialName("engagementRatePercentage") val engagementRatePercentage: Double)
@Serializable data class CreateSocialPostRequest(@SerialName("contentType") val contentType: String, val body: String, val audience: String? = null, val location: String? = null, @SerialName("commentsEnabled") val commentsEnabled: Boolean? = null)
@Serializable data class CreateSocialCommentRequest(val body: String, @SerialName("parentCommentId") val parentCommentId: String? = null)

@Serializable data class MobileAccountProfile(val participantType: String, @SerialName("profileId") val profileId: String, @SerialName("displayName") val displayName: String, val email: String? = null, val phone: String? = null, val title: String? = null, @SerialName("roleLabel") val roleLabel: String? = null, @SerialName("shortBio") val shortBio: String? = null, val username: String? = null, val bio: String? = null, val website: String? = null, val location: String? = null, @SerialName("profileEmail") val profileEmail: String? = null, @SerialName("isEmailVisible") val isEmailVisible: Boolean = false, @SerialName("isPrivate") val isPrivate: Boolean, val avatar: MobileAvatar? = null, @SerialName("isVerified") val isVerified: Boolean, @SerialName("usernameChangesRemaining") val usernameChangesRemaining: Int = 0, @SerialName("isPhoneVisible") val isPhoneVisible: Boolean = false, @SerialName("translationAccess") val translationAccess: TranslationAccess? = null)
@Serializable data class TranslationAccess(val state: String, @SerialName("canManage") val canManage: Boolean, @SerialName("preferredCommunicationLanguage") val preferredCommunicationLanguage: String? = null)
@Serializable data class AccountUpdateRequest(@SerialName("displayName") val displayName: String, val phone: String? = null, val title: String? = null, @SerialName("shortBio") val shortBio: String? = null, val username: String? = null, val bio: String? = null, val website: String? = null, val location: String? = null, @SerialName("publicEmail") val publicEmail: String? = null, @SerialName("isEmailVisible") val isEmailVisible: Boolean = false, @SerialName("isPhoneVisible") val isPhoneVisible: Boolean = false, @SerialName("isPrivate") val isPrivate: Boolean? = null, @SerialName("preferredCommunicationLanguage") val preferredCommunicationLanguage: String? = null)
@Serializable data class AccountPrivacyUpdateRequest(@SerialName("isPrivate") val isPrivate: Boolean)
@Serializable data class AccountLifecycle(val state: String, @SerialName("allowsFullAccess") val allowsFullAccess: Boolean, @SerialName("canResume") val canResume: Boolean, val message: String? = null)
@Serializable data class ConfirmationRequest(val confirmation: String)

@Serializable data class MobileApiProblem(val code: String? = null, val message: String? = null, @SerialName("correlationId") val correlationId: String? = null)
