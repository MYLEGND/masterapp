# LEGEND® Android portability map

This is an implementation map, not a second product specification. `Legend-ios` and `AgentPortal/Mobile` remain the authorities.

| iOS authority | Existing server authority | Android equivalent |
| --- | --- | --- |
| `LegendApp.swift`, `RootView.swift` | `GET /api/v1/mobile/session` | `MainActivity`, `LegendRoot`, `SessionViewModel` |
| `MobileSessionCoordinator.swift` | Entra bearer policy + `session/select-role` | `SessionRepository`, `MsalLegendAuthClient` |
| `SecureTokenStore.swift` | Entra token issuer | MSAL token cache; `SecureSessionStore` encrypts only non-token cache with Android Keystore |
| `MobileHTTPClient.swift` | `/api/v1/mobile` + `MobileApiErrorResponse` | one `LegendApiClient` (Retrofit/OkHttp/Kotlin serialization) |
| `MobileHomeStore.swift` | `home`, `financial`, agent client/lead projections | `HomeRepository`, `FinancialRepository`, ViewModels |
| `MessagingStore.swift` / `MessagingModels.swift` | MessagingService mobile routes | `MessagingRepository`, `MessagingViewModel`, `MessagesScreen` |
| iOS FCM-equivalent notification flow | `NotificationEngine`, notification ledger | FCM receive + REST reconciliation; no Android notification authority |
| `MobileSocialStore.swift` / creation flow | Social feed/media routes + server media worker | `SocialRepository`, Compose creator, `SocialMediaUploader` |
| AVFoundation protected media | `social/media/{id}` | `AuthenticatedMediaRepository`, Media3 `LegendVideoPlayer` |
| `MobileDiscoveryStore.swift` | `discovery/search`, `discovery/profiles/{id}` | `DiscoveryRepository`, `DiscoveryViewModel`, Discover tab |
| `MobileJourneyCirclesStore.swift` | `journey-circles` dashboard/connections | `JourneyRepository`, Discover Journey Circles section |
| `MobileAccountStore.swift` | account/privacy/lifecycle projections | `AccountRepository`, `AccountViewModel`, account/lifecycle UI |
| `MobileCommunitySafetyStore.swift` | CommunitySafetyService | `CommunityRepository`; no local moderation rule |
| `MobileNotificationsStore.swift` | notification ledger and current APNs-only device contract | FCM receive/reconciliation boundary; Android registration awaits platform-neutral server transport |
| `LegendNextTheme.swift` | iOS visual authority | `LegendTheme.kt` semantic Compose tokens |

The current backend has no bearer-capable mobile SignalR contract. As documented by iOS, Android uses FCM wake-up and authenticated REST reconciliation; it deliberately does not connect to browser-only `/messaginghub`.
