import Foundation
import Combine
import XCTest
import UIKit
@testable import Legend

@MainActor
final class MobileNativeContractTests: XCTestCase {
    func testLocalizationValidatesEntriesWithoutDiscardingOtherTranslatedCopy() {
        func entry(revision: String = "revision1", failure: String? = nil) -> LegendApplicationLocalizedCopy {
            LegendApplicationLocalizedCopy(id: "entry", source: "Settings", text: "Anviwònman", context: "visual interface copy", sourceRevision: revision, placeholders: [], provider: "AzureTranslator", provenance: "ProviderDerived", validationState: "Observation", createdUtc: "2026-09-10T00:00:00Z", reused: true, failureCode: failure)
        }
        XCTAssertEqual(entry().validatedText(source: "Settings", context: "visual interface copy", revision: "revision1", placeholders: []), "Anviwònman")
        XCTAssertNil(entry(revision: "revision2").validatedText(source: "Settings", context: "visual interface copy", revision: "revision1", placeholders: []))
        XCTAssertNil(entry(failure: "translation_pending").validatedText(source: "Settings", context: "visual interface copy", revision: "revision1", placeholders: []))
        XCTAssertNil(entry().validatedText(source: "Other", context: "visual interface copy", revision: "revision1", placeholders: []))
    }

    func testSavedAccountAvatarRoundTripAndLegacyCompatibility() throws {
        let legacy = Data(#"{"id":"saved","displayName":"Saved member","participantType":"Client","lastUsedAt":0}"#.utf8)
        let old = try JSONDecoder().decode(MobileSignedInAccount.self, from: legacy)
        XCTAssertNil(old.avatar)
        let avatar = ProfileAvatar(kind: "Image", contentType: "image/png", resourcePath: "/api/v1/mobile/media/avatar/saved")
        let updated = MobileSignedInAccount(id: old.id, displayName: old.displayName, participantType: old.participantType, avatar: avatar)
        let restored = try JSONDecoder().decode(MobileSignedInAccount.self, from: JSONEncoder().encode(updated))
        XCTAssertEqual(restored.avatar, avatar)
        XCTAssertEqual(restored.id, old.id)
    }

    func testSocialPhotoExportSupportsZoomingOutAndBoundsPortraitResolution() throws {
        let image = UIGraphicsImageRenderer(size: CGSize(width: 300, height: 600)).image { context in
            UIColor.red.setFill()
            context.fill(CGRect(x: 0, y: 0, width: 300, height: 600))
        }
        let data = try XCTUnwrap(image.pngData())
        var edit = LegendSocialMediaEditState.initial
        edit.cropZoom = 0.5
        let output = try XCTUnwrap(LegendSocialMediaRenderer.renderedImage(from: data, edit: edit, aspectRatio: 0.5))
        XCTAssertLessThanOrEqual(max(output.size.width, output.size.height), 2048)
        XCTAssertEqual(output.size.width / output.size.height, 0.5, accuracy: 0.01)
        let cgImage = try XCTUnwrap(output.cgImage)
        var pixel = [UInt8](repeating: 0, count: 4)
        let context = try XCTUnwrap(CGContext(data: &pixel, width: 1, height: 1, bitsPerComponent: 8, bytesPerRow: 4,
            space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        context.draw(cgImage, in: CGRect(x: 0, y: 0, width: cgImage.width, height: cgImage.height))
        XCTAssertEqual(pixel[0], 0, "Zooming out must reveal the canvas instead of being clamped back to fill.")
    }

    func testOtherProfileNetworkUsesCanonicalTargetAndAuthenticatedRole() async throws {
        StubURLProtocol.responseStatus = 200
        StubURLProtocol.responseBody = Data("[]".utf8)
        defer { StubURLProtocol.responseBody = nil }
        let client = MobileHTTPClient(baseURL: URL(string: "https://api.example.test")!, session: stubSession())
        let api = URLSessionMobileSocialAPI(client: client, participantType: .agent)
        let profile = MobileSocialAuthor(identity: try LogicalParticipantIdentity(userID: "client-1", participantType: .client),
            profileID: "profile-1", displayName: "Client", avatar: nil)
        _ = try await api.currentProfileFollowList(kind: .followers, profile: profile, accessToken: "test-token")
        let request = try XCTUnwrap(StubURLProtocol.lastRequest)
        XCTAssertEqual(request.url?.path, "/api/v1/mobile/social/profiles/follows")
        XCTAssertEqual(request.value(forHTTPHeaderField: "X-Legend-Participant-Type"), "Agent")
        let items = try XCTUnwrap(URLComponents(url: XCTUnwrap(request.url), resolvingAgainstBaseURL: false)?.queryItems)
        let query = Dictionary(uniqueKeysWithValues: items.map { ($0.name, $0.value ?? "") })
        XCTAssertEqual(query["userId"], "client-1")
        XCTAssertEqual(query["participantType"], "Client")
        XCTAssertEqual(query["profileId"], "profile-1")
        XCTAssertEqual(query["list"], "followers")
    }

    func testCrmSearchMatchesNameEmailAndFormattedPhone() {
        let values: [String?] = ["Ana García", "ana@example.com", "(602) 555-0123"]
        XCTAssertTrue(legendCrmMatchesSearch("ana garcia", values: values))
        XCTAssertTrue(legendCrmMatchesSearch("@example.com", values: values))
        XCTAssertTrue(legendCrmMatchesSearch("602555", values: values))
        XCTAssertFalse(legendCrmMatchesSearch("missing", values: values))
    }

    func testCrmContactEditPreservesTheExactServerRevision() throws {
        let json = #"{"id":"lead-1","kind":"Lead","displayName":"A Lead","firstName":"A","lastName":"Lead","stage":"NewLead","managementPath":"/Leads?leadId=lead-1","updatedUtc":"2026-09-07T01:02:03.1234567Z","archived":false}"#
        let record = try JSONDecoder().decode(MobileCrmRecord.self, from: Data(json.utf8))
        let encoded = try JSONEncoder().encode(MobileCrmContactInput(record))
        let saved = try XCTUnwrap(JSONSerialization.jsonObject(with: encoded) as? [String: Any])
        XCTAssertEqual(saved["updatedUtc"] as? String, "2026-09-07T01:02:03.1234567Z")
        XCTAssertEqual(saved["firstName"] as? String, "A")
        XCTAssertEqual(record.archived, false)
    }

    func testSignedAPNSEnvironmentMapsOnlyTheAppEntitlementValues() {
        XCTAssertEqual(
            LegendAPNSEnvironment.fromSignedEntitlement("development"),
            .sandbox)
        XCTAssertEqual(
            LegendAPNSEnvironment.fromSignedEntitlement("production"),
            .production)
        XCTAssertNil(LegendAPNSEnvironment.fromSignedEntitlement("staging"))
        XCTAssertNil(LegendAPNSEnvironment.fromSignedEntitlement(nil))
    }

    func testVerificationBadgeIsLimitedToTheProfileImageIdentitySurface() {
        XCTAssertTrue(
            LegendVerifiedBadgePlacement.alongsideProfileImage
                .displaysBadge(for: true))
        XCTAssertFalse(
            LegendVerifiedBadgePlacement.none.displaysBadge(for: true))
        XCTAssertFalse(
            LegendVerifiedBadgePlacement.alongsideProfileImage
                .displaysBadge(for: false))
    }

    func testSharedScrollChromeHidesAllActionChromeDownwardAndRestoresItUpward() {
        let chrome = LegendScrollChrome()

        chrome.record(verticalDragTranslation: -1.1)
        XCTAssertFalse(chrome.isBottomNavigationVisible)

        chrome.record(verticalDragTranslation: 0.6)
        XCTAssertTrue(chrome.isBottomNavigationVisible)
    }

    func testPKCEUsesAS256ChallengeAndAuthorizationRequestUsesStandardParameters() throws {
        let pkce = try PKCEChallenge.create()
        XCTAssertGreaterThanOrEqual(pkce.verifier.count, 43)
        XCTAssertFalse(pkce.challenge.contains("="))

        let request = OAuthAuthorizationRequest(
            authorizationEndpoint: URL(string: "https://login.example.test/authorize")!,
            clientID: "public-client",
            redirectScheme: "com-mylegnd-legend-registered",
            scope: "openid profile api://legend/mobile_access",
            state: "expected-state",
            pkce: pkce)
        let query = try XCTUnwrap(URLComponents(url: request.url(), resolvingAgainstBaseURL: false)?.queryItems)

        XCTAssertEqual(query.first(where: { $0.name == "code_challenge_method" })?.value, "S256")
        XCTAssertEqual(query.first(where: { $0.name == "state" })?.value, "expected-state")
        XCTAssertNil(query.first(where: { $0.name == "audience" }))
        XCTAssertEqual(query.first(where: { $0.name == "prompt" })?.value, "login")
    }

    func testCallbackValidationRequiresExactSchemePathAndState() throws {
        let valid = URL(string: "com-mylegnd-legend-registered://oauth/callback?code=auth-code&state=expected")!
        XCTAssertEqual(
            try OAuthCallbackValidator.authorizationCode(
                from: valid,
                redirectScheme: "com-mylegnd-legend-registered",
                expectedState: "expected"),
            "auth-code")

        XCTAssertThrowsError(try OAuthCallbackValidator.authorizationCode(
            from: URL(string: "com-mylegnd-legend-registered://oauth/callback?code=auth-code&state=wrong")!,
            redirectScheme: "com-mylegnd-legend-registered",
            expectedState: "expected"))
        XCTAssertThrowsError(try OAuthCallbackValidator.authorizationCode(
            from: URL(string: "other-scheme://oauth/callback?code=auth-code&state=expected")!,
            redirectScheme: "com-mylegnd-legend-registered",
            expectedState: "expected"))
    }

    func testSessionAndConversationDTOsDecodeTheServerContract() throws {
        let sessionData = Data("""
        {
          "authenticated": true,
          "actor": {
            "identity": { "userId": "same-oid", "participantType": "Agent" },
            "profileId": "00000000-0000-0000-0000-000000000001",
            "displayName": "Agent One",
            "avatar": { "kind": "inline", "contentType": "image/png", "base64Content": "aW1hZ2U=" }
          },
          "permittedParticipantTypes": ["Agent", "Client"],
          "requiresParticipantSelection": true,
          "capabilities": { "messaging": true },
          "correlationId": "correlation-1"
        }
        """.utf8)
        let session = try JSONDecoder.mobile.decode(MobileBootstrapResponse.self, from: sessionData)
        XCTAssertTrue(session.authenticated)
        XCTAssertEqual(session.actor?.identity.userID, "same-oid")
        XCTAssertEqual(session.permittedParticipantTypes, [.agent, .client])
        XCTAssertTrue(session.requiresParticipantSelection)
        XCTAssertTrue(session.capabilities.messaging)
        XCTAssertEqual(session.actor?.avatar?.imageData, Data("image".utf8))

        let conversationData = Data("""
        {
          "id": "00000000-0000-0000-0000-000000000010",
          "title": "Secure conversation",
          "conversationType": "ClientAgent",
          "participants": [{
            "identity": { "userId": "same-oid", "participantType": "Client" },
            "profileId": "00000000-0000-0000-0000-000000000002",
            "displayName": "Client One",
            "avatar": null
          }],
          "messages": [{
            "id": "00000000-0000-0000-0000-000000000011",
            "conversationId": "00000000-0000-0000-0000-000000000010",
            "sender": {
              "identity": { "userId": "same-oid", "participantType": "Client" },
              "profileId": "00000000-0000-0000-0000-000000000002",
              "displayName": "Client One",
              "avatar": null
            },
            "body": "Hello",
            "sentUtc": "2026-07-25T20:00:00Z",
            "attachments": [],
            "isMine": false
          }],
          "isMuted": false,
          "isClosed": false,
          "canManageMembers": false
        }
        """.utf8)
        let conversation = try JSONDecoder.mobile.decode(ConversationDetail.self, from: conversationData)
        XCTAssertEqual(conversation.participants.first?.identity.participantType, .client)
        XCTAssertEqual(conversation.messages.first?.conversationID, conversation.id)
        XCTAssertFalse(conversation.messages.first?.isMine ?? true)
    }

    func testCreateGroupRequestEncodesThePreparedGroupImage() throws {
        let request = CreateMessagingGroupRequest(
            subject: "Family protection",
            participants: [
                MessagingGroupMemberRequest(
                    userID: "client-1",
                    participantType: .client)
            ],
            initialMessageBody: nil,
            groupImage: MessagingGroupImageRequest(
                contentType: "image/jpeg",
                base64Content: "AQID"),
            meeting: nil)

        let payload = try XCTUnwrap(
            JSONSerialization.jsonObject(
                with: JSONEncoder.mobile.encode(request)) as? [String: Any])
        let image = try XCTUnwrap(payload["groupImage"] as? [String: Any])

        XCTAssertEqual(image["contentType"] as? String, "image/jpeg")
        XCTAssertEqual(image["base64Content"] as? String, "AQID")
    }

    func testJSONDecoderAcceptsAspNetCoreFractionalSecondUtcTimestamps() throws {
        let data = Data("""
        {
          "id": "00000000-0000-0000-0000-000000000011",
          "conversationId": "00000000-0000-0000-0000-000000000010",
          "sender": {
            "identity": { "userId": "agent-oid", "participantType": "Agent" },
            "profileId": "00000000-0000-0000-0000-000000000001",
            "displayName": "Agent One",
            "avatar": null
          },
          "body": "Hello",
          "sentUtc": "2026-07-26T11:27:01.1234567Z",
          "attachments": [],
          "isMine": true
        }
        """.utf8)

        let message = try JSONDecoder.mobile.decode(ConversationMessage.self, from: data)

        XCTAssertEqual(message.sentUTC.timeIntervalSince1970, 1_785_065_221.1234567, accuracy: 0.001)
    }

    func testConversationMessagePreservesTheServerProvidedTranslationPresentation() throws {
        let data = Data("""
        {
          "id": "00000000-0000-0000-0000-000000000011",
          "conversationId": "00000000-0000-0000-0000-000000000010",
          "sender": {
            "identity": { "userId": "agent-oid", "participantType": "Agent" },
            "profileId": "00000000-0000-0000-0000-000000000001",
            "displayName": "Agent One",
            "avatar": null
          },
          "body": "Èske ou resevwa tout bagay ki te klase epi rezoud?",
          "originalBody": "Did you get everything all sorted out and resolved?",
          "translation": {
            "originalLanguage": "en",
            "targetLanguage": "ht",
            "provider": "AzureTranslator"
          },
          "sentUtc": "2026-08-03T00:31:53Z",
          "attachments": [],
          "isMine": false
        }
        """.utf8)

        let message = try JSONDecoder.mobile.decode(ConversationMessage.self, from: data)

        XCTAssertEqual(message.body, "Èske ou resevwa tout bagay ki te klase epi rezoud?")
        XCTAssertEqual(message.originalBody, "Did you get everything all sorted out and resolved?")
        XCTAssertEqual(message.translation?.originalLanguage, "en")
        XCTAssertEqual(message.translation?.targetLanguage, "ht")
    }

    func testMessageAttachmentDTOUsesTheServerScanState() throws {
        let data = Data("""
        {
          "id": "00000000-0000-0000-0000-000000000011",
          "conversationId": "00000000-0000-0000-0000-000000000010",
          "sender": {
            "identity": { "userId": "agent-oid", "participantType": "Agent" },
            "profileId": "00000000-0000-0000-0000-000000000001",
            "displayName": "Agent One",
            "avatar": null
          },
          "body": "Your plan is attached.",
          "sentUtc": "2026-07-26T11:27:01Z",
          "attachments": [{
            "id": "00000000-0000-0000-0000-000000000012",
            "originalFileName": "plan.pdf",
            "contentType": "application/pdf",
            "sizeBytes": 512,
            "scanStatus": "Pending",
            "createdUtc": "2026-07-26T11:27:01Z",
            "canDownload": false
          }],
          "isMine": true
        }
        """.utf8)

        let message = try JSONDecoder.mobile.decode(ConversationMessage.self, from: data)
        let attachment = try XCTUnwrap(message.attachments.first)

        XCTAssertEqual(attachment.originalFileName, "plan.pdf")
        XCTAssertEqual(attachment.scanStatus, "Pending")
        XCTAssertFalse(attachment.canDownload)
    }

    func testJSONDecoderTreatsAZoneLessAspNetCoreUtcFieldAsUtc() throws {
        let data = Data("""
        {
          "id": "00000000-0000-0000-0000-000000000011",
          "conversationId": "00000000-0000-0000-0000-000000000010",
          "sender": {
            "identity": { "userId": "agent-oid", "participantType": "Agent" },
            "profileId": "00000000-0000-0000-0000-000000000001",
            "displayName": "Agent One",
            "avatar": null
          },
          "body": "Hello",
          "sentUtc": "2026-07-26T11:27:01.1234567",
          "attachments": [],
          "isMine": true
        }
        """.utf8)

        let message = try JSONDecoder.mobile.decode(ConversationMessage.self, from: data)

        XCTAssertEqual(message.sentUTC.timeIntervalSince1970, 1_785_065_221.1234567, accuracy: 0.001)
    }

    func testSendRequestEncodingDoesNotContainSenderOrParticipantIdentity() throws {
        let clientMessageID = UUID()
        let data = try JSONEncoder.mobile.encode(SendMessageRequest(body: "Secure hello", replyToMessageID: nil, clientMessageID: clientMessageID))
        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])

        XCTAssertEqual(object["body"] as? String, "Secure hello")
        XCTAssertEqual(object["clientMessageId"] as? String, clientMessageID.uuidString)
        XCTAssertEqual(Set(object.keys), Set(["body", "clientMessageId"]))
    }

    func testAccountAndJourneyContractsKeepProfileFieldsAndSelectionsTyped() throws {
        let accountData = Data("""
        {
          "participantType": "Client",
          "profileId": "00000000-0000-0000-0000-000000000123",
          "displayName": "Client Identity",
          "email": "hello@example.test",
          "phone": "555-0100",
          "title": null,
          "shortBio": null,
          "profileEmail": "hello@example.test",
          "isEmailVisible": true,
          "isPhoneVisible": true,
          "username": "client.legend",
          "bio": "Building a legacy.",
          "website": "https://legend.example.test",
          "location": "Phoenix, Arizona",
          "allowsConsentedTranslationLearning": true,
          "translationAccess": {
            "state": "Granted",
            "canManage": false,
            "preferredCommunicationLanguage": "ht",
            "characterAllowance": 50000,
            "isUnlimited": false,
            "consumedCharacters": 1200,
            "reservedCharacters": 34,
            "remainingCharacters": 48766,
            "percentUsed": 2.468,
            "periodStartUtc": "2026-08-01T00:00:00Z",
            "periodEndUtc": "2026-09-01T00:00:00Z",
            "nextResetUtc": "2026-09-01T00:00:00Z",
            "entitlementSource": "FounderCustom",
            "isFounderOverride": true,
            "lastTranslationActivityUtc": "2026-08-10T01:02:03Z"
          },
          "avatar": { "kind": "inline", "contentType": "image/png", "base64Content": "Y2xpZW50" }
        }
        """.utf8)
        let account = try JSONDecoder.mobile.decode(MobileAccountProfile.self, from: accountData)
        XCTAssertEqual(account.participantType, .client)
        XCTAssertEqual(account.profileEmail, "hello@example.test")
        XCTAssertTrue(account.isEmailVisible)
        XCTAssertTrue(account.isPhoneVisible)
        XCTAssertEqual(account.username, "client.legend")
        XCTAssertEqual(account.avatar?.imageData, Data("client".utf8))
        XCTAssertTrue(account.translationAccess.isGranted)
        XCTAssertEqual(account.translationAccess.characterAllowance, 50_000)
        XCTAssertEqual(account.translationAccess.consumedCharacters, 1_200)
        XCTAssertEqual(account.translationAccess.remainingCharacters, 48_766)
        XCTAssertEqual(account.translationAccess.entitlementSource, "FounderCustom")
        XCTAssertTrue(account.translationAccess.isFounderOverride)
        XCTAssertTrue(account.allowsConsentedTranslationLearning)

        let accountUpdate = MobileAccountUpdate(
            displayName: "Client Identity",
            phone: "555-0100",
            title: nil,
            shortBio: nil,
            username: "client.legend",
            bio: "Building a legacy.",
            website: "https://legend.example.test",
            location: "Phoenix, Arizona",
            publicEmail: "hello@example.test",
            isEmailVisible: true,
            isPhoneVisible: true)
        let accountUpdateObject = try XCTUnwrap(
            JSONSerialization.jsonObject(with: JSONEncoder.mobile.encode(accountUpdate)) as? [String: Any])
        XCTAssertEqual(accountUpdateObject["publicEmail"] as? String, "hello@example.test")
        XCTAssertEqual(accountUpdateObject["isEmailVisible"] as? Bool, true)
        XCTAssertEqual(accountUpdateObject["isPhoneVisible"] as? Bool, true)

        let input = MobileJourneyProfileInput(
            consentAffirmed: true,
            isOptedIn: true,
            isDiscoverable: true,
            allowSuggestions: true,
            allowConnectionRequests: true,
            introduction: "Building a legacy.",
            lifeStages: ["Business ownership"],
            locations: ["Southwest"],
            goals: ["Growing a business"],
            interests: ["Leadership"],
            circleCodes: ["Entrepreneurs Circle"],
            connectionTypes: ["Business peer"],
            communicationStyles: ["Detailed planning"],
            accountabilityFrequencies: ["Weekly"])
        let encoded = try JSONEncoder.mobile.encode(input)
        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: encoded) as? [String: Any])
        XCTAssertEqual(object["introduction"] as? String, "Building a legacy.")
        XCTAssertEqual(object["goals"] as? [String], ["Growing a business"])
        XCTAssertNil(object["email"])
    }

    func testMobileHTTPClientMapsUnauthorizedAndForbiddenResponses() async throws {
        StubURLProtocol.responseStatus = 401
        let unauthorizedClient = MobileHTTPClient(baseURL: URL(string: "https://api.example.test")!, session: stubSession())
        do {
            let _: MobileBootstrapResponse = try await unauthorizedClient.get("/api/v1/mobile/session", accessToken: "token", response: MobileBootstrapResponse.self)
            XCTFail("Expected an unauthorized error")
        } catch let error as MobileAPIError {
            XCTAssertEqual(error, .apiUnauthorized(code: "mobile_authentication_required", correlationID: "test-correlation"))
        }

        StubURLProtocol.responseStatus = 403
        let forbiddenClient = MobileHTTPClient(baseURL: URL(string: "https://api.example.test")!, session: stubSession())
        do {
            let _: MobileBootstrapResponse = try await forbiddenClient.get("/api/v1/mobile/session", accessToken: "token", response: MobileBootstrapResponse.self)
            XCTFail("Expected a forbidden error")
        } catch let error as MobileAPIError {
            XCTAssertEqual(error, .apiForbidden(code: "mobile_access_forbidden", correlationID: "test-correlation"))
        }
    }

    func testGuestRequestDoesNotSendAccountCredentials() async throws {
        StubURLProtocol.responseStatus = 200
        StubURLProtocol.responseBody = Data(#"{"title":"Explore Legend","subtitle":"Welcome","introduction":"Public reading","readings":[],"guides":[],"accountTitle":"Your account","accountDescription":"Sign in","links":[]}"#.utf8)
        defer { StubURLProtocol.responseBody = nil }
        let client = MobileHTTPClient(baseURL: URL(string: "https://api.example.test")!, session: stubSession())
        let content = try await client.getPublic("/api/v1/mobile/guest", response: MobileGuestSnapshot.self)
        XCTAssertEqual(content.title, "Explore Legend")
        let request = try XCTUnwrap(StubURLProtocol.lastRequest)
        XCTAssertEqual(request.url?.path, "/api/v1/mobile/guest")
        XCTAssertNil(request.value(forHTTPHeaderField: "Authorization"))
        XCTAssertNil(request.value(forHTTPHeaderField: "X-Legend-Participant-Type"))
        XCTAssertFalse(request.httpShouldHandleCookies)
    }

    func testMobileHTTPClientBoundsProtectedMediaDownloads() async throws {
        StubURLProtocol.responseStatus = 200
        StubURLProtocol.lastRequestTimeout = nil

        let client = MobileHTTPClient(
            baseURL: URL(string: "https://api.example.test")!,
            session: stubSession())
        let data = try await client.getData(
            "/api/v1/mobile/social/media/00000000-0000-0000-0000-000000000001",
            accessToken: "token")

        XCTAssertTrue(data.isEmpty)
        XCTAssertEqual(StubURLProtocol.lastRequestTimeout, 20)
    }

    func testMobileHTTPClientPreservesVersionQueriesOnProtectedResourcePaths() async throws {
        StubURLProtocol.responseStatus = 200
        let client = MobileHTTPClient(
            baseURL: URL(string: "https://api.example.test/mobile")!,
            session: stubSession())
        let resources = [
            (
                "/api/v1/mobile/profile-images/Agent/81d5d665-7e00-4675-9903-4d0f27ab6315?v=4df3214380552811",
                "/mobile/api/v1/mobile/profile-images/Agent/81d5d665-7e00-4675-9903-4d0f27ab6315",
                "4df3214380552811"
            ),
            (
                "/api/v1/mobile/messaging/conversations/17aa8133-5551-465e-bb04-88f2e22f1502/image?v=9fe451fd95809b94",
                "/mobile/api/v1/mobile/messaging/conversations/17aa8133-5551-465e-bb04-88f2e22f1502/image",
                "9fe451fd95809b94"
            )
        ]

        for (resourcePath, expectedPath, expectedVersion) in resources {
            StubURLProtocol.lastRequestURL = nil

            _ = try await client.getData(resourcePath, accessToken: "token")

            let requestURL = try XCTUnwrap(StubURLProtocol.lastRequestURL)
            let components = try XCTUnwrap(
                URLComponents(url: requestURL, resolvingAgainstBaseURL: false))

            XCTAssertEqual(components.path, expectedPath)
            XCTAssertEqual(
                components.queryItems,
                [URLQueryItem(name: "v", value: expectedVersion)])
            XCTAssertFalse(requestURL.absoluteString.contains("%3F"))
        }
    }

    func testProtectedImageRequestsUseTheActiveParticipantRole() throws {
        let actor = try MobileActor(
            identity: LogicalParticipantIdentity(
                userID: "agent-user",
                participantType: .agent),
            profileID: "00000000-0000-0000-0000-000000000001",
            displayName: "Agent User",
            avatar: nil)
        let headers = MobileSessionCoordinator.protectedImageHeaders(
            for: .authenticated(MobileSession(
                actor: actor,
                capabilities: ["messaging"])))

        XCTAssertEqual(headers, ["X-Legend-Participant-Type": "Agent"])
        XCTAssertEqual(
            MobileSessionCoordinator.protectedImageHeaders(for: .signedOut),
            [:])
    }

    func testSignOutClearsTheKeychainAbstraction() {
        let store = InMemoryTokenStore()
        let coordinator = MobileSessionCoordinator(
            configuration: completeConfiguration(),
            tokenStore: store,
            authorizer: TestAuthorizer(),
            tokenExchanger: TestTokenExchanger())

        coordinator.signOut()

        XCTAssertTrue(store.didClear)
        XCTAssertEqual(coordinator.state, .signedOut)
    }

    func testAuthorizedDualRoleSwitchReusesTheCurrentBearerWithoutSigningOut() async throws {
        let store = InMemoryTokenStore(
            storedTokens: OAuthTokenSet(
                accessToken: "stored-access-token",
                refreshToken: "stored-refresh-token",
                expiresAt: .distantFuture))
        let service = try DualRoleSessionService()
        let coordinator = MobileSessionCoordinator(
            configuration: completeConfiguration(),
            tokenStore: store,
            authorizer: TestAuthorizer(),
            tokenExchanger: TestTokenExchanger(),
            sessionService: service)

        coordinator.restore()
        try await waitForState(
            coordinator,
            matching: { state in
                if case .roleSelection = state { return true }
                return false
            })

        coordinator.selectRole(.agent)
        let agentSession = try await waitForAuthenticatedSession(
            coordinator,
            participantType: .agent)
        XCTAssertEqual(agentSession.alternateParticipantTypes, [.client])

        coordinator.switchToRole(.client)
        let clientSession = try await waitForAuthenticatedSession(
            coordinator,
            participantType: .client)
        XCTAssertEqual(clientSession.alternateParticipantTypes, [.agent])
        XCTAssertFalse(store.didClear)
        let requestedRoles = await service.requestedRoles()
        XCTAssertEqual(requestedRoles, [.agent, .client])
    }

    func testFaceIDRestoreReopensTheLastDualRoleAccountWithoutRoleSelection() async throws {
        let cache = LegendLaunchCache(
            directoryName: "MobileNativeContractTests-\(UUID().uuidString)")
        defer { cache.clear() }

        let cachedActor = try MobileActor(
            identity: LogicalParticipantIdentity(
                userID: "shared-entra-oid",
                participantType: .agent),
            profileID: "00000000-0000-0000-0000-000000000001",
            displayName: "Agent Account",
            avatar: nil)
        cache.writeSession(MobileSessionCacheEntry(
            actor: cachedActor,
            capabilities: ["messaging"],
            permittedParticipantTypes: [.agent, .client],
            cachedUtc: Date(),
            credentialFingerprint: LegendSessionCredentialFingerprint.make(
                from: OAuthTokenSet(
                    accessToken: "stored-access-token",
                    refreshToken: "stored-refresh-token",
                    expiresAt: .distantFuture))))

        let store = InMemoryTokenStore(
            storedTokens: OAuthTokenSet(
                accessToken: "stored-access-token",
                refreshToken: "stored-refresh-token",
                expiresAt: .distantFuture))
        let service = try DualRoleSessionService()
        let coordinator = MobileSessionCoordinator(
            configuration: completeConfiguration(),
            tokenStore: store,
            authorizer: TestAuthorizer(),
            tokenExchanger: TestTokenExchanger(),
            sessionService: service,
            launchCache: cache,
            biometricSecurity: AcceptingBiometricSecurity())

        coordinator.restore()

        let restored = try await waitForAuthenticatedSession(
            coordinator,
            participantType: .agent)
        XCTAssertEqual(restored.actor.displayName, "Agent Account")
        let requestedRoles = await service.requestedRoles()
        XCTAssertEqual(requestedRoles, [.agent])
        XCTAssertFalse(store.didClear)
    }

    func testMessagingStoreTransitionsFromLoadingToLoaded() async {
        let store = MessagingStore(
            api: StubMessagingAPI(),
            accessTokenProvider: { "token" },
            diagnostics: LegendDiagnostics(),
            actorParticipantType: .client)

        store.load()
        try? await Task.sleep(for: .milliseconds(50))

        guard case .loaded(let conversations) = store.state else {
            return XCTFail("Expected a loaded messaging state")
        }
        XCTAssertTrue(conversations.isEmpty)
    }

    func testMessagingStoreReloadsTheServerInboxWhenMessagesTabIsReentered() async {
        let conversationID = UUID()
        let api = InboxReconciliationMessagingAPI(conversationID: conversationID)
        let store = MessagingStore(
            api: api,
            accessTokenProvider: { "token" },
            diagnostics: LegendDiagnostics(),
            actorParticipantType: .client)

        store.load()
        try? await Task.sleep(for: .milliseconds(50))
        guard case .loaded(let initial) = store.state else {
            return XCTFail("Expected the initial server inbox")
        }
        XCTAssertTrue(initial.isEmpty)

        api.makeConversationVisible()
        store.load()
        try? await Task.sleep(for: .milliseconds(50))

        guard case .loaded(let refreshed) = store.state else {
            return XCTFail("Expected the refreshed server inbox")
        }
        XCTAssertEqual(refreshed.map(\.id), [conversationID])
        XCTAssertGreaterThanOrEqual(api.conversationListCallCount, 2)
    }

    func testMessageRetryReusesIdentityAfterLostAcknowledgementAndNewMessageGetsFreshIdentity() async {
        let api = InboxReconciliationMessagingAPI(conversationID: UUID())
        let store = MessagingStore(api: api, accessTokenProvider: { "token" },
            diagnostics: LegendDiagnostics(), actorParticipantType: .client)
        let started = expectation(description: "Conversation opened")
        store.startConversation(with: api.recipient) { _ in started.fulfill() }
        await fulfillment(of: [started], timeout: 1)
        api.failNextSendAfterPersistence = true
        let failed = await store.send(body: "Delivery acknowledgement may be lost")
        XCTAssertNil(failed)
        XCTAssertNotNil(store.sendFailure)
        let retried = await store.send(body: "Delivery acknowledgement may be lost")
        XCTAssertNotNil(retried)
        XCTAssertEqual(api.sentClientMessageIDs.count, 2)
        XCTAssertEqual(api.sentClientMessageIDs[0], api.sentClientMessageIDs[1])
        XCTAssertEqual(api.persistedMessages.count, 1)
        let another = await store.send(body: "Delivery acknowledgement may be lost")
        XCTAssertNotNil(another)
        XCTAssertNotEqual(api.sentClientMessageIDs[1], api.sentClientMessageIDs[2])
        XCTAssertEqual(api.persistedMessages.count, 2)
        api.failNextSendAfterPersistence = true
        _ = await store.send(body: "Uncertain first payload")
        _ = await store.send(body: "A deliberately changed payload")
        XCTAssertNotEqual(api.sentClientMessageIDs[3], api.sentClientMessageIDs[4])
    }

    func testMessagingStoreFirstSuccessfulSendReconcilesPreviousMessagesFromServer() async {
        let conversationID = UUID()
        let api = InboxReconciliationMessagingAPI(conversationID: conversationID)
        let store = MessagingStore(
            api: api,
            accessTokenProvider: { "token" },
            diagnostics: LegendDiagnostics(),
            actorParticipantType: .client)

        store.load()
        try? await Task.sleep(for: .milliseconds(50))

        let recipient = api.recipient
        let started = expectation(description: "Starts blank canonical conversation")
        store.startConversation(with: recipient) { id in
            XCTAssertEqual(id, conversationID)
            started.fulfill()
        }
        await fulfillment(of: [started], timeout: 1)

        guard case .loaded(let beforeFirstMessage) = store.state else {
            return XCTFail("Expected a loaded inbox before first message")
        }
        XCTAssertTrue(beforeFirstMessage.isEmpty)

        let sent = await store.send(body: "First persisted message")
        XCTAssertNotNil(sent)

        // send() persists the message first, then reconciles Previous Messages
        // from the server-owned inbox in a separate task. Wait for that
        // authoritative reconciliation instead of racing the task scheduler.
        let reconciliationDeadline = ContinuousClock.now.advanced(by: .seconds(1))
        while ContinuousClock.now < reconciliationDeadline {
            if case .loaded(let conversations) = store.state,
               conversations.map(\.id) == [conversationID],
               conversations.first?.lastMessagePreview == "First persisted message" {
                return
            }

            try? await Task.sleep(for: .milliseconds(10))
        }

        guard case .loaded(let afterFirstMessage) = store.state else {
            return XCTFail("Expected the server-authoritative inbox after first message")
        }
        XCTAssertEqual(afterFirstMessage.map(\.id), [conversationID])
        XCTAssertEqual(afterFirstMessage.first?.lastMessagePreview, "First persisted message")
    }

    func testIncomingActivityDuringInboxRequestFetchesTheNewerServerSnapshot() async throws {
        let id = UUID()
        let api = InboxReconciliationMessagingAPI(conversationID: id)
        api.holdNextInboxSnapshot = true
        let realtime = RecordingMessagingRealtimeTransport()
        let store = MessagingStore(api: api, accessTokenProvider: { "token" },
            diagnostics: LegendDiagnostics(), actorParticipantType: .client, realtime: realtime)
        store.load()
        while api.releaseInboxSnapshot == nil { await Task.yield() }
        api.makeConversationVisible()
        realtime.publish(MobileMessagingRealtimeEvent(
            conversationID: id, messageID: UUID(), unreadCount: 1, revision: 1, occurredUTC: .now))
        // Let reconciliation join the held request before completing its stale snapshot.
        try await Task.sleep(for: .milliseconds(30))
        api.releaseInboxSnapshot?.resume()
        api.releaseInboxSnapshot = nil
        let deadline = ContinuousClock.now.advanced(by: .seconds(1))
        while ContinuousClock.now < deadline {
            if case .loaded(let rows) = store.state, rows.map(\.id) == [id] {
                XCTAssertEqual(api.conversationListCallCount, 2)
                return
            }
            await Task.yield()
        }
        XCTFail("An event arriving during the request must not leave the old inbox visible")
    }

    func testMessagingRecipientSearchUsesItsRecentCacheBeforeRevalidation() async throws {
        let api = InboxReconciliationMessagingAPI(conversationID: UUID())
        let store = MessagingStore(
            api: api,
            accessTokenProvider: { "token" },
            diagnostics: LegendDiagnostics(),
            actorParticipantType: .client)

        store.searchRecipients("agent")
        try await Task.sleep(for: .milliseconds(180))
        XCTAssertEqual(api.recipientCallCount, 1)

        store.searchRecipients("agent")

        // Cache application is synchronous; the second server call is only a
        // background authority check after the short debounce.
        guard case .loaded(let recipients) = store.recipientState else {
            return XCTFail("Expected cached recipients immediately")
        }
        XCTAssertEqual(recipients, [api.recipient])

        try await Task.sleep(for: .milliseconds(180))
        XCTAssertEqual(api.recipientCallCount, 2)
    }

    func testMessagingStoreReconcilesServerStateForRealtimeEvents() async throws {
        let conversationID = UUID()
        let api = InboxReconciliationMessagingAPI(conversationID: conversationID)
        let realtime = RecordingMessagingRealtimeTransport()
        let store = MessagingStore(
            api: api,
            accessTokenProvider: { "token" },
            diagnostics: LegendDiagnostics(),
            actorParticipantType: .client,
            realtime: realtime)

        store.load()
        try await Task.sleep(for: .milliseconds(80))
        XCTAssertGreaterThanOrEqual(realtime.startCount, 1)
        XCTAssertEqual(api.conversationListCallCount, 1)

        api.makeConversationVisible()
        realtime.publish(MobileMessagingRealtimeEvent(
            conversationID: conversationID,
            messageID: UUID(),
            occurredUTC: .now))
        try await Task.sleep(for: .milliseconds(100))

        guard case .loaded(let conversations) = store.state else {
            return XCTFail("Expected a server-reconciled inbox")
        }
        XCTAssertEqual(conversations.map(\.id), [conversationID])
        XCTAssertGreaterThanOrEqual(api.conversationListCallCount, 2)
    }


    func testMessagingReconnectReloadsMessagesMissedWithoutAConversationEvent() async throws {
        let conversationID = UUID()
        let api = InboxReconciliationMessagingAPI(conversationID: conversationID)
        let realtime = RecordingMessagingRealtimeTransport()
        let store = MessagingStore(
            api: api, accessTokenProvider: { "token" },
            diagnostics: LegendDiagnostics(), actorParticipantType: .client,
            realtime: realtime)
        _ = await store.refresh()
        api.makeConversationVisible()
        realtime.publish(MobileMessagingRealtimeEvent(
            conversationID: nil, messageID: nil,
            requiresResync: true, occurredUTC: .now))
        try await Task.sleep(for: .milliseconds(100))
        guard case .loaded(let conversations) = store.state else {
            return XCTFail("Expected reconciled inbox after reconnect")
        }
        XCTAssertEqual(conversations.map(\.id), [conversationID])
    }

    func testLanguageRefreshReplacesTheOpenConversationWithServerPresentation() async throws {
        let id = UUID()
        let api = InboxReconciliationMessagingAPI(conversationID: id)
        let store = MessagingStore(api: api, accessTokenProvider: { "token" }, diagnostics: LegendDiagnostics(), actorParticipantType: .client)
        _ = try await api.send(conversationID: id, body: "Bonjou", replyToMessageID: nil, clientMessageID: UUID(), accessToken: "token")
        store.openConversation(id)
        try await Task.sleep(for: .milliseconds(80))
        _ = try await api.send(conversationID: id, body: "Hello", replyToMessageID: nil, clientMessageID: UUID(), accessToken: "token")
        await store.refreshLanguagePresentation()
        guard case .loaded(let conversation) = store.detailState else {
            return XCTFail("Expected refreshed conversation")
        }
        XCTAssertEqual(conversation.messages.map(\.body), ["Hello"])
    }

    func testMessagingStoreShowsOfflineStateForNetworkFailure() async {
        let store = MessagingStore(
            api: OfflineMessagingAPI(),
            accessTokenProvider: { "token" },
            diagnostics: LegendDiagnostics(),
            actorParticipantType: .client)

        store.load()
        let deadline = ContinuousClock.now.advanced(by: .seconds(1))
        while ContinuousClock.now < deadline {
            if case .offline = store.state {
                return
            }
            try? await Task.sleep(for: .milliseconds(10))
        }

        XCTFail("Expected an offline messaging state")
    }

    func testMessagingStoreShowsUnauthorizedStateForTheBearerApiContract() async {
        let store = MessagingStore(
            api: UnauthorizedMessagingAPI(),
            accessTokenProvider: { "token" },
            diagnostics: LegendDiagnostics(),
            actorParticipantType: .client)

        store.load()
        try? await Task.sleep(for: .milliseconds(50))

        guard case .unauthorized(let failure) = store.state else {
            return XCTFail("Expected an unauthorized messaging state")
        }
        XCTAssertEqual(failure.message, "Your session has ended. Please sign in again.")
    }

    func testAgentClientMessageActionUsesTheExactAuthorizedClientProfile() async {
        let clientProfileID = UUID(uuidString: "00000000-0000-0000-0000-000000000222")!
        let api = TypedClientRecipientMessagingAPI(clientProfileID: clientProfileID)
        let store = MessagingStore(
            api: api,
            accessTokenProvider: { "token" },
            diagnostics: LegendDiagnostics(),
            actorParticipantType: .agent)
        let started = expectation(description: "Starts the exact client conversation")

        store.startConversation(forClientProfileID: clientProfileID) { _ in
            started.fulfill()
        }

        await fulfillment(of: [started], timeout: 1)
        XCTAssertEqual(api.startedRecipient?.identity.participantType, .client)
        XCTAssertEqual(UUID(uuidString: api.startedRecipient?.profileID ?? ""), clientProfileID)
    }

    func testFinancialStoreUsesDedicatedFinancialProjection() async throws {
        let snapshot = MobileFinancialSnapshotResponse(
            position: MobileFinancialPosition(
                healthScore: 72,
                assetsTotal: 125_000,
                liabilitiesTotal: 45_000,
                netWorth: 80_000,
                annualEarnings: 100_000,
                annualLifestyleRemaining: 55_000,
                annualTaxes: 12_000,
                protectionGapTotal: 0,
                positionStatus: "Stable",
                positionSummary: "Your saved financial position is available.",
                estatePlanningStatus: "In progress",
                estatePlanningRiskLevel: "Moderate",
                updatedUTC: .now
            ),
            intelligence: nil,
            upcomingBills: [],
            operatingSystem: availableOperatingSystem
        )
        let store = MobileFinancialStore(
            api: StubMobileFinancialAPI(snapshot: snapshot),
            accessTokenProvider: { "token" },
            diagnostics: LegendDiagnostics()
        )

        store.load()
        try await Task.sleep(for: .milliseconds(50))

        guard case .available(let loaded) = store.state else {
            return XCTFail("Expected the dedicated financial projection")
        }

        XCTAssertEqual(loaded.position?.netWorth, 80_000)
        XCTAssertEqual(loaded.operatingSystem?.projection.status, "Available")
    }

    func testFinancialStoreDistinguishesNeverSavedExpenseLensState() async throws {
        let neverSaved = MobileFinancialSnapshotResponse(
            position: nil,
            intelligence: nil,
            upcomingBills: [],
            operatingSystem: MobileFinancialOperatingSystemSnapshotResponse(
                projection: MobileFinancialProjectionStatusResponse(
                    status: "Unavailable",
                    reasonCode: "EXPENSE_LENS_STATE_NOT_FOUND",
                    summary: "Save Expense Lens to begin."
                ),
                freshness: MobileFinancialDataFreshnessResponse(
                    financeStateUpdatedUTC: nil,
                    intelligenceEvaluatedUTC: nil,
                    generatedUTC: .now
                ),
                weekAtGlance: nil,
                monthAtGlance: nil,
                tools: []
            )
        )
        let store = MobileFinancialStore(
            api: StubMobileFinancialAPI(snapshot: neverSaved),
            accessTokenProvider: { "token" },
            diagnostics: LegendDiagnostics()
        )

        store.load()
        try await Task.sleep(for: .milliseconds(50))

        guard case .neverSaved(_, let detail) = store.state else {
            return XCTFail("Expected the unsaved Expense Lens state")
        }

        XCTAssertEqual(detail, "Save Expense Lens to begin.")
    }

    func testFinancialStoreKeepsSavedHealthSnapshotAvailableWhenExpenseLensIsNotSaved() async throws {
        let healthSnapshot = MobileFinancialHealthSnapshotResponse(
            updatedUTC: .now,
            sections: [
                MobileFinancialHealthSectionResponse(
                    key: "assets",
                    title: "Assets",
                    semantic: "assets",
                    period: nil,
                    groups: [
                        MobileFinancialHealthGroupResponse(
                            key: "asset-components",
                            title: nil,
                            metrics: [
                                MobileFinancialHealthMetricResponse(
                                    key: "savings",
                                    label: "Savings",
                                    valueType: "Currency",
                                    amountCents: 990_025,
                                    numericValue: nil,
                                    textValue: nil,
                                    status: nil)
                            ])
                    ],
                    total: MobileFinancialHealthMetricResponse(
                        key: "total-assets",
                        label: "Total Assets",
                        valueType: "Currency",
                        amountCents: 990_025,
                        numericValue: nil,
                        textValue: nil,
                        status: nil))
            ])
        let snapshot = MobileFinancialSnapshotResponse(
            position: nil,
            intelligence: nil,
            upcomingBills: [],
            operatingSystem: MobileFinancialOperatingSystemSnapshotResponse(
                projection: MobileFinancialProjectionStatusResponse(
                    status: "Unavailable",
                    reasonCode: "EXPENSE_LENS_STATE_NOT_FOUND",
                    summary: "Save Expense Lens to begin."),
                freshness: MobileFinancialDataFreshnessResponse(
                    financeStateUpdatedUTC: nil,
                    intelligenceEvaluatedUTC: nil,
                    generatedUTC: .now),
                weekAtGlance: nil,
                monthAtGlance: nil,
                tools: []),
            healthSnapshot: healthSnapshot)
        let store = MobileFinancialStore(
            api: StubMobileFinancialAPI(snapshot: snapshot),
            accessTokenProvider: { "token" },
            diagnostics: LegendDiagnostics())

        store.load()
        try await Task.sleep(for: .milliseconds(50))

        guard case .available(let loaded) = store.state else {
            return XCTFail("Expected the saved Financial Health Snapshot")
        }

        XCTAssertEqual(loaded.healthSnapshot?.sections.first?.total?.amountCents, 990_025)
        XCTAssertEqual(
            loaded.operatingSystem?.projection.reasonCode,
            "EXPENSE_LENS_STATE_NOT_FOUND")
    }

    func testDailyScriptureDecodesServerOwnedOverrideContentWithoutRewritingIt() throws {
        let data = Data("""
        {
          "date": "2026-08-07",
          "reference": "Psalm 121",
          "translation": "KJV",
          "verses": [],
          "text": "1 I will lift up mine eyes unto the hills.\\n2 My help cometh from the LORD.",
          "source": "ScheduledOverride",
          "passageText": "1 I will lift up mine eyes unto the hills.\\n2 My help cometh from the LORD."
        }
        """.utf8)

        let scripture = try JSONDecoder.mobile.decode(MobileDailyScripture.self, from: data)

        XCTAssertEqual(scripture.source, "ScheduledOverride")
        XCTAssertEqual(scripture.passageText, scripture.text)
        XCTAssertTrue(scripture.verses.isEmpty)

        let paragraphs = DailyScripturePassageFormatter.paragraphs(for: scripture)
        XCTAssertEqual(paragraphs.count, 1)
        XCTAssertEqual(paragraphs[0].segments, [
            DailyScripturePassageSegment(number: 1, text: "I will lift up mine eyes unto the hills."),
            DailyScripturePassageSegment(number: 2, text: "My help cometh from the LORD.")
        ])
    }

    func testDailyScripturePassageFormatterKeepsUnstructuredRawPassageUntouched() {
        let raw = "Read slowly and reflect.\nThis paragraph has no structured verse markers."
        let scripture = MobileDailyScripture(
            date: "2026-08-07",
            reference: "Psalm 121",
            translation: "KJV",
            verses: [],
            text: raw,
            source: "ScheduledOverride",
            passageText: raw)

        let paragraphs = DailyScripturePassageFormatter.paragraphs(for: scripture)

        XCTAssertEqual(paragraphs, [
            DailyScripturePassageParagraph(
                id: "raw-0",
                segments: [DailyScripturePassageSegment(number: nil, text: raw)])
        ])
    }

    func testServerGrantedScriptureManagementCapabilityIsAvailableToTheNativeSettingsSurface() throws {
        let data = Data(#"{"messaging":true,"isFounder":false,"canManageScripture":true}"#.utf8)

        let capabilities = try JSONDecoder.mobile.decode(MobileCapabilities.self, from: data)

        XCTAssertTrue(capabilities.messaging)
        XCTAssertFalse(capabilities.isFounder)
        XCTAssertTrue(capabilities.sessionCapabilities.contains("scripture-management"))
        XCTAssertFalse(capabilities.sessionCapabilities.contains("founder"))
    }

    private var availableOperatingSystem: MobileFinancialOperatingSystemSnapshotResponse {
        MobileFinancialOperatingSystemSnapshotResponse(
            projection: MobileFinancialProjectionStatusResponse(
                status: "Available",
                reasonCode: nil,
                summary: "Your saved weekly plan is available."
            ),
            freshness: MobileFinancialDataFreshnessResponse(
                financeStateUpdatedUTC: .now,
                intelligenceEvaluatedUTC: nil,
                generatedUTC: .now
            ),
            weekAtGlance: nil,
            monthAtGlance: nil,
            tools: []
        )
    }

    func testAddingAccountKeepsPriorCredentialThroughRoleSelectionAndSwitchBack() async throws {
        let store = AccountTestTokenStore()
        defer { try? store.removeAccount(id: "member-a"); try? store.removeAccount(id: "member-b") }
        let initialDate = Date().addingTimeInterval(-30 * 86400)
        let original = OAuthTokenSet(accessToken: "account-a", refreshToken: "refresh-a", expiresAt: .distantFuture, interactiveSignInAt: initialDate)
        _ = try store.upsert(original, for: MobileSignedInAccount(id: "member-a", displayName: "A", participantType: .client))
        let coordinator = MobileSessionCoordinator(configuration: completeConfiguration(), tokenStore: store,
            authorizer: AccountAddingAuthorizer(), tokenExchanger: AccountAddingExchanger(),
            sessionService: AccountAddingService(), launchCache: LegendEphemeralLaunchCache(),
            biometricSecurity: AcceptingBiometricSecurity())
        coordinator.restore()
        try await waitForState(coordinator) { if case .authenticated = $0 { return true }; return false }
        coordinator.addAccount()
        try await waitForState(coordinator) { if case .roleSelection = $0 { return true }; return false }
        XCTAssertEqual(try store.read(), original, "Pending second identity must not overwrite the selected credential")
        coordinator.selectRole(.client)
        try await waitForState(coordinator) { if case .authenticated(let session) = $0 { return session.actor.identity.userID == "member-b" }; return false }
        XCTAssertEqual(Set(try store.signedInAccounts().map(\.id)), ["member-a", "member-b"])
        coordinator.switchToSignedInAccount("member-a")
        try await waitForState(coordinator) { if case .authenticated(let session) = $0 { return session.actor.identity.userID == "member-a" }; return false }
        XCTAssertEqual(try store.read()?.accessToken, "account-a")
        XCTAssertEqual(try store.read()?.interactiveSignInAt, initialDate)
        coordinator.addAccount()
        try await waitForState(coordinator) { if case .roleSelection = $0 { return true }; return false }
        coordinator.signOut()
        try await waitForState(coordinator) { if case .authenticated(let session) = $0 { return session.actor.identity.userID == "member-a" }; return false }
        XCTAssertEqual(try store.read()?.accessToken, "account-a", "Cancelling pending role selection must not sign out the original account")
        XCTAssertEqual(try store.signedInAccounts().count, 2)
    }

    func testExpiredAccountsRemainListedAndRefreshDoesNotRestartRetention() throws {
        let store = AccountTestTokenStore()
        defer { try? store.removeAccount(id: "expired"); try? store.removeAccount(id: "current") }
        let expired = OAuthTokenSet(accessToken: "old", refreshToken: "old-refresh", expiresAt: .distantFuture,
            interactiveSignInAt: Date().addingTimeInterval(-91 * 86400))
        let refreshed = expired.refreshed(accessToken: "renewed", refreshToken: "renewed-refresh", expiresAt: .distantFuture)
        XCTAssertTrue(refreshed.requiresInteractiveSignIn)
        XCTAssertEqual(refreshed.interactiveSignInAt, expired.interactiveSignInAt)
        _ = try store.upsert(refreshed, for: MobileSignedInAccount(id: "expired", displayName: "Expired", participantType: .client))
        _ = try store.upsert(OAuthTokenSet(accessToken: "current", refreshToken: nil, expiresAt: .distantFuture),
            for: MobileSignedInAccount(id: "current", displayName: "Current", participantType: .agent))
        let accounts = try store.signedInAccounts()
        XCTAssertEqual(accounts.count, 2)
        XCTAssertEqual(accounts.first { $0.id == "expired" }?.requiresSignIn, true)
        XCTAssertEqual(accounts.first { $0.id == "current" }?.requiresSignIn, false)
        let legacyAccount = Data(#"{"id":"legacy","displayName":"Legacy","participantType":"Client","lastUsedAt":0}"#.utf8)
        XCTAssertNoThrow(try JSONDecoder().decode(MobileSignedInAccount.self, from: legacyAccount))
    }

    private func completeConfiguration() -> MobileConfiguration {
        MobileConfiguration(
            bundleIdentifier: "com.mylegnd.legend.registered",
            apiBaseURL: URL(string: "https://api.example.test")!,
            authorizationEndpoint: URL(string: "https://identity.example.test/authorize")!,
            tokenEndpoint: URL(string: "https://identity.example.test/token")!,
            clientID: "public-client",
            redirectScheme: "com-mylegnd-legend-registered",
            scope: "openid profile api://legend/mobile_access",
            audience: "api://legend")
    }

    private func waitForState(
        _ coordinator: MobileSessionCoordinator,
        matching predicate: (MobileSessionState) -> Bool
    ) async throws {
        let deadline = ContinuousClock.now.advanced(by: .seconds(1))
        while !predicate(coordinator.state),
              ContinuousClock.now < deadline {
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertTrue(predicate(coordinator.state), "The expected session state was not reached.")
    }

    private func waitForAuthenticatedSession(
        _ coordinator: MobileSessionCoordinator,
        participantType: ParticipantType
    ) async throws -> MobileSession {
        let deadline = ContinuousClock.now.advanced(by: .seconds(1))
        while ContinuousClock.now < deadline {
            if case .authenticated(let session) = coordinator.state,
               session.actor.identity.participantType == participantType {
                return session
            }
            try await Task.sleep(for: .milliseconds(10))
        }

        XCTFail("The expected authenticated role was not reached.")
        throw SessionSwitchTestError.expectedSessionWasNotReached
    }

    func testFounderChatConsumesProgressAndResultOnOneAuthenticatedPost() async throws {
        let store = await availableFounderStore()
        defer { resetFounderStreamStub() }
        let wire = """
        {"type":"accepted","operationId":"test-operation","responseAuthority":"LegendAi"}
        {"type":"progress","progress":{"stage":"tool","message":"Reading governed evidence"}}
        {"type":"heartbeat","elapsedSeconds":4}
        {"type":"result","status":200,"result":{"succeeded":true,"mode":"legend","message":"Computed résultat.","responseAuthority":"LegendAi"}}

        """
        let bytes = Array(wire.utf8)
        let split = try XCTUnwrap(bytes.firstIndex(of: 0xC3)) + 1
        StubURLProtocol.responseChunks = [Data(bytes[..<split]), Data(bytes[split...])]
        var updates: [String] = []
        let observation = store.$progressMessage.compactMap { $0 }.sink { updates.append($0) }
        defer { observation.cancel() }
        await store.send("Use the governed record.", nativeOnly: true)
        XCTAssertEqual(store.messages.last?.content, "Computed résultat.")
        XCTAssertNil(store.failureMessage)
        XCTAssertFalse(store.isSending)
        XCTAssertTrue(updates.contains("Reading governed evidence"))
        XCTAssertTrue(updates.contains("Reading governed evidence · 4s"))
        XCTAssertEqual(StubURLProtocol.requests.count, 1)
        let request = try XCTUnwrap(StubURLProtocol.requests.first)
        XCTAssertEqual(request.httpMethod, "POST")
        XCTAssertEqual(request.url?.path, "/api/v1/mobile/founder/legend-ai/chat")
        XCTAssertEqual(request.value(forHTTPHeaderField: "Accept"), "application/x-ndjson")
        XCTAssertEqual(request.value(forHTTPHeaderField: "Content-Type"), "application/json")
        XCTAssertEqual(request.value(forHTTPHeaderField: "Authorization"), "Bearer test-token")
        XCTAssertEqual(request.value(forHTTPHeaderField: "X-Legend-Participant-Type"), "Agent")
        XCTAssertNotNil(UUID(uuidString: request.value(forHTTPHeaderField: "X-Legend-Ai-Operation-Id") ?? ""))
    }

    func testFounderStreamPreservesStructuredFailureDespiteSuccessfulTransport() async {
        let store = await availableFounderStore()
        defer { resetFounderStreamStub() }
        StubURLProtocol.responseBody = Data("""
        {"type":"result","status":503,"result":{"succeeded":false,"mode":"legend","error":"Evidence is unavailable.","failureKind":"governed_unavailable","reason":"source_withdrawn","reference":"safe-reference"}}

        """.utf8)
        await store.send("Read the governed record.")
        XCTAssertEqual(store.messages.count, 1)
        XCTAssertTrue(store.failureMessage?.contains("Evidence is unavailable.") == true)
        XCTAssertTrue(store.failureMessage?.contains("governed_unavailable") == true)
        XCTAssertTrue(store.failureMessage?.contains("source_withdrawn") == true)
        XCTAssertTrue(store.failureMessage?.contains("safe-reference") == true)
    }

    func testFounderStreamRequiresTerminalResultAndConsistentSuccessStatus() async {
        for wire in [
            "{\"type\":\"heartbeat\",\"elapsedSeconds\":4}\n",
            "{\"type\":\"result\",\"result\":{\"succeeded\":true,\"mode\":\"legend\",\"message\":\"Unsupported success\"}}\n",
            "{\"type\":\"result\",\"status\":503,\"result\":{\"succeeded\":true,\"mode\":\"legend\",\"message\":\"Unsupported success\"}}\n",
            "{\"type\":\"result\",\"status\":200,\"result\":{\"succeeded\":false,\"mode\":\"legend\",\"error\":\"Refused\"}}\n"
        ] {
            let store = await availableFounderStore()
            StubURLProtocol.responseBody = Data(wire.utf8)
            await store.send("Read the governed record.")
            XCTAssertEqual(store.messages.count, 1)
            XCTAssertNotNil(store.failureMessage)
            XCTAssertFalse(store.isSending)
            resetFounderStreamStub()
        }
    }

    func testFounderStopCancelsTheOriginalStreamingPost() async {
        let store = await availableFounderStore()
        defer { resetFounderStreamStub() }
        let started = expectation(description: "Chat POST started")
        let stopped = expectation(description: "Original POST cancelled")
        StubURLProtocol.onRequest = { started.fulfill() }
        StubURLProtocol.onStop = { stopped.fulfill() }
        StubURLProtocol.holdOpen = true
        StubURLProtocol.responseBody = Data("{\"type\":\"heartbeat\",\"elapsedSeconds\":4}\n".utf8)
        let sending = Task { await store.send("Read the governed record.") }
        await fulfillment(of: [started], timeout: 2)
        sending.cancel()
        await sending.value
        await fulfillment(of: [stopped], timeout: 2)
        XCTAssertFalse(store.isSending)
        XCTAssertEqual(store.messages.count, 1)
        XCTAssertEqual(store.failureMessage, "Response stopped. Your next message is ready to send.")
        XCTAssertEqual(StubURLProtocol.requests.count, 1)
    }

    private func availableFounderStore() async -> LegendFounderAiStore {
        resetFounderStreamStub()
        StubURLProtocol.responseStatus = 200
        StubURLProtocol.responseBody = Data("{\"available\":true}".utf8)
        let store = LegendFounderAiStore(
            client: MobileHTTPClient(baseURL: URL(string: "https://api.example.test")!, session: stubSession()),
            participantType: .agent, accessTokenProvider: { "test-token" })
        await store.resolveAvailability()
        XCTAssertTrue(store.isAvailable)
        StubURLProtocol.requests = []
        return store
    }

    private func resetFounderStreamStub() {
        StubURLProtocol.responseBody = nil
        StubURLProtocol.responseChunks = nil
        StubURLProtocol.requests = []
        StubURLProtocol.holdOpen = false
        StubURLProtocol.onRequest = nil
        StubURLProtocol.onStop = nil
    }

    private func stubSession() -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubURLProtocol.self]
        return URLSession(configuration: configuration)
    }
}

private struct StubMobileFinancialAPI: MobileFinancialAPI {
    let snapshot: MobileFinancialSnapshotResponse

    func financial(
        accessToken: String
    ) async throws -> MobileFinancialSnapshotResponse {
        snapshot
    }
}

private final class StubURLProtocol: URLProtocol {
    static var responseStatus = 200
    static var responseBody: Data?
    static var lastRequest: URLRequest?
    static var lastRequestTimeout: TimeInterval?
    static var lastRequestURL: URL?
    static var requests: [URLRequest] = []
    static var responseChunks: [Data]?
    static var holdOpen = false
    static var onRequest: (() -> Void)?
    static var onStop: (() -> Void)?

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        Self.lastRequest = request
        Self.requests.append(request)
        Self.onRequest?()
        Self.lastRequestTimeout = request.timeoutInterval
        Self.lastRequestURL = request.url
        let response = HTTPURLResponse(
            url: request.url!,
            statusCode: Self.responseStatus,
            httpVersion: nil,
            headerFields: ["X-Correlation-ID": "test-correlation"])!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        let body: Data
        switch Self.responseStatus {
        case 401:
            body = Data(#"{"code":"mobile_authentication_required","correlationId":"test-correlation"}"#.utf8)
        case 403:
            body = Data(#"{"code":"mobile_access_forbidden","correlationId":"test-correlation"}"#.utf8)
        default:
            body = Self.responseBody ?? Data()
        }
        for chunk in Self.responseChunks ?? [body] {
            client?.urlProtocol(self, didLoad: chunk)
        }
        if !Self.holdOpen { client?.urlProtocolDidFinishLoading(self) }
    }

    override func stopLoading() { Self.onStop?() }
}

private final class InMemoryTokenStore: SecureTokenStoring, @unchecked Sendable {
    var storedTokens: OAuthTokenSet?
    var didClear = false

    init(storedTokens: OAuthTokenSet? = nil) {
        self.storedTokens = storedTokens
    }

    func read() throws -> OAuthTokenSet? { storedTokens }
    func save(_ tokens: OAuthTokenSet) throws { storedTokens = tokens }
    func clear() throws {
        storedTokens = nil
        didClear = true
    }
}

@MainActor
private final class AcceptingBiometricSecurity: MobileBiometricSessionSecuring {
    var isAvailable: Bool { true }

    func hasPrompted(for identity: LogicalParticipantIdentity) -> Bool { true }
    func markPrompted(for identity: LogicalParticipantIdentity) {}
    func isEnabled(for identity: LogicalParticipantIdentity) -> Bool { true }
    func disable(for identity: LogicalParticipantIdentity) {}
    func enable(for identity: LogicalParticipantIdentity) async -> Bool { true }
    func authenticate() async -> Bool { true }
}

private enum SessionSwitchTestError: Error {
    case expectedSessionWasNotReached
}

private actor DualRoleSessionService: MobileSessionServicing {
    private let permittedParticipantTypes: [ParticipantType] = [.agent, .client]
    private let agent: MobileActor
    private let client: MobileActor
    private var roles: [ParticipantType] = []

    init() throws {
        let agentIdentity = try LogicalParticipantIdentity(
            userID: "shared-entra-oid",
            participantType: .agent)
        let clientIdentity = try LogicalParticipantIdentity(
            userID: "shared-entra-oid",
            participantType: .client)
        agent = try MobileActor(
            identity: agentIdentity,
            profileID: "00000000-0000-0000-0000-000000000001",
            displayName: "Agent Account",
            avatar: nil)
        client = try MobileActor(
            identity: clientIdentity,
            profileID: "00000000-0000-0000-0000-000000000002",
            displayName: "Client Account",
            avatar: nil)
    }

    func bootstrap(accessToken: String) async throws -> MobileBootstrapResponse {
        MobileBootstrapResponse(
            authenticated: true,
            actor: nil,
            permittedParticipantTypes: permittedParticipantTypes,
            requiresParticipantSelection: true,
            capabilities: MobileCapabilities(messaging: true),
            correlationID: "dual-role-bootstrap")
    }

    func selectRole(
        _ participantType: ParticipantType,
        accessToken: String
    ) async throws -> MobileRoleSelectionResponse {
        roles.append(participantType)
        let selectedActor = switch participantType {
        case .agent: agent
        case .client: client
        }
        return MobileRoleSelectionResponse(
            actor: selectedActor,
            permittedParticipantTypes: permittedParticipantTypes,
            correlationID: "dual-role-selection")
    }

    func requestedRoles() -> [ParticipantType] {
        roles
    }
}

private final class TestAuthorizer: OAuthAuthorizing {
    func authorize(_ request: OAuthAuthorizationRequest) async throws -> URL {
        URL(string: "com-mylegnd-legend-registered://oauth/callback?code=test&state=test")!
    }
}

private struct TestTokenExchanger: OAuthTokenExchanging {
    func exchange(code: String, pkceVerifier: String, configuration: MobileConfiguration) async throws -> OAuthTokenSet {
        OAuthTokenSet(accessToken: "token", refreshToken: nil, expiresAt: .distantFuture)
    }

    func refresh(refreshToken: String, configuration: MobileConfiguration) async throws -> OAuthTokenSet {
        OAuthTokenSet(accessToken: "token", refreshToken: refreshToken, expiresAt: .distantFuture)
    }
}

private struct StubMessagingAPI: MessagingAPI {
    func conversations(accessToken: String) async throws -> [ConversationSummary] { [] }
    func recipients(search: String?, scope: MessagingRecipientScope?, accessToken: String) async throws -> [MessagingRecipient] { [] }
    func start(recipient: MessagingRecipient, accessToken: String) async throws -> ConversationDetail { throw MobileMessagingContractError.unavailable }
    func conversation(id: UUID, accessToken: String) async throws -> ConversationDetail { throw MobileMessagingContractError.unavailable }
    func messages(conversationID: UUID, accessToken: String) async throws -> [ConversationMessage] { throw MobileMessagingContractError.unavailable }
    func send(conversationID: UUID, body: String, replyToMessageID: UUID?, clientMessageID: UUID, accessToken: String) async throws -> ConversationMessage { throw MobileMessagingContractError.unavailable }
    func upload(conversationID: UUID, messageID: UUID, attachment: MessagingAttachmentDraft, accessToken: String) async throws -> MessagingAttachment { throw MobileMessagingContractError.unavailable }
    func markRead(conversationID: UUID, accessToken: String) async throws {}
}

private final class InboxReconciliationMessagingAPI: MessagingAPI, @unchecked Sendable {
    let conversationID: UUID
    let recipient: MessagingRecipient
    private(set) var conversationListCallCount = 0
    private(set) var recipientCallCount = 0
    private var isVisibleInInbox = false
    private var lastMessage: ConversationMessage?
    var failNextSendAfterPersistence = false
    private(set) var sentClientMessageIDs: [UUID] = []
    private(set) var persistedMessages: [UUID: ConversationMessage] = [:]
    var holdNextInboxSnapshot = false
    var releaseInboxSnapshot: CheckedContinuation<Void, Never>?

    init(conversationID: UUID) {
        self.conversationID = conversationID
        self.recipient = MessagingRecipient(
            identity: try! LogicalParticipantIdentity(
                userID: "agent-1",
                participantType: .agent),
            profileID: UUID().uuidString,
            displayName: "Agent One",
            email: "agent.one@example.test",
            roleLabel: nil,
            relationshipLabel: "Agent",
            existingConversationID: nil,
            avatar: nil)
    }

    func makeConversationVisible() {
        isVisibleInInbox = true
    }

    func conversations(accessToken: String) async throws -> [ConversationSummary] {
        conversationListCallCount += 1
        if holdNextInboxSnapshot {
            holdNextInboxSnapshot = false
            await withCheckedContinuation { releaseInboxSnapshot = $0 }
            return []
        }
        guard isVisibleInInbox else { return [] }
        return [ConversationSummary(
            id: conversationID,
            conversationType: "ClientAgent",
            counterparty: MessagingParticipant(
                identity: recipient.identity,
                profileID: recipient.profileID,
                displayName: recipient.displayName,
                roleLabel: recipient.roleLabel,
                avatar: recipient.avatar,
                isVerified: false),
            title: recipient.displayName,
            lastMessagePreview: lastMessage?.body ?? "Incoming message",
            lastMessageUTC: lastMessage?.sentUTC ?? .now,
            unreadCount: 0,
            isClosed: false,
            purpose: nil,
            groupAvatar: nil)]
    }

    func recipients(search: String?, scope: MessagingRecipientScope?, accessToken: String) async throws -> [MessagingRecipient] {
        recipientCallCount += 1
        return [recipient]
    }

    func start(recipient: MessagingRecipient, accessToken: String) async throws -> ConversationDetail {
        ConversationDetail(
            id: conversationID,
            conversationType: "ClientAgent",
            title: recipient.displayName,
            participants: [],
            messages: [],
            isMuted: false,
            isClosed: false,
            canManageMembers: false)
    }

    func conversation(id: UUID, accessToken: String) async throws -> ConversationDetail {
        ConversationDetail(
            id: conversationID,
            conversationType: "ClientAgent",
            title: recipient.displayName,
            participants: [],
            messages: lastMessage.map { [$0] } ?? [],
            isMuted: false,
            isClosed: false,
            canManageMembers: false)
    }

    func messages(conversationID: UUID, accessToken: String) async throws -> [ConversationMessage] {
        lastMessage.map { [$0] } ?? []
    }

    func send(conversationID: UUID, body: String, replyToMessageID: UUID?, clientMessageID: UUID, accessToken: String) async throws -> ConversationMessage {
        sentClientMessageIDs.append(clientMessageID)
        if let existing = persistedMessages[clientMessageID] { return existing }
        let message = ConversationMessage(
            id: UUID(),
            conversationID: conversationID,
            sender: MessagingParticipant(
                identity: try! LogicalParticipantIdentity(
                    userID: "client-1",
                    participantType: .client),
                profileID: UUID().uuidString,
                displayName: "Client One",
                roleLabel: nil,
                avatar: nil,
                isVerified: false),
            body: body,
            sentUTC: .now,
            attachments: [],
            isMine: true,
            reply: nil)
        lastMessage = message
        isVisibleInInbox = true
        persistedMessages[clientMessageID] = message
        if failNextSendAfterPersistence {
            failNextSendAfterPersistence = false
            throw URLError(.timedOut)
        }
        return message
    }

    func upload(conversationID: UUID, messageID: UUID, attachment: MessagingAttachmentDraft, accessToken: String) async throws -> MessagingAttachment {
        throw MobileMessagingContractError.unavailable
    }

    func markRead(conversationID: UUID, accessToken: String) async throws {}
}

@MainActor
private final class RecordingMessagingRealtimeTransport: MessagingRealtimeTransport {
    var onEvent: ((MobileMessagingRealtimeEvent) -> Void)?
    private(set) var startCount = 0

    func start() {
        startCount += 1
    }

    func stop() {}

    func publish(_ event: MobileMessagingRealtimeEvent) {
        onEvent?(event)
    }
}

private struct OfflineMessagingAPI: MessagingAPI {
    func conversations(accessToken: String) async throws -> [ConversationSummary] {
        throw MobileAPIError.networkUnavailable
    }

    func recipients(search: String?, scope: MessagingRecipientScope?, accessToken: String) async throws -> [MessagingRecipient] {
        throw MobileAPIError.networkUnavailable
    }

    func start(recipient: MessagingRecipient, accessToken: String) async throws -> ConversationDetail {
        throw MobileAPIError.networkUnavailable
    }

    func conversation(id: UUID, accessToken: String) async throws -> ConversationDetail {
        throw MobileAPIError.networkUnavailable
    }

    func messages(conversationID: UUID, accessToken: String) async throws -> [ConversationMessage] {
        throw MobileAPIError.networkUnavailable
    }

    func send(conversationID: UUID, body: String, replyToMessageID: UUID?, clientMessageID: UUID, accessToken: String) async throws -> ConversationMessage {
        throw MobileAPIError.networkUnavailable
    }

    func upload(conversationID: UUID, messageID: UUID, attachment: MessagingAttachmentDraft, accessToken: String) async throws -> MessagingAttachment {
        throw MobileAPIError.networkUnavailable
    }

    func markRead(conversationID: UUID, accessToken: String) async throws {
        throw MobileAPIError.networkUnavailable
    }
}

private struct UnauthorizedMessagingAPI: MessagingAPI {
    func conversations(accessToken: String) async throws -> [ConversationSummary] {
        throw MobileAPIError.apiUnauthorized(code: "mobile_authentication_required", correlationID: "test-correlation")
    }

    func recipients(search: String?, scope: MessagingRecipientScope?, accessToken: String) async throws -> [MessagingRecipient] {
        throw MobileAPIError.apiUnauthorized(code: "mobile_authentication_required", correlationID: "test-correlation")
    }

    func start(recipient: MessagingRecipient, accessToken: String) async throws -> ConversationDetail {
        throw MobileAPIError.apiUnauthorized(code: "mobile_authentication_required", correlationID: "test-correlation")
    }

    func conversation(id: UUID, accessToken: String) async throws -> ConversationDetail {
        throw MobileAPIError.apiUnauthorized(code: "mobile_authentication_required", correlationID: "test-correlation")
    }

    func messages(conversationID: UUID, accessToken: String) async throws -> [ConversationMessage] {
        throw MobileAPIError.apiUnauthorized(code: "mobile_authentication_required", correlationID: "test-correlation")
    }

    func send(conversationID: UUID, body: String, replyToMessageID: UUID?, clientMessageID: UUID, accessToken: String) async throws -> ConversationMessage {
        throw MobileAPIError.apiUnauthorized(code: "mobile_authentication_required", correlationID: "test-correlation")
    }

    func upload(conversationID: UUID, messageID: UUID, attachment: MessagingAttachmentDraft, accessToken: String) async throws -> MessagingAttachment {
        throw MobileAPIError.apiUnauthorized(code: "mobile_authentication_required", correlationID: "test-correlation")
    }

    func markRead(conversationID: UUID, accessToken: String) async throws {
        throw MobileAPIError.apiUnauthorized(code: "mobile_authentication_required", correlationID: "test-correlation")
    }
}

private final class TypedClientRecipientMessagingAPI: MessagingAPI, @unchecked Sendable {
    let clientProfileID: UUID
    private(set) var startedRecipient: MessagingRecipient?

    init(clientProfileID: UUID) {
        self.clientProfileID = clientProfileID
    }

    func conversations(accessToken: String) async throws -> [ConversationSummary] { [] }

    func recipients(search: String?, scope: MessagingRecipientScope?, accessToken: String) async throws -> [MessagingRecipient] {
        [
            MessagingRecipient(
                identity: try LogicalParticipantIdentity(userID: "same-person", participantType: .agent),
                profileID: "00000000-0000-0000-0000-000000000111",
                displayName: "Agent identity",
                email: "agent@example.test",
                roleLabel: nil,
                relationshipLabel: "Company agent",
                existingConversationID: nil,
                avatar: nil),
            MessagingRecipient(
                identity: try LogicalParticipantIdentity(userID: "same-person", participantType: .client),
                profileID: clientProfileID.uuidString,
                displayName: "Client identity",
                email: "client@example.test",
                roleLabel: nil,
                relationshipLabel: "Active client",
                existingConversationID: nil,
                avatar: nil)
        ]
    }

    func start(recipient: MessagingRecipient, accessToken: String) async throws -> ConversationDetail {
        startedRecipient = recipient
        return ConversationDetail(
            id: UUID(),
            conversationType: "ClientAgent",
            title: recipient.displayName,
            participants: [],
            messages: [],
            isMuted: false,
            isClosed: false,
            canManageMembers: false)
    }

    func createGroup(subject: String, recipients: [MessagingRecipient], groupImage: MessagingGroupImageRequest?, accessToken: String) async throws -> ConversationDetail {
        throw MobileMessagingContractError.unavailable
    }

    func startVerificationRequest(accessToken: String) async throws -> VerificationRequestSubmission {
        throw MobileMessagingContractError.unavailable
    }

    func updateGroup(conversationID: UUID, subject: String, groupImage: MessagingGroupImageRequest?, accessToken: String) async throws {
        throw MobileMessagingContractError.unavailable
    }

    func addGroupParticipant(conversationID: UUID, recipient: MessagingRecipient, accessToken: String) async throws {
        throw MobileMessagingContractError.unavailable
    }

    func resolveVerificationRequest(requestID: UUID, approve: Bool, accessToken: String) async throws {
        throw MobileMessagingContractError.unavailable
    }

    func conversation(id: UUID, accessToken: String) async throws -> ConversationDetail { throw MobileMessagingContractError.unavailable }
    func messages(conversationID: UUID, accessToken: String) async throws -> [ConversationMessage] { [] }
    func send(conversationID: UUID, body: String, replyToMessageID: UUID?, clientMessageID: UUID, accessToken: String) async throws -> ConversationMessage { throw MobileMessagingContractError.unavailable }
    func upload(conversationID: UUID, messageID: UUID, attachment: MessagingAttachmentDraft, accessToken: String) async throws -> MessagingAttachment { throw MobileMessagingContractError.unavailable }
    func markRead(conversationID: UUID, accessToken: String) async throws {}
}

private final class AccountAddingAuthorizer: OAuthAuthorizing {
    func authorize(_ request: OAuthAuthorizationRequest) async throws -> URL {
        URL(string: "\(request.redirectScheme)://oauth/callback?code=second&state=\(request.state)")!
    }
}

private struct AccountAddingExchanger: OAuthTokenExchanging {
    func exchange(code: String, pkceVerifier: String, configuration: MobileConfiguration) async throws -> OAuthTokenSet {
        OAuthTokenSet(accessToken: "account-b", refreshToken: "refresh-b", expiresAt: .distantFuture)
    }
    func refresh(refreshToken: String, configuration: MobileConfiguration) async throws -> OAuthTokenSet {
        throw MobileAPIError.networkUnavailable
    }
}

private struct AccountAddingService: MobileSessionServicing {
    private func actor(_ userID: String) throws -> MobileActor {
        try MobileActor(identity: LogicalParticipantIdentity(userID: userID, participantType: .client),
            profileID: "00000000-0000-0000-0000-000000000001", displayName: userID, avatar: nil)
    }
    func bootstrap(accessToken: String) async throws -> MobileBootstrapResponse {
        MobileBootstrapResponse(authenticated: true, actor: accessToken == "account-a" ? try actor("member-a") : nil,
            permittedParticipantTypes: [.client], requiresParticipantSelection: accessToken != "account-a",
            capabilities: MobileCapabilities(messaging: true), correlationID: "account-test")
    }
    func selectRole(_ participantType: ParticipantType, accessToken: String) async throws -> MobileRoleSelectionResponse {
        guard accessToken == "account-b" else { throw MobileAPIError.forbidden(correlationID: "account-test") }
        return MobileRoleSelectionResponse(actor: try actor("member-b"), permittedParticipantTypes: [.client], correlationID: "account-test")
    }
}

private final class AccountTestTokenStore: MultiAccountSecureTokenStoring, @unchecked Sendable {
    private var entries: [String: (MobileSignedInAccount, OAuthTokenSet)] = [:]
    private var selected: String?
    func read() throws -> OAuthTokenSet? { selected.flatMap { entries[$0]?.1 } }
    func save(_ tokens: OAuthTokenSet) throws {
        if let selected, let account = entries[selected]?.0 { entries[selected] = (account, tokens) }
    }
    func clear() throws { if let selected { entries.removeValue(forKey: selected) }; selected = nil }
    func signedInAccounts() throws -> [MobileSignedInAccount] {
        entries.values.map { entry in
            var account = entry.0
            account.requiresSignIn = entry.1.requiresInteractiveSignIn
            return account
        }
    }
    func selectedAccountID() throws -> String? { selected }
    func selectAccount(id: String) throws -> OAuthTokenSet? { selected = id; return entries[id]?.1 }
    func upsert(_ tokens: OAuthTokenSet, for account: MobileSignedInAccount) throws -> MobileSignedInAccount {
        entries[account.id] = (account, tokens); selected = account.id; return account
    }
    func removeAccount(id: String) throws { entries.removeValue(forKey: id); if selected == id { selected = nil } }
}
