package com.mylegnd.legend.registered.data

import com.mylegnd.legend.registered.core.model.*
import com.mylegnd.legend.registered.core.network.*
import android.content.Context
import android.net.Uri
import com.mylegnd.legend.registered.core.media.SocialMediaUploader
import com.mylegnd.legend.registered.core.media.MessagingAttachmentUploader
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import okhttp3.Request
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.flow.flowOn
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.json.Json
import java.util.TimeZone

sealed interface LoadState<out T> { data object Idle : LoadState<Nothing>; data object Loading : LoadState<Nothing>; data class Data<T>(val value: T) : LoadState<T>; data class Error(val message: String) : LoadState<Nothing> }
private suspend fun <T> request(block: suspend () -> T): LoadState<T> = runCatching { LoadState.Data(block()) }.getOrElse { LoadState.Error((it as? LegendApiException)?.problem?.message ?: "Legend is unavailable right now.") }
class HomeRepository(private val client: LegendApiClient) { suspend fun load(role: String) = request { client.api.home(role).legendBody() } }
class FounderAiRepository(private val client: LegendApiClient) {
    private val json = Json { ignoreUnknownKeys = true }

    suspend fun access(role: String): LoadState<FounderAiAccessResponse> = request {
        client.api.founderAiAccess(role).legendBody()
    }

    suspend fun chat(
        role: String,
        operationId: String,
        chatRequest: FounderAiChatRequest,
    ): LoadState<FounderAiChatResponse> = request {
        client.api.founderAiChat(role, operationId, chatRequest).legendBody()
    }

    /** Advisory progress only; the same chat response remains authoritative. */
    fun progress(role: String, operationId: String): Flow<FounderAiProgressEnvelope> = flow<FounderAiProgressEnvelope> {
        val base = client.baseUrl.toHttpUrlOrNull()
            ?: return@flow
        val url = base.newBuilder()
            .addPathSegments("api/v1/mobile/founder/legend-ai/progress/$operationId")
            .build()
        val request = Request.Builder()
            .url(url)
            .header("Accept", "application/x-ndjson")
            .header("X-Legend-Participant-Type", role)
            .build()

        client.httpClient.newCall(request).execute().use { response ->
            if (!response.isSuccessful) return@use
            val source = response.body.source()
            while (!source.exhausted()) {
                currentCoroutineContext().ensureActive()
                val line = source.readUtf8Line()?.trim().orEmpty()
                if (line.isBlank()) continue
                val envelope = runCatching {
                    json.decodeFromString(FounderAiProgressEnvelope.serializer(), line)
                }.getOrNull()
                if (envelope != null) emit(envelope)
            }
        }
    }.flowOn(Dispatchers.IO)
}
class AgentWorkspaceRepository(private val client: LegendApiClient) {
    suspend fun clients(role: String) = request { client.api.agentClients(role).legendBody() }
    suspend fun leads(role: String) = request { client.api.agentLeads(role).legendBody() }

    /**
     * The server creates the ticket. Android only verifies that the returned
     * location remains on the configured AgentPortal origin before hosting it.
     */
    suspend fun clientCreationPortalLaunch(role: String) = request {
        val launch = client.api.clientCreationPortalLaunch(role).legendBody()
        val base = client.baseUrl.toHttpUrlOrNull()
            ?: error("Legend mobile API configuration is invalid.")
        val launchUrl = launch.launchPath.toHttpUrlOrNull() ?: base.resolve(launch.launchPath)
            ?: error("Legend returned an invalid client-intake location.")
        require(
            launchUrl.scheme == base.scheme &&
                launchUrl.host == base.host &&
                launchUrl.port == base.port,
        ) { "Legend returned a client-intake location outside the approved portal." }
        launch.copy(launchPath = launchUrl.toString())
    }
}
class FinancialRepository(private val client: LegendApiClient) {
    suspend fun load(role: String) = request {
        val timeZone = TimeZone.getDefault()
        val minutesBehindUtc = -(
            timeZone.getOffset(System.currentTimeMillis()) / 60_000
        )
        client.api.financial(
            role,
            timeZone.id,
            minutesBehindUtc.toString(),
        ).legendBody()
    }
}
class AccountRepository(private val client: LegendApiClient) { suspend fun profile(role: String) = request { client.api.account(role).legendBody() }; suspend fun lifecycle(role: String) = request { client.api.lifecycle(role).legendBody() }; suspend fun usernameAvailability(role: String, username: String?) = request { client.api.usernameAvailability(role, username).legendBody() }; suspend fun update(role: String, update: AccountUpdateRequest) = request { client.api.updateAccount(role, update).legendBody() }; suspend fun updatePrivacy(role: String, isPrivate: Boolean) = request { client.api.updatePrivacy(role, AccountPrivacyUpdateRequest(isPrivate)).legendBody() }; suspend fun updateTranslationLearningConsent(role: String, allowsConsentedTranslationLearning: Boolean) = request { client.api.updateTranslationLearningConsent(role, TranslationLearningConsentUpdateRequest(allowsConsentedTranslationLearning)).legendBody() }; suspend fun updateAvatar(role: String, base64Content: String) = request { client.api.updateAvatar(role, AccountAvatarUpdateRequest(base64Content)).legendBody() }; suspend fun requestDeletion(role: String, confirmation: String) = request { client.api.deletionRequest(role, ConfirmationRequest(confirmation)).legendBody() }; suspend fun pause(role: String) = request { client.api.pauseAccount(role, ConfirmationRequest("PAUSE")).legendBody() }; suspend fun resume(role: String) = request { client.api.resumeAccount(role).legendBody() } }
class DailyScriptureManagementRepository(private val client: LegendApiClient) { suspend fun management(role: String) = request { client.api.dailyScriptureManagement(role).legendBody() }; suspend fun create(role: String, draft: DailyScriptureOverrideRequest) = request { client.api.createDailyScriptureOverride(role, draft).legendBody() }; suspend fun update(role: String, id: String, draft: DailyScriptureOverrideRequest) = request { client.api.updateDailyScriptureOverride(role, id, draft).legendBody() }; suspend fun remove(role: String, id: String) = request { client.api.deleteDailyScriptureOverride(role, id).legendBody() } }
class FounderAccountRepository(private val client: LegendApiClient) {
    suspend fun accounts(role: String, search: String? = null, scope: String? = null) = request { client.api.founderAccounts(role, search, scope = scope).legendBody() }
    suspend fun remove(role: String, account: FounderManagedAccount, confirmation: String) = request { client.api.removeFounderAccount(role, FounderAccountRemovalRequest(account.profileId, account.participantType, confirmation)).legendBody() }
    suspend fun removeBatch(role: String, accounts: List<FounderManagedAccount>, confirmation: String) = request { client.api.removeFounderAccounts(role, FounderAccountBatchRequest(accounts.map { FounderAccountTargetRequest(it.profileId, it.participantType) }, confirmation)).legendBody() }
    suspend fun purge(role: String, accounts: List<FounderManagedAccount>, confirmation: String) = request { client.api.purgeFounderAccounts(role, FounderAccountBatchRequest(accounts.map { FounderAccountTargetRequest(it.profileId, it.participantType) }, confirmation)).legendBody() }
}
class MessagingRepository(private val client: LegendApiClient) {
    private val attachmentUploader = MessagingAttachmentUploader(client)

    suspend fun conversations(role: String, take: Int = 24, skip: Int = 0) = request {
        client.api.conversations(role, take, skip).legendBody()
    }
    suspend fun recipients(role: String, search: String? = null, scope: String? = null) = request {
        client.api.recipients(role, search, scope).legendBody()
    }
    suspend fun conversation(role: String, id: String, beforeUtc: String? = null) = request {
        client.api.conversation(role, id, beforeUtc).legendBody()
    }
    suspend fun send(role: String, id: String, text: String, replyToMessageId: String? = null) = request {
        client.api.sendMessage(role, id, SendMessageRequest(text, replyToMessageId)).legendBody()
    }
    suspend fun uploadAttachment(context: Context, role: String, conversationId: String, messageId: String, uri: Uri) = request {
        attachmentUploader.upload(context, role, conversationId, messageId, uri)
    }
    suspend fun startConversation(role: String, recipient: MessagingRecipient) = request {
        client.api.startConversation(
            role,
            StartConversationRequest(recipient.identity.userId, recipient.identity.participantType),
        ).legendBody()
    }
    suspend fun createGroup(role: String, request: CreateMessagingGroupRequest) = request { client.api.createMessagingGroup(role, request).legendBody() }
    suspend fun updateGroup(role: String, id: String, request: UpdateMessagingGroupRequest) = request { client.api.updateMessagingGroup(role, id, request).legendBody() }
    suspend fun addParticipant(role: String, id: String, participant: MessagingGroupParticipantRequest) = request { client.api.addGroupParticipant(role, id, participant).legendBody() }
    suspend fun setGroupCollaborator(role: String, id: String, participant: MobileParticipant, isManager: Boolean) = request { client.api.setGroupCollaborator(role, id, MessagingGroupCollaboratorRequest(participant.identity.userId, participant.identity.participantType, isManager)).legendBody() }
    suspend fun deleteGroup(role: String, id: String) = request { client.api.deleteMessagingGroup(role, id).legendBody() }
    suspend fun setGroupPromotion(role: String, id: String, isPromoted: Boolean) = request { client.api.setGroupPromotion(role, id, MessagingGroupPromotionRequest(isPromoted)).legendBody() }
    suspend fun markRead(role: String, id: String) = request { client.api.markRead(role, id).legendBody() }
    suspend fun setPinned(role: String, id: String, isPinned: Boolean) = request {
        client.api.setConversationPinned(role, id, ConversationPinnedRequest(isPinned)).legendBody()
    }
    suspend fun setMuted(role: String, id: String, isMuted: Boolean) = request {
        client.api.setConversationMuted(role, id, ConversationMutedRequest(isMuted)).legendBody()
    }
    suspend fun remove(role: String, id: String) = request { client.api.removeConversation(role, id).legendBody() }
    suspend fun deleteMessage(role: String, conversationId: String, messageId: String) = request {
        client.api.deleteMessage(role, conversationId, messageId).legendBody()
    }
    suspend fun callOptions(role: String, id: String) = request { client.api.conversationCallOptions(role, id).legendBody() }
    suspend fun requestVerification(role: String) = request { client.api.requestVerification(role).legendBody() }
    suspend fun resolveVerification(role: String, id: String, approve: Boolean, note: String? = null) = request { client.api.resolveVerification(role, id, VerificationResolutionRequest(approve, note)).legendBody() }
    suspend fun controlledRecipients(role: String, resourceType: String, search: String? = null) = request { client.api.controlledResourceRecipients(role, resourceType, search).legendBody() }
    suspend fun setControlledGrant(role: String, resourceType: String, recipient: MessagingRecipient, isGranted: Boolean) = request { client.api.setControlledResourceGrant(role, resourceType, ControlledResourceGrantRequest(recipient.identity.userId, recipient.identity.participantType, isGranted)).legendBody() }
    suspend fun activity(role: String) = request { client.api.messagingActivity(role).legendBody() }
    suspend fun languages(role: String) = request { client.api.communicationLanguages(role).legendBody() }
}
class SocialRepository(private val client: LegendApiClient) {
    private val uploader = SocialMediaUploader(client)
    suspend fun feed(role: String) = request { client.api.socialFeed(role).legendBody() }
    suspend fun currentProfilePosts(role: String) = request { client.api.currentProfilePosts(role).legendBody() }
    suspend fun publicProfilePosts(role: String, author: SocialAuthor) = request { client.api.publicProfilePosts(role, author.identity.userId, author.identity.participantType, author.profileId).legendBody() }
    suspend fun profileMetrics(role: String, author: SocialAuthor? = null) = request { client.api.profileMetrics(role, author?.identity?.userId, author?.identity?.participantType, author?.profileId).legendBody() }
    suspend fun follows(role: String, list: String) = request { client.api.profileFollows(role, list).legendBody() }
    suspend fun followRequests(role: String) = request { client.api.incomingFollowRequests(role).legendBody() }
    suspend fun createPost(role: String, request: CreateSocialPostRequest) = request { client.api.createPost(role, request).legendBody() }
    suspend fun updatePost(role: String, id: String, body: String) = request { client.api.updatePost(role, id, UpdateSocialPostRequest(body)).legendBody() }
    suspend fun deletePost(role: String, id: String) = request { client.api.deletePost(role, id).legendBody() }
    suspend fun createMediaPost(context: Context, role: String, uris: List<Uri>, options: SocialMediaPublishOptions, previewUri: Uri? = null) = request { uploader.upload(context, role, uris, options, previewUri) }
    suspend fun react(role: String, id: String) = request { client.api.react(role, id).legendBody() }
    suspend fun comment(role: String, id: String, body: String, parentCommentId: String? = null) = request { client.api.comment(role, id, CreateSocialCommentRequest(body, parentCommentId)).legendBody() }
    suspend fun toggleFollow(role: String, author: SocialAuthor, sourcePostId: String? = null) = request { client.api.toggleFollow(role, SocialFollowRequest(author.identity.userId, author.identity.participantType, sourcePostId)).legendBody() }
    suspend fun decideFollowRequest(role: String, id: String, approve: Boolean) = request { client.api.decideFollowRequest(role, id, FollowRequestDecision(approve)).legendBody() }
    suspend fun toggleSave(role: String, id: String) = request { client.api.savePost(role, id).legendBody() }
    suspend fun toggleRepost(role: String, id: String) = request { client.api.repost(role, id).legendBody() }
    suspend fun recordShare(role: String, id: String) = request { client.api.recordShare(role, id).legendBody() }
    suspend fun recordView(role: String, id: String, requestBody: SocialViewRequest) = request { client.api.recordView(role, id, requestBody).legendBody() }
    suspend fun recordProfileVisit(role: String, author: SocialAuthor, sourcePostId: String? = null) = request { client.api.recordProfileVisit(role, SocialProfileVisitRequest(author.identity.userId, author.identity.participantType, sourcePostId)).legendBody() }
    suspend fun joinPromotedGroup(role: String, id: String) = request { client.api.joinPromotedGroup(role, id).legendBody() }
}
class NotificationRepository(private val client: LegendApiClient) { suspend fun snapshot(role: String) = request { client.api.notifications(role).legendBody() }; suspend fun markRead(role: String, id: String) = request { client.api.markNotificationRead(role, id).legendBody() }; suspend fun clearBadges(role: String) = request { client.api.clearNotificationBadges(role).legendBody() } }
class DiscoveryRepository(private val client: LegendApiClient) { suspend fun search(role: String, query: String? = null, offset: Int = 0, pageSize: Int = 24, sort: String? = null) = request { client.api.discovery(role, query, offset, pageSize, sort).legendBody() }; suspend fun profile(role: String, id: String) = request { client.api.discoveryProfile(role, id).legendBody() } }
class JourneyRepository(private val client: LegendApiClient) {
    suspend fun dashboard(role: String) = request { client.api.journeyCircles(role).legendBody() }
    suspend fun saveProfile(role: String, input: JourneyProfileInput) = request { client.api.saveJourneyProfile(role, input).legendBody() }
    suspend fun requestConnection(role: String, clientProfileId: String, reason: String? = null, introduction: String? = null) = request { client.api.requestJourneyConnection(role, JourneyConnectionRequest(clientProfileId, reason, introduction)).legendBody() }
    suspend fun respond(role: String, id: String, accept: Boolean) = request { client.api.respondJourneyConnection(role, id, JourneyConnectionResponse(accept)).legendBody() }
    suspend fun disconnect(role: String, id: String) = request { client.api.disconnectJourneyConnection(role, id).legendBody() }
    suspend fun blockProfile(role: String, id: String) = request { client.api.blockJourneyProfile(role, id).legendBody() }
    suspend fun reportProfile(role: String, id: String, category: String, detail: String? = null) = request { client.api.reportJourneyProfile(role, id, JourneyProfileReportRequest(category, detail)).legendBody() }
}
class CommunityRepository(private val client: LegendApiClient) { suspend fun block(role: String, userId: String, participantType: String) = request { client.api.block(role, CommunityBlockRequest(userId, participantType)).legendBody() }; suspend fun report(role: String, userId: String, participantType: String, targetKind: String, targetEntityId: String?, category: String, detail: String) = request { client.api.report(role, CommunityReportRequest(userId, participantType, targetKind, targetEntityId, category, detail)).legendBody() }; suspend fun openReports(role: String) = request { client.api.openCommunityReports(role).legendBody() }; suspend fun resolveReport(role: String, id: String, resolution: String) = request { client.api.resolveCommunityReport(role, id, CommunitySafetyReportResolutionRequest(resolution)).legendBody() } }
class NotificationDeviceRepository(private val client: LegendApiClient) {
    suspend fun registerFcm(role: String, token: String) = request { client.api.registerFcmDevice(role, FcmDeviceTokenRequest(token)).legendBody() }
    suspend fun deactivateFcm(role: String, token: String) = request { client.api.deactivateFcmDevice(role, FcmDeviceTokenRequest(token)).legendBody() }
}
