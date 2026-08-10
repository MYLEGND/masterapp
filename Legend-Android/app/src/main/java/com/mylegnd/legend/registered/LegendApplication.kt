package com.mylegnd.legend.registered

import android.app.Application
import com.mylegnd.legend.registered.core.auth.LegendAuthClient
import com.mylegnd.legend.registered.core.auth.MsalLegendAuthClient
import com.mylegnd.legend.registered.core.auth.SecureSessionStore
import com.mylegnd.legend.registered.core.push.FcmPushRegistrationCoordinator
import com.mylegnd.legend.registered.core.push.LegendFirebaseMessagingService
import com.mylegnd.legend.registered.core.navigation.LegendNotificationNavigation
import com.mylegnd.legend.registered.core.config.LegendRuntimeConfigurationLoader
import com.mylegnd.legend.registered.core.design.LegendDesignAuthority
import com.mylegnd.legend.registered.core.network.AccessTokenProvider
import com.mylegnd.legend.registered.core.network.LegendApiClient
import com.mylegnd.legend.registered.core.media.AuthenticatedMediaRepository
import com.mylegnd.legend.registered.core.session.SessionRepository
import com.mylegnd.legend.registered.data.*

class LegendApplication : Application() { lateinit var container: LegendContainer; private set; override fun onCreate() { super.onCreate(); LegendDesignAuthority.initialize(this); LegendFirebaseMessagingService.ensureNotificationChannel(this); container = LegendContainer(this) } }
class LegendContainer(application: Application) {
    val configuration = LegendRuntimeConfigurationLoader.load(application)
    val auth: LegendAuthClient = MsalLegendAuthClient(application, configuration)
    val notificationNavigation = LegendNotificationNavigation()
    private val sessionStore = SecureSessionStore(application)
    private val apiClient by lazy { require(configuration.isReady) { "Legend mobile configuration is incomplete." }; LegendApiClient.create(configuration.apiBaseUrl, object : AccessTokenProvider { override suspend fun accessToken() = auth.restoreAccessToken() }) }
    private val notificationDeviceRepository by lazy { NotificationDeviceRepository(apiClient) }
    val fcmPushRegistration = FcmPushRegistrationCoordinator(application, notificationDeviceRepository, sessionStore)
    val sessionRepository = SessionRepository(configuration, auth, { apiClient }, sessionStore) { fcmPushRegistration.deactivateForCurrentActor() }
    val homeRepository by lazy { HomeRepository(apiClient) }; val financialRepository by lazy { FinancialRepository(apiClient) }; val accountRepository by lazy { AccountRepository(apiClient) }
    val messagingRepository by lazy { MessagingRepository(apiClient) }; val socialRepository by lazy { SocialRepository(apiClient) }; val authenticatedMediaRepository by lazy { AuthenticatedMediaRepository(application, apiClient) }; val discoveryRepository by lazy { DiscoveryRepository(apiClient) }; val journeyRepository by lazy { JourneyRepository(apiClient) }; val communityRepository by lazy { CommunityRepository(apiClient) }
}
