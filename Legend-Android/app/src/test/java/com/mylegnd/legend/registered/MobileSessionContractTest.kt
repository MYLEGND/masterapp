package com.mylegnd.legend.registered

import com.mylegnd.legend.registered.core.model.MobileSessionResponse
import com.mylegnd.legend.registered.core.model.MobileAccountProfile
import com.mylegnd.legend.registered.core.model.ConversationMessage
import com.mylegnd.legend.registered.core.model.ConversationDetail
import com.mylegnd.legend.registered.core.model.SocialPost
import com.mylegnd.legend.registered.core.model.SocialSnapshot
import com.mylegnd.legend.registered.core.model.LegendSocialContentType
import com.mylegnd.legend.registered.core.network.JourneyDashboard
import com.mylegnd.legend.registered.core.model.FounderManagedAccount
import com.mylegnd.legend.registered.core.model.DailyScriptureManagementSnapshot
import com.mylegnd.legend.registered.core.model.CommunitySafetyReport
import com.mylegnd.legend.registered.core.model.MobileClientCreationPortalLaunch
import com.mylegnd.legend.registered.core.auth.CachedLegendSession
import com.mylegnd.legend.registered.core.auth.LegendAuthClient
import com.mylegnd.legend.registered.core.auth.LegendAuthenticatedAccount
import com.mylegnd.legend.registered.core.auth.LegendBearerTokenAuthority
import com.mylegnd.legend.registered.core.model.MobileReviewTokenResponse
import com.mylegnd.legend.registered.core.realtime.toHubUrl
import okhttp3.Request
import kotlinx.serialization.json.Json
import org.junit.Assert.*
import org.junit.Test
import java.time.Instant
import kotlinx.coroutines.runBlocking

class MobileSessionContractTest {
    private val json = Json { ignoreUnknownKeys = true }

    @Test fun `account metadata honors the shared interactive sign-in boundary`() {
        val now = Instant.parse("2026-08-14T12:00:00Z")
        val valid = CachedLegendSession(
            actorId = "member-1",
            participantType = "Client",
            displayName = "Sanitized Member",
            cachedUtc = now.toString(),
            accountId = "entra-account-1",
            interactiveSignInUtc = now.minusSeconds(89L * 24L * 60L * 60L).toString(),
        )
        val expired = valid.copy(
            interactiveSignInUtc = now.minusSeconds(90L * 24L * 60L * 60L).toString(),
        )

        assertFalse(valid.requiresInteractiveSignIn(retentionDays = 90, now = now))
        assertTrue(expired.requiresInteractiveSignIn(retentionDays = 90, now = now))
    }

    @Test fun `App Review response uses the canonical server token contract`() {
        val response = json.decodeFromString(
            MobileReviewTokenResponse.serializer(),
            """{"accessToken":"server-issued-review-token","expiresIn":900}""",
        )

        assertEquals("server-issued-review-token", response.accessToken)
        assertEquals(900, response.expiresIn)
    }

    @Test fun `one bearer authority prefers a live review credential and revokes it immediately`() = runBlocking {
        val msal = object : LegendAuthClient {
            override suspend fun restoreAccessToken(accountId: String?) = "msal-token"
            override suspend fun signIn(activity: android.app.Activity, forceReauthentication: Boolean) =
                LegendAuthenticatedAccount("account", "Member")
            override suspend fun signedInAccounts() = emptyList<LegendAuthenticatedAccount>()
            override suspend fun signOut(accountId: String?) = Unit
        }
        val authority = LegendBearerTokenAuthority(msal)

        assertEquals("msal-token", authority.accessToken())
        authority.activateReviewCredential("review-token", 900)
        assertEquals("review-token", authority.accessToken())
        authority.clearReviewCredential()
        assertEquals("msal-token", authority.accessToken())
    }
    @Test fun `session fixture preserves typed actor and server capabilities`() {
        val fixture = """{"authenticated":true,"actor":{"identity":{"userId":"sanitized-user","participantType":"Client"},"profileId":"00000000-0000-0000-0000-000000000001","displayName":"Sanitized Member","avatar":{"kind":"remote","contentType":"image/jpeg","resourcePath":"/api/v1/mobile/profile-images/Client/00000000-0000-0000-0000-000000000001"}},"permittedParticipantTypes":["Client"],"requiresParticipantSelection":false,"capabilities":{"messaging":true,"isFounder":false,"canManageScripture":false,"canManageCommunity":false},"correlationId":"sanitized-correlation"}"""
        val response = json.decodeFromString(MobileSessionResponse.serializer(), fixture)
        assertTrue(response.authenticated); assertEquals("sanitized-user", response.actor?.identity?.userId); assertEquals("Client", response.actor?.identity?.participantType); assertTrue(response.capabilities.messaging)
    }

    @Test fun `message fixture keeps server recipient body and optional original body distinct`() {
        val fixture = """{"id":"message-1","conversationId":"conversation-1","sender":{"identity":{"userId":"member-2","participantType":"Client"},"profileId":"profile-2","displayName":"Sanitized Sender"},"body":"Bonjour","originalBody":"Hello","sentUtc":"2026-01-01T00:00:00Z","isMine":false,"isDeleted":false}"""
        val message = json.decodeFromString(ConversationMessage.serializer(), fixture)
        assertEquals("Bonjour", message.body)
        assertEquals("Hello", message.originalBody)
    }

    @Test fun `account fixture preserves the server-owned translation entitlement period`() {
        val fixture = """{"participantType":"Client","profileId":"00000000-0000-0000-0000-000000000001","displayName":"Sanitized Member","isPrivate":false,"isVerified":false,"allowsConsentedTranslationLearning":true,"translationAccess":{"state":"Granted","canManage":false,"preferredCommunicationLanguage":"ht","characterAllowance":50000,"isUnlimited":false,"consumedCharacters":1200,"reservedCharacters":34,"remainingCharacters":48766,"percentUsed":2.468,"periodStartUtc":"2026-08-01T00:00:00Z","periodEndUtc":"2026-09-01T00:00:00Z","nextResetUtc":"2026-09-01T00:00:00Z","entitlementSource":"FounderCustom","isFounderOverride":true,"lastTranslationActivityUtc":"2026-08-10T01:02:03Z"}}"""
        val account = json.decodeFromString(MobileAccountProfile.serializer(), fixture)

        assertEquals("Granted", account.translationAccess?.state)
        assertEquals(50_000L, account.translationAccess?.characterAllowance)
        assertEquals(1_200L, account.translationAccess?.consumedCharacters)
        assertEquals(48_766L, account.translationAccess?.remainingCharacters)
        assertEquals("FounderCustom", account.translationAccess?.entitlementSource)
        assertTrue(account.translationAccess?.isFounderOverride == true)
        assertTrue(account.allowsConsentedTranslationLearning)
    }

    @Test fun `conversation fixture preserves the full server messaging projection`() {
        val fixture = """{"id":"conversation-1","conversationType":"Group","title":"Sanitized group","participants":[{"identity":{"userId":"member-1","participantType":"Client"},"profileId":"profile-1","displayName":"Sanitized Member","isGroupManager":true}],"messages":[{"id":"message-1","conversationId":"conversation-1","sender":{"identity":{"userId":"member-1","participantType":"Client"},"profileId":"profile-1","displayName":"Sanitized Member"},"body":"Recipient-facing body","originalBody":"Original body","sentUtc":"2026-01-01T00:00:00Z","attachments":[{"id":"attachment-1","originalFileName":"sanitized.pdf","contentType":"application/pdf","sizeBytes":42,"scanStatus":"Clean","createdUtc":"2026-01-01T00:00:01Z","canDownload":true}],"isMine":false,"isDeleted":false,"reply":{"id":"reply-1","sender":{"identity":{"userId":"member-2","participantType":"Client"},"profileId":"profile-2","displayName":"Sanitized Reply"},"body":"Context","isDeleted":false},"translation":{"originalLanguage":"en","targetLanguage":"ht","provider":"Server"}}],"isMuted":false,"isClosed":false,"canManageMembers":true,"canManageCollaborators":true,"canDeleteGroup":true,"isPromoted":true,"canManagePromotion":true,"meeting":{"host":{"identity":{"userId":"member-1","participantType":"Client"},"profileId":"profile-1","displayName":"Sanitized Member"},"linkLabel":"Weekly call","linkUrl":"https://example.invalid/meeting","schedule":{"frequency":"Weekly","weekdays":["Monday"],"localTime":"10:00","timeZoneId":"UTC"}},"canManageMeeting":true,"hasOlderMessages":true}"""
        val conversation = json.decodeFromString(ConversationDetail.serializer(), fixture)
        assertEquals("Group", conversation.conversationType)
        assertTrue(conversation.participants.single().isGroupManager)
        assertTrue(conversation.hasOlderMessages)
        assertEquals("ht", conversation.messages.single().translation?.targetLanguage)
        assertEquals("sanitized.pdf", conversation.messages.single().attachments.single().originalFileName)
        assertEquals("Weekly call", conversation.meeting?.linkLabel)
    }

    @Test fun `social fixture preserves server processing and visibility state`() {
        val fixture = """{"id":"post-1","author":{"identity":{"userId":"member-1","participantType":"Client"},"profileId":"profile-1","displayName":"Sanitized Member"},"contentType":"Post","body":"Server content","audience":"Public","commentsEnabled":true,"postedUtc":"2026-01-01T00:00:00Z","reactionCount":2,"commentCount":1,"reactedByCurrentActor":false,"followedByCurrentActor":false,"followRequestPending":false,"savedByCurrentActor":false,"repostedByCurrentActor":false,"media":[{"id":"asset-1","displayOrder":0,"mediaKind":"Video","mimeType":"video/mp4","fileSizeBytes":100,"processingState":"Ready","hasPreviewImage":true}]}"""
        val post = json.decodeFromString(SocialPost.serializer(), fixture)
        assertEquals("Ready", post.media.single().processingState)
        assertEquals("Public", post.audience)
    }

    @Test fun `Hac uses the server Reel discriminator while retaining member vocabulary`() {
        assertEquals(LegendSocialContentType.HAC, LegendSocialContentType.fromApiValue("Reel"))
        assertNull(LegendSocialContentType.fromApiValue("Hac"))
        assertEquals("Reel", LegendSocialContentType.HAC.apiValue)
    }

    @Test fun `social feed fixture keeps the server promoted group projection separate from posts`() {
        val fixture = """{"stories":[],"posts":[],"hacs":[],"activity":[],"activityCount":1,"currentProfileMetrics":null,"creatorInsights":null,"promotedGroups":[{"conversationId":"00000000-0000-0000-0000-000000000123","subject":"Sanitized group","owner":{"identity":{"userId":"owner-1","participantType":"Client"},"profileId":"profile-owner","displayName":"Sanitized Owner"},"groupAvatar":null,"activeMemberCount":4,"isJoinedByCurrentActor":false,"promotionStartedUtc":"2026-01-01T00:00:00Z"}]}"""
        val snapshot = json.decodeFromString(SocialSnapshot.serializer(), fixture)
        assertEquals("Sanitized group", snapshot.promotedGroups.single().subject)
        assertFalse(snapshot.promotedGroups.single().isJoinedByCurrentActor)
        assertTrue(snapshot.posts.isEmpty())
    }

    @Test fun `journey fixture preserves consent taxonomy and connection context`() {
        val fixture = """{"profile":{"clientProfileId":"profile-1","displayName":"Sanitized Member","introduction":"A new season","lifeStages":["Adult"],"locations":["Sanitized city"],"goals":["Growth"],"interests":["Service"],"circleCodes":["Community"],"connectionTypes":["Mentorship"],"communicationStyles":["Direct"],"accountabilityFrequencies":["Weekly"]},"preferences":{"consentAffirmed":true,"isOptedIn":true,"isDiscoverable":true,"allowSuggestions":true,"allowConnectionRequests":true},"recommendations":[],"connections":[{"id":"connection-1","profile":{"clientProfileId":"profile-2","displayName":"Sanitized Connection"},"status":"Connected","connectionReason":"Shared goal","introduction":"Hello","createdUtc":"2026-01-01T00:00:00Z"}],"requests":[],"taxonomy":{"goals":["Growth"],"circles":["Community"],"lifeStages":["Adult"],"locations":["Sanitized city"],"interests":["Service"],"connectionTypes":["Mentorship"],"communicationStyles":["Direct"],"accountabilityFrequencies":["Weekly"]}}"""
        val dashboard = json.decodeFromString(JourneyDashboard.serializer(), fixture)
        assertTrue(dashboard.preferences?.consentAffirmed == true)
        assertEquals("Shared goal", dashboard.connections.single().connectionReason)
        assertEquals("Community", dashboard.taxonomy.circles.single())
    }

    @Test fun `founder account fixture retains lifecycle and subscription state`() {
        val fixture = """{"profileId":"00000000-0000-0000-0000-000000000001","userId":"sanitized-user","participantType":"Client","displayName":"Sanitized Account","email":"member@example.invalid","lifecycleState":"Active","hasCancelableSubscription":true,"isActive":true}"""
        val account = json.decodeFromString(FounderManagedAccount.serializer(), fixture)
        assertEquals("Active", account.lifecycleState)
        assertTrue(account.hasCancelableSubscription)
    }

    @Test fun `daily scripture management fixture preserves the server business date and exact passage`() {
        val fixture = """{"businessDate":"2026-08-10","current":{"date":"2026-08-10","reference":"Psalm 100","translation":"KJV","text":"Sanitized daily passage","source":"ScheduledOverride"},"upcoming":[{"id":"override-1","displayDate":"2026-08-11","reference":"Psalm 121","translation":"KJV","passageText":"Sanitized scheduled passage","createdUtc":"2026-08-01T00:00:00Z","updatedUtc":"2026-08-01T00:00:00Z"}]}"""
        val snapshot = json.decodeFromString(DailyScriptureManagementSnapshot.serializer(), fixture)
        assertEquals("2026-08-10", snapshot.businessDate)
        assertEquals("Psalm 121", snapshot.upcoming.single().reference)
        assertEquals("Sanitized daily passage", snapshot.current.text)
    }

    @Test fun `community review fixture preserves only server-projected report state`() {
        val fixture = """{"id":"report-1","targetKind":"SocialPost","targetEntityId":"post-1","category":"Safety","detail":"Sanitized report detail","status":"Open","createdUtc":"2026-08-10T00:00:00Z","reporterParticipantType":"Client","reportedParticipantType":"Client"}"""
        val report = json.decodeFromString(CommunitySafetyReport.serializer(), fixture)
        assertEquals("SocialPost", report.targetKind)
        assertEquals("Open", report.status)
        assertEquals("Sanitized report detail", report.detail)
    }

    @Test fun `client creation portal fixture retains only the server-issued launch path`() {
        val launch = json.decodeFromString(
            MobileClientCreationPortalLaunch.serializer(),
            """{"launchPath":"/mobile/agent/clients/create?ticket=sanitized-ticket"}""",
        )

        assertEquals("/mobile/agent/clients/create?ticket=sanitized-ticket", launch.launchPath)
    }

    @Test fun `realtime hub keeps a valid OkHttp HTTP URL for WebSocket upgrade`() {
        val hubUrl = "https://api.legend.example/api/v1/mobile?ignored=true#ignored".toHubUrl()

        assertEquals("https://api.legend.example/api/v1/mobile/messaginghub", hubUrl)
        assertEquals(
            "https://api.legend.example/api/v1/mobile/messaginghub",
            Request.Builder().url(requireNotNull(hubUrl)).build().url.toString(),
        )
    }
}
