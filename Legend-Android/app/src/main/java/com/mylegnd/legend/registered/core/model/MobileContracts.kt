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
@Serializable data class MobileAgentClient(@SerialName("profileId") val profileId: String, @SerialName("displayName") val displayName: String, val email: String, @SerialName("crmStatus") val crmStatus: String, val avatar: MobileAvatar? = null)
@Serializable data class MobileAgentLead(@SerialName("leadId") val leadId: String, @SerialName("displayName") val displayName: String, @SerialName("crmStage") val crmStage: String, @SerialName("updatedUtc") val updatedUtc: String)

@Serializable data class FinancialSnapshot(
    val position: FinancialPosition? = null,
    val intelligence: FinancialIntelligence? = null,
    @SerialName("upcomingBills") val upcomingBills: List<UpcomingBill> = emptyList(),
    @SerialName("operatingSystem") val operatingSystem: FinancialOperatingSystem? = null,
    val presentation: FinancialPresentation? = null,
    @SerialName("healthSnapshot") val healthSnapshot: FinancialHealthSnapshot? = null,
)
@Serializable data class FinancialPosition(@SerialName("healthScore") val healthScore: Int, @SerialName("assetsTotal") val assetsTotal: Double, @SerialName("liabilitiesTotal") val liabilitiesTotal: Double, @SerialName("netWorth") val netWorth: Double, @SerialName("annualEarnings") val annualEarnings: Double, @SerialName("annualLifestyleRemaining") val annualLifestyleRemaining: Double, @SerialName("annualTaxes") val annualTaxes: Double, @SerialName("protectionGapTotal") val protectionGapTotal: Double, @SerialName("positionStatus") val positionStatus: String, @SerialName("positionSummary") val positionSummary: String, @SerialName("estatePlanningStatus") val estatePlanningStatus: String, @SerialName("estatePlanningRiskLevel") val estatePlanningRiskLevel: String, @SerialName("updatedUtc") val updatedUtc: String)
@Serializable data class FinancialIntelligence(val status: String, @SerialName("dataCompletenessScore") val dataCompletenessScore: Double, @SerialName("currentRiskSummary") val currentRiskSummary: String, @SerialName("currentOpportunitySummary") val currentOpportunitySummary: String, @SerialName("currentLeakageSummary") val currentLeakageSummary: String, @SerialName("lastEvaluatedUtc") val lastEvaluatedUtc: String? = null, val findings: List<FinancialFinding> = emptyList())
@Serializable data class FinancialFinding(val id: String, val category: String, val title: String, val explanation: String, @SerialName("estimatedImpact") val estimatedImpact: Double? = null, @SerialName("impactUnit") val impactUnit: String? = null, val urgency: String, val status: String, @SerialName("lastDetectedUtc") val lastDetectedUtc: String)
@Serializable data class UpcomingBill(val id: String, @SerialName("displayName") val displayName: String, @SerialName("averageAmountCents") val averageAmountCents: Long, val cadence: String, @SerialName("nextExpectedDateUtc") val nextExpectedDateUtc: String, val status: String)
@Serializable data class FinancialOperatingSystem(val projection: FinancialProjectionStatus, val freshness: FinancialDataFreshness, @SerialName("weekAtGlance") val weekAtGlance: FinancialWeekAtGlance? = null, @SerialName("monthAtGlance") val monthAtGlance: FinancialMonthAtGlance? = null, val tools: List<FinancialToolSummary> = emptyList())
@Serializable data class FinancialProjectionStatus(val status: String, @SerialName("reasonCode") val reasonCode: String? = null, val summary: String? = null)
@Serializable data class FinancialDataFreshness(@SerialName("financeStateUpdatedUtc") val financeStateUpdatedUtc: String? = null, @SerialName("intelligenceEvaluatedUtc") val intelligenceEvaluatedUtc: String? = null, @SerialName("generatedUtc") val generatedUtc: String)
@Serializable data class FinancialWeekAtGlance(@SerialName("weekKey") val weekKey: String, @SerialName("startDate") val startDate: String, @SerialName("endDate") val endDate: String, @SerialName("openingCashCents") val openingCashCents: Long, @SerialName("incomeCents") val incomeCents: Long, @SerialName("debitExpenseCents") val debitExpenseCents: Long, @SerialName("creditExpenseCents") val creditExpenseCents: Long, @SerialName("requiredDebtPaymentCents") val requiredDebtPaymentCents: Long, @SerialName("extraDebtPaymentCents") val extraDebtPaymentCents: Long, @SerialName("endingCashCents") val endingCashCents: Long, @SerialName("openingDebtCents") val openingDebtCents: Long, @SerialName("endingDebtCents") val endingDebtCents: Long, @SerialName("pressureStatus") val pressureStatus: String, @SerialName("pressureSummary") val pressureSummary: String? = null, val events: List<FinancialCashFlowEvent> = emptyList())
@Serializable data class FinancialMonthAtGlance(@SerialName("monthKey") val monthKey: String, @SerialName("startDate") val startDate: String, @SerialName("endDate") val endDate: String, @SerialName("openingCashCents") val openingCashCents: Long, @SerialName("incomeCents") val incomeCents: Long, @SerialName("debitExpenseCents") val debitExpenseCents: Long, @SerialName("creditExpenseCents") val creditExpenseCents: Long, @SerialName("requiredDebtPaymentCents") val requiredDebtPaymentCents: Long, @SerialName("extraDebtPaymentCents") val extraDebtPaymentCents: Long, @SerialName("endingCashCents") val endingCashCents: Long, @SerialName("openingDebtCents") val openingDebtCents: Long, @SerialName("endingDebtCents") val endingDebtCents: Long, @SerialName("savingsContributionCents") val savingsContributionCents: Long, @SerialName("pressureStatus") val pressureStatus: String, @SerialName("pressureSummary") val pressureSummary: String? = null, @SerialName("largestObligation") val largestObligation: FinancialLargestObligation? = null, val weeks: List<FinancialWeekSummary> = emptyList())
@Serializable data class FinancialCashFlowEvent(@SerialName("eventKey") val eventKey: String, @SerialName("occursOn") val occursOn: String, val kind: String, val title: String, @SerialName("amountCents") val amountCents: Long, @SerialName("sourceToolId") val sourceToolId: String? = null, @SerialName("sourceItemId") val sourceItemId: String? = null, val status: String)
@Serializable data class FinancialWeekSummary(@SerialName("weekKey") val weekKey: String, @SerialName("startDate") val startDate: String, @SerialName("endDate") val endDate: String, @SerialName("incomeCents") val incomeCents: Long, @SerialName("outflowCents") val outflowCents: Long, @SerialName("endingCashCents") val endingCashCents: Long, @SerialName("endingDebtCents") val endingDebtCents: Long, @SerialName("pressureStatus") val pressureStatus: String)
@Serializable data class FinancialLargestObligation(val title: String, @SerialName("occursOn") val occursOn: String, @SerialName("amountCents") val amountCents: Long, val kind: String)
@Serializable data class FinancialToolSummary(@SerialName("toolId") val toolId: String, val title: String, val category: String, val priority: Int, @SerialName("availabilityStatus") val availabilityStatus: String, @SerialName("updatedUtc") val updatedUtc: String? = null, val summary: String? = null, val metrics: List<FinancialMetric> = emptyList())
@Serializable data class FinancialPresentation(@SerialName("assignedAgent") val assignedAgent: FinancialAssignedAgent? = null, @SerialName("prioritySections") val prioritySections: List<FinancialPrioritySection> = emptyList())
@Serializable data class FinancialAssignedAgent(@SerialName("hasAssignedAgent") val hasAssignedAgent: Boolean, @SerialName("displayName") val displayName: String? = null, @SerialName("firstName") val firstName: String? = null)
@Serializable data class FinancialPrioritySection(val key: String, val eyebrow: String, val title: String, @SerialName("systemImage") val systemImage: String, val priority: Int, val status: String, val reason: String, @SerialName("discussionPrompt") val discussionPrompt: String, @SerialName("primaryMetric") val primaryMetric: FinancialSummaryMetric, @SerialName("secondaryMetric") val secondaryMetric: FinancialSummaryMetric? = null)
@Serializable data class FinancialSummaryMetric(val label: String, @SerialName("amountCents") val amountCents: Long? = null, val date: String? = null, @SerialName("textValue") val textValue: String? = null, val semantic: String)
@Serializable data class FinancialHealthSnapshot(@SerialName("updatedUtc") val updatedUtc: String, val sections: List<FinancialHealthSection>)
@Serializable data class FinancialHealthSection(val key: String, val title: String, val semantic: String, val period: String? = null, val groups: List<FinancialHealthGroup> = emptyList(), val total: FinancialMetric? = null)
@Serializable data class FinancialHealthGroup(val key: String, val title: String? = null, val metrics: List<FinancialMetric> = emptyList())
@Serializable data class FinancialMetric(val key: String, val label: String, @SerialName("valueType") val valueType: String, @SerialName("amountCents") val amountCents: Long? = null, @SerialName("numericValue") val numericValue: Double? = null, @SerialName("textValue") val textValue: String? = null, val status: String? = null)

@Serializable data class MobileParticipant(
    val identity: MobileIdentity,
    @SerialName("profileId") val profileId: String,
    @SerialName("displayName") val displayName: String,
    val avatar: MobileAvatar? = null,
    val roleLabel: String? = null,
    val isVerified: Boolean = false,
    @SerialName("isGroupManager") val isGroupManager: Boolean = false,
)
@Serializable data class ConversationSummary(val id: String, @SerialName("conversationType") val conversationType: String, val title: String, val counterparty: MobileParticipant, @SerialName("lastMessagePreview") val lastMessagePreview: String? = null, @SerialName("lastMessageUtc") val lastMessageUtc: String? = null, @SerialName("unreadCount") val unreadCount: Int, @SerialName("isClosed") val isClosed: Boolean, val purpose: String? = null, @SerialName("groupAvatar") val groupAvatar: MobileAvatar? = null, @SerialName("isPinned") val isPinned: Boolean = false, @SerialName("isMuted") val isMuted: Boolean = false)
@Serializable data class ConversationDetail(
    val id: String,
    @SerialName("conversationType") val conversationType: String,
    val title: String,
    val participants: List<MobileParticipant> = emptyList(),
    val messages: List<ConversationMessage> = emptyList(),
    @SerialName("isMuted") val isMuted: Boolean,
    @SerialName("isClosed") val isClosed: Boolean,
    @SerialName("canManageMembers") val canManageMembers: Boolean,
    val purpose: String? = null,
    @SerialName("groupAvatar") val groupAvatar: MobileAvatar? = null,
    @SerialName("canManageCollaborators") val canManageCollaborators: Boolean = false,
    @SerialName("canDeleteGroup") val canDeleteGroup: Boolean = false,
    @SerialName("isPromoted") val isPromoted: Boolean = false,
    @SerialName("promotionStartedUtc") val promotionStartedUtc: String? = null,
    @SerialName("promotionEndedUtc") val promotionEndedUtc: String? = null,
    @SerialName("canManagePromotion") val canManagePromotion: Boolean = false,
    val meeting: MessagingGroupMeeting? = null,
    @SerialName("canManageMeeting") val canManageMeeting: Boolean = false,
    @SerialName("hasOlderMessages") val hasOlderMessages: Boolean = false,
)
@Serializable data class MessagingGroupMeeting(
    val host: MobileParticipant,
    @SerialName("linkLabel") val linkLabel: String? = null,
    @SerialName("linkUrl") val linkUrl: String? = null,
    val schedule: MessagingGroupMeetingSchedule? = null,
)
@Serializable data class MessagingGroupMeetingSchedule(
    val frequency: String,
    val weekdays: List<String> = emptyList(),
    @SerialName("localTime") val localTime: String? = null,
    @SerialName("timeZoneId") val timeZoneId: String? = null,
    @SerialName("startsUtc") val startsUtc: String? = null,
    @SerialName("customDescription") val customDescription: String? = null,
)
@Serializable data class ConversationMessage(
    val id: String,
    @SerialName("conversationId") val conversationId: String,
    val sender: MobileParticipant,
    val body: String,
    @SerialName("sentUtc") val sentUtc: String,
    val attachments: List<MessageAttachment> = emptyList(),
    @SerialName("isMine") val isMine: Boolean,
    @SerialName("isDeleted") val isDeleted: Boolean,
    val reply: MessageReplyPreview? = null,
    @SerialName("verificationReview") val verificationReview: VerificationReview? = null,
    val translation: MessageTranslation? = null,
    @SerialName("originalBody") val originalBody: String? = null,
)
@Serializable data class MessageReplyPreview(val id: String, val sender: MobileParticipant, val body: String, @SerialName("isDeleted") val isDeleted: Boolean)
@Serializable data class VerificationReview(val id: String, @SerialName("requesterUserId") val requesterUserId: String, @SerialName("requesterParticipantType") val requesterParticipantType: String, val status: String, @SerialName("requestedUtc") val requestedUtc: String, @SerialName("canResolve") val canResolve: Boolean, @SerialName("resourceType") val resourceType: String)
@Serializable data class MessageAttachment(val id: String, @SerialName("originalFileName") val originalFileName: String, @SerialName("contentType") val contentType: String, @SerialName("sizeBytes") val sizeBytes: Long, @SerialName("scanStatus") val scanStatus: String, @SerialName("createdUtc") val createdUtc: String, @SerialName("canDownload") val canDownload: Boolean)
@Serializable data class MessageTranslation(@SerialName("originalLanguage") val originalLanguage: String, @SerialName("targetLanguage") val targetLanguage: String, val provider: String)
@Serializable data class SendMessageRequest(val body: String, @SerialName("replyToMessageId") val replyToMessageId: String? = null)
@Serializable data class StartConversationRequest(@SerialName("targetUserId") val targetUserId: String, @SerialName("targetParticipantType") val targetParticipantType: String, @SerialName("initialMessageBody") val initialMessageBody: String? = null)
@Serializable data class MessagingGroupParticipantRequest(@SerialName("userId") val userId: String, @SerialName("participantType") val participantType: String)
@Serializable data class MessagingGroupImageRequest(@SerialName("contentType") val contentType: String, @SerialName("base64Content") val base64Content: String)
@Serializable data class MessagingGroupMeetingRequest(val host: MessagingGroupParticipantRequest? = null, @SerialName("linkLabel") val linkLabel: String? = null, @SerialName("linkUrl") val linkUrl: String? = null, val schedule: MessagingGroupMeetingScheduleRequest? = null)
@Serializable data class MessagingGroupMeetingScheduleRequest(val frequency: String, val weekdays: List<String> = emptyList(), @SerialName("localTime") val localTime: String? = null, @SerialName("timeZoneId") val timeZoneId: String? = null, @SerialName("startsUtc") val startsUtc: String? = null, @SerialName("customDescription") val customDescription: String? = null)
@Serializable data class CreateMessagingGroupRequest(val subject: String, val participants: List<MessagingGroupParticipantRequest>, @SerialName("initialMessageBody") val initialMessageBody: String? = null, @SerialName("groupImage") val groupImage: MessagingGroupImageRequest? = null, val meeting: MessagingGroupMeetingRequest? = null)
@Serializable data class UpdateMessagingGroupRequest(val subject: String, @SerialName("groupImage") val groupImage: MessagingGroupImageRequest? = null, val meeting: MessagingGroupMeetingRequest? = null)
@Serializable data class MessagingGroupCollaboratorRequest(@SerialName("userId") val userId: String, @SerialName("participantType") val participantType: String, @SerialName("isManager") val isManager: Boolean)
@Serializable data class MessagingGroupPromotionRequest(@SerialName("isPromoted") val isPromoted: Boolean)
@Serializable data class VerificationResolutionRequest(val approve: Boolean, val note: String? = null)
@Serializable data class ControlledResourceGrantRequest(@SerialName("targetUserId") val targetUserId: String, @SerialName("targetParticipantType") val targetParticipantType: String, @SerialName("isGranted") val isGranted: Boolean)
@Serializable data class MessagingVerificationRequest(val id: String, val status: String, @SerialName("requestedUtc") val requestedUtc: String, @SerialName("resourceType") val resourceType: String)
@Serializable data class MessagingActivityNotification(val id: String, val kind: String, val title: String, val detail: String, @SerialName("occurredUtc") val occurredUtc: String, @SerialName("controlledResourceRequestId") val controlledResourceRequestId: String? = null)
@Serializable data class CommunicationLanguage(val code: String, @SerialName("displayName") val displayName: String)
@Serializable data class MessagingRecipient(
    val identity: MobileIdentity,
    @SerialName("profileId") val profileId: String,
    @SerialName("displayName") val displayName: String,
    val email: String? = null,
    @SerialName("relationshipLabel") val relationshipLabel: String? = null,
    @SerialName("existingConversationId") val existingConversationId: String? = null,
    val avatar: MobileAvatar? = null,
    @SerialName("roleLabel") val roleLabel: String? = null,
    @SerialName("isVerified") val isVerified: Boolean = false,
    @SerialName("resourceType") val resourceType: String? = null,
    @SerialName("resourceAccessState") val resourceAccessState: String? = null,
)
@Serializable data class ConversationPinnedRequest(@SerialName("isPinned") val isPinned: Boolean)
@Serializable data class ConversationMutedRequest(@SerialName("isMuted") val isMuted: Boolean)
@Serializable data class ConversationCallOptions(@SerialName("conversationId") val conversationId: String, @SerialName("displayName") val displayName: String, @SerialName("phoneNumber") val phoneNumber: String? = null, @SerialName("faceTimeAddress") val faceTimeAddress: String? = null)

@Serializable data class SocialSnapshot(
    val stories: List<SocialPost> = emptyList(), val posts: List<SocialPost> = emptyList(), val hacs: List<SocialPost> = emptyList(),
    val activity: List<SocialActivity> = emptyList(), @SerialName("activityCount") val activityCount: Int = 0,
    @SerialName("currentProfileMetrics") val currentProfileMetrics: SocialProfileMetrics? = null,
    @SerialName("creatorInsights") val creatorInsights: CreatorInsights? = null,
    @SerialName("promotedGroups") val promotedGroups: List<SocialPromotedGroup> = emptyList(),
)
/**
 * Server-authorized public invitation metadata. This is deliberately not a
 * conversation projection: message history remains unavailable until the
 * existing messaging authority accepts the member into the group.
 */
@Serializable data class SocialPromotedGroup(
    @SerialName("conversationId") val conversationId: String,
    val subject: String,
    val owner: SocialAuthor,
    @SerialName("groupAvatar") val groupAvatar: MobileAvatar? = null,
    @SerialName("activeMemberCount") val activeMemberCount: Int,
    @SerialName("isJoinedByCurrentActor") val isJoinedByCurrentActor: Boolean,
    @SerialName("promotionStartedUtc") val promotionStartedUtc: String,
)
@Serializable data class SocialAuthor(val identity: MobileIdentity, @SerialName("profileId") val profileId: String, @SerialName("displayName") val displayName: String, val avatar: MobileAvatar? = null, val username: String? = null, val bio: String? = null, val website: String? = null, val location: String? = null, @SerialName("publicEmail") val publicEmail: String? = null, @SerialName("publicPhone") val publicPhone: String? = null, val isPrivate: Boolean = false, val isVerified: Boolean = false, val roleLabel: String? = null)
@Serializable data class SocialPost(val id: String, val author: SocialAuthor, @SerialName("contentType") val contentType: String, val body: String, val audience: String, val location: String? = null, @SerialName("commentsEnabled") val commentsEnabled: Boolean, @SerialName("postedUtc") val postedUtc: String, @SerialName("expiresUtc") val expiresUtc: String? = null, @SerialName("reactionCount") val reactionCount: Int, @SerialName("commentCount") val commentCount: Int, @SerialName("reactedByCurrentActor") val reactedByCurrentActor: Boolean, @SerialName("followedByCurrentActor") val followedByCurrentActor: Boolean, @SerialName("followRequestPending") val followRequestPending: Boolean = false, @SerialName("savedByCurrentActor") val savedByCurrentActor: Boolean, @SerialName("repostedByCurrentActor") val repostedByCurrentActor: Boolean, val metrics: SocialPostMetrics = SocialPostMetrics(), val music: SocialMusic? = null, val media: List<SocialMedia> = emptyList(), val comments: List<SocialComment> = emptyList())
@Serializable data class SocialMedia(val id: String, @SerialName("displayOrder") val displayOrder: Int, @SerialName("mediaKind") val mediaKind: String, @SerialName("mimeType") val mimeType: String, @SerialName("fileSizeBytes") val fileSizeBytes: Long, val width: Int? = null, val height: Int? = null, @SerialName("aspectRatio") val aspectRatio: Double? = null, @SerialName("durationSeconds") val durationSeconds: Double? = null, @SerialName("processingState") val processingState: String, @SerialName("accessibilityText") val accessibilityText: String? = null, @SerialName("hasPreviewImage") val hasPreviewImage: Boolean)
@Serializable data class SocialComment(val id: String, val author: SocialAuthor, @SerialName("parentCommentId") val parentCommentId: String? = null, val body: String, @SerialName("createdUtc") val createdUtc: String)
@Serializable data class SocialActivity(val id: String, val kind: String, val actor: SocialAuthor, @SerialName("postId") val postId: String? = null, @SerialName("occurredUtc") val occurredUtc: String)
@Serializable data class SocialPostMetrics(@SerialName("viewCount") val viewCount: Int = 0, @SerialName("uniqueViewerCount") val uniqueViewerCount: Int = 0, @SerialName("reactionCount") val reactionCount: Int = 0, @SerialName("commentCount") val commentCount: Int = 0, @SerialName("replyCount") val replyCount: Int = 0, @SerialName("repostCount") val repostCount: Int = 0, @SerialName("saveCount") val saveCount: Int = 0, @SerialName("shareCount") val shareCount: Int = 0, @SerialName("profileVisitCount") val profileVisitCount: Int = 0, @SerialName("followsGenerated") val followsGenerated: Int = 0, @SerialName("averageWatchDurationSeconds") val averageWatchDurationSeconds: Double? = null, @SerialName("averageWatchCompletionPercentage") val averageWatchCompletionPercentage: Double? = null, @SerialName("storyExitCount") val storyExitCount: Int = 0, @SerialName("storyTapForwardCount") val storyTapForwardCount: Int = 0, @SerialName("storyTapBackwardCount") val storyTapBackwardCount: Int = 0)
@Serializable data class SocialMusic(@SerialName("providerId") val providerId: String, @SerialName("providerTrackId") val providerTrackId: String, @SerialName("trackTitle") val trackTitle: String, @SerialName("artistName") val artistName: String, @SerialName("trackDurationSeconds") val trackDurationSeconds: Double, @SerialName("audioUrl") val audioUrl: String? = null, @SerialName("trimStartSeconds") val trimStartSeconds: Double? = null, @SerialName("trimEndSeconds") val trimEndSeconds: Double? = null, @SerialName("musicVolume") val musicVolume: Double? = null, @SerialName("originalAudioVolume") val originalAudioVolume: Double? = null)
@Serializable data class SocialProfileMetrics(val profile: SocialAuthor, @SerialName("postCount") val postCount: Int, @SerialName("videoCount") val videoCount: Int, @SerialName("storyCount") val storyCount: Int, @SerialName("followerCount") val followerCount: Int, @SerialName("followingCount") val followingCount: Int, @SerialName("totalReactionCount") val totalReactionCount: Int = 0, @SerialName("totalContentViewCount") val totalContentViewCount: Int = 0, @SerialName("totalReachCount") val totalReachCount: Int = 0, @SerialName("privateProfileVisitCount") val privateProfileVisitCount: Int? = null)
@Serializable data class SocialPostInsight(@SerialName("postId") val postId: String, @SerialName("contentType") val contentType: String, @SerialName("postedUtc") val postedUtc: String, val metrics: SocialPostMetrics, @SerialName("engagementRatePercentage") val engagementRatePercentage: Double)

/**
 * The one Android mapping for the social API discriminator. The persisted
 * backend value for a member-facing Hac remains `Reel`; presentation code must
 * never send or compare the member label "Hac" as though it were an API value.
 */
enum class LegendSocialContentType(val apiValue: String) {
    POST("Post"),
    STORY("Story"),
    HAC("Reel"),
    ;

    companion object {
        fun fromApiValue(value: String?): LegendSocialContentType? = entries.firstOrNull {
            it.apiValue.equals(value?.trim(), ignoreCase = true)
        }
    }
}

val SocialPost.legendContentType: LegendSocialContentType?
    get() = LegendSocialContentType.fromApiValue(contentType)

@Serializable data class CreatorInsights(@SerialName("generatedUtc") val generatedUtc: String? = null, @SerialName("totalViews") val totalViews: Int, @SerialName("totalReach") val totalReach: Int, @SerialName("followerCount") val followerCount: Int, @SerialName("followingCount") val followingCount: Int = 0, @SerialName("followersGained") val followersGained: Int = 0, @SerialName("profileVisits") val profileVisits: Int = 0, @SerialName("totalReactions") val totalReactions: Int = 0, @SerialName("totalComments") val totalComments: Int = 0, @SerialName("totalReplies") val totalReplies: Int = 0, @SerialName("totalShares") val totalShares: Int = 0, @SerialName("totalReposts") val totalReposts: Int = 0, @SerialName("totalSaves") val totalSaves: Int = 0, @SerialName("engagementRatePercentage") val engagementRatePercentage: Double, @SerialName("topPosts") val topPosts: List<SocialPostInsight> = emptyList(), @SerialName("topVideos") val topVideos: List<SocialPostInsight> = emptyList(), @SerialName("topStories") val topStories: List<SocialPostInsight> = emptyList())
@Serializable data class CreateSocialPostRequest(@SerialName("contentType") val contentType: String, val body: String, val audience: String? = null, val location: String? = null, @SerialName("commentsEnabled") val commentsEnabled: Boolean? = null)
@Serializable data class SocialMediaPublishOptions(
    @SerialName("contentType") val contentType: String,
    val body: String,
    val audience: String,
    val location: String? = null,
    @SerialName("commentsEnabled") val commentsEnabled: Boolean = true,
    @SerialName("accessibilityText") val accessibilityText: String? = null,
    val music: SocialMusic? = null,
)
@Serializable data class UpdateSocialPostRequest(val body: String)
@Serializable data class CreateSocialCommentRequest(val body: String, @SerialName("parentCommentId") val parentCommentId: String? = null)
@Serializable data class SocialFollowRequest(@SerialName("followedUserId") val followedUserId: String, @SerialName("followedParticipantType") val followedParticipantType: String, @SerialName("sourcePostId") val sourcePostId: String? = null)
@Serializable data class SocialFollowResult(@SerialName("isFollowing") val isFollowing: Boolean, @SerialName("isPending") val isPending: Boolean)
@Serializable data class SocialStateResult(@SerialName("isActive") val isActive: Boolean)
@Serializable data class SocialFollowListEntry(val profile: SocialAuthor, @SerialName("followedByCurrentActor") val followedByCurrentActor: Boolean)
@Serializable data class SocialFollowRequestItem(val id: String, val profile: SocialAuthor, @SerialName("requestedUtc") val requestedUtc: String)
@Serializable data class FollowRequestDecision(val approve: Boolean)

@Serializable data class MobileAccountProfile(val participantType: String, @SerialName("profileId") val profileId: String, @SerialName("displayName") val displayName: String, val email: String? = null, val phone: String? = null, val title: String? = null, @SerialName("roleLabel") val roleLabel: String? = null, @SerialName("shortBio") val shortBio: String? = null, val username: String? = null, val bio: String? = null, val website: String? = null, val location: String? = null, @SerialName("profileEmail") val profileEmail: String? = null, @SerialName("isEmailVisible") val isEmailVisible: Boolean = false, @SerialName("isPrivate") val isPrivate: Boolean, val avatar: MobileAvatar? = null, @SerialName("isVerified") val isVerified: Boolean, @SerialName("usernameChangesRemaining") val usernameChangesRemaining: Int = 0, @SerialName("isPhoneVisible") val isPhoneVisible: Boolean = false, @SerialName("translationAccess") val translationAccess: TranslationAccess? = null)
@Serializable data class TranslationAccess(val state: String, @SerialName("canManage") val canManage: Boolean, @SerialName("preferredCommunicationLanguage") val preferredCommunicationLanguage: String? = null)
@Serializable data class AccountUpdateRequest(@SerialName("displayName") val displayName: String, val phone: String? = null, val title: String? = null, @SerialName("shortBio") val shortBio: String? = null, val username: String? = null, val bio: String? = null, val website: String? = null, val location: String? = null, @SerialName("publicEmail") val publicEmail: String? = null, @SerialName("isEmailVisible") val isEmailVisible: Boolean = false, @SerialName("isPhoneVisible") val isPhoneVisible: Boolean = false, @SerialName("isPrivate") val isPrivate: Boolean? = null, @SerialName("preferredCommunicationLanguage") val preferredCommunicationLanguage: String? = null)
@Serializable data class AccountPrivacyUpdateRequest(@SerialName("isPrivate") val isPrivate: Boolean)
@Serializable data class AccountAvatarUpdateRequest(@SerialName("base64Content") val base64Content: String)
@Serializable data class AccountLifecycle(val state: String, @SerialName("allowsFullAccess") val allowsFullAccess: Boolean, @SerialName("canResume") val canResume: Boolean, val message: String? = null)
@Serializable data class ConfirmationRequest(val confirmation: String)
@Serializable data class MobileUsernameAvailability(@SerialName("isAvailable") val isAvailable: Boolean, val message: String? = null)
@Serializable data class DailyScriptureManagementSnapshot(@SerialName("businessDate") val businessDate: String, val current: MobileDailyScripture, val upcoming: List<DailyScriptureOverride> = emptyList())
@Serializable data class DailyScriptureOverride(val id: String, @SerialName("displayDate") val displayDate: String, val reference: String, val translation: String, @SerialName("passageText") val passageText: String, @SerialName("createdUtc") val createdUtc: String, @SerialName("updatedUtc") val updatedUtc: String)
@Serializable data class DailyScriptureOverrideRequest(@SerialName("displayDate") val displayDate: String, val reference: String, val translation: String, @SerialName("passageText") val passageText: String)
@Serializable data class CommunitySafetyReport(val id: String, @SerialName("targetKind") val targetKind: String, @SerialName("targetEntityId") val targetEntityId: String? = null, val category: String, val detail: String? = null, val status: String, @SerialName("createdUtc") val createdUtc: String, @SerialName("reporterParticipantType") val reporterParticipantType: String, @SerialName("reportedParticipantType") val reportedParticipantType: String, @SerialName("resolvedUtc") val resolvedUtc: String? = null, val resolution: String? = null)
@Serializable data class CommunitySafetyReportResolutionRequest(val resolution: String)
@Serializable data class FounderManagedAccount(@SerialName("profileId") val profileId: String, @SerialName("userId") val userId: String, @SerialName("participantType") val participantType: String, @SerialName("displayName") val displayName: String, val email: String? = null, @SerialName("lifecycleState") val lifecycleState: String, @SerialName("hasCancelableSubscription") val hasCancelableSubscription: Boolean, @SerialName("isActive") val isActive: Boolean)
@Serializable data class FounderAccountTargetRequest(@SerialName("profileId") val profileId: String, @SerialName("participantType") val participantType: String)
@Serializable data class FounderAccountRemovalRequest(@SerialName("profileId") val profileId: String, @SerialName("participantType") val participantType: String, val confirmation: String)
@Serializable data class FounderAccountBatchRequest(val accounts: List<FounderAccountTargetRequest>, val confirmation: String)
@Serializable data class FounderAccountRemovalResponse(val completed: Boolean, val message: String, @SerialName("lifecycleState") val lifecycleState: String)
@Serializable data class FounderAccountBatchItemResponse(val succeeded: Boolean, val completed: Boolean, val errorCode: String? = null, val message: String, @SerialName("lifecycleState") val lifecycleState: String)
@Serializable data class FounderAccountBatchResponse(@SerialName("completedCount") val completedCount: Int, @SerialName("failedCount") val failedCount: Int, val results: List<FounderAccountBatchItemResponse>)

@Serializable data class MobileApiProblem(val code: String? = null, val message: String? = null, @SerialName("correlationId") val correlationId: String? = null)
