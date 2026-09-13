# Share content audit and structured repair plan

Read-only audit of /private/tmp/legend-social-creation-20260911, observed HEAD 59ebd76c8e19521f6530c01502c271718b0a6dd7. No code edits, builds, provider calls, writes to application databases, or deployments. Messaging repair remains undeployed pending user approval.

## Confirmed content loss

1. iOS discards the social identity/media at the share UI boundary. Legend-ios/Legend/Features/Social/LegendSocialHomeSection.swift:150–176 constructs internalMessageBody from heading and optional body only; externalShareBody is body or heading. At239 ShareLink receives that String, not a URL/file. At443–471 internal sharing calls existing messaging.startConversation then messaging.send(body: internalMessageBody). No post ID, asset ID, playable URL, preview, or attachment reaches the send contract. A captionless Hac/story/photo necessarily becomes only “Shared a Legend … by …”.
2. Android has the equivalent explicit flattening: LegendApp.kt:4001–4016 legendInternalShareBody/legendExternalShareBody; internal send at3935 passes only body. External ACTION_SEND at3886 is text/plain with EXTRA_TEXT, no EXTRA_STREAM or content URL. One global share UI is already reused across post/Hac/story surfaces; it should consume an improved shared contract rather than acquire a second sharing engine.
3. The server share endpoint does not deliver content. MobileSocialController.cs:504 maps POST posts/{id}/share to SocialFeedService.RecordShareAsync:1485. That validates the sharing actor can see the source, then inserts one SocialPostShare per actor/post for analytics. Domain/Entities/SocialPostShare.cs contains no destination, message ID, or payload. This is intentionally a share-intent metric, not a message-attachment authority. Its success cannot establish recipient content delivery or actual external share completion.
4. MessagingMessageSummary (Domain/Messaging/MessagingModels.cs:590–604) carries body, file attachments, reply and translation metadata, but no typed social-post reference. Existing clients cannot recover missing post identity from a human-readable heading. Parsing those headings would create an unreliable parallel protocol.
5. Ordinary file attachment viewing is also incomplete on mobile. iOS MessagingHomeView.swift:3732 renders LegendMessageAttachmentChip; its body at3932 is a static icon/name/scan-status HStack, no open/download action. Android LegendApp.kt:3775 similarly renders filename + scanStatus as Text. MobileMessagingController.cs exposes attachment upload at685 but no attachment GET was found among its routes. Web already renders clean attachment download anchors in SHARED/wwwroot/js/messaging.js:939–955, backed by MessagingControllerBase.cs:312 and existing MessagingService.GetAttachmentForDownloadAsync:2469. Thus upload metadata can exist without a native viewable attachment.

## Existing authorities and restrictions to retain

- SocialFeedService.GetVisiblePostAsync:2155 requires nondeleted, published, unexpired content, ready assets, active author, then existing block/private-profile/follow visibility. GetMediaStreamAsync:958 resolves the asset back to that visible source before storage access. Do not substitute messaging membership for source visibility or silently grant a recipient permission merely because the sender could see a post.
- MobileSocialController.cs:15 requires mobile authorization. Its media/{assetId} GET at354 uses resolved social actor and GetMediaAsync, returns a range-capable stream. Those protected URLs are not anonymous external sharing links and must never be accompanied by bearer tokens in shared text.
- Stories use the same SocialPost projection and expiration checks. Hac readiness/publication state belongs to existing social media processing/storage. Sharing must not claim playable content while a draft is processing.
- Existing messaging attachment download authority validates access and clean scan status; preserve it rather than inventing a mobile storage bypass. Web has a functional authorized download route, while native attachment consumers need parity.
- No social post deep-link handler was identified in inspected clients. Android's observed VIEW intent filter at AndroidManifest.xml:62 is the MSAL authentication callback, not a social link. The mobile social API has no inspected GET posts/{id} view route, only mutations/insights. A real navigable link needs explicit resolution; placing an arbitrary URL in a share sheet is insufficient.
- An equivalent web social sharing producer was not identified in the inspected controller/shared-JS paths. Web messaging is a confirmed consumer and must render any new typed reference. This is a bounded repository observation, not proof no other website has a social entry point.

## Structured plan (implementation follows the social-creation repair)

### 1. Establish one typed content reference in existing messaging contracts

Extend existing send/start commands and message projection with a backward-compatible social reference (source post ID and server-projected kind/status), persisted with the existing message in its atomic save. Keep optional user commentary separate from source identity. Do not clone social body/media into another canonical post system. Route the existing iOS/Android global share controls through this contract. Use existing client message ID for delivery retry/idempotency; analytics remain independently truthful.

At send, validate sender source visibility through existing social authority plus destination messaging authorization. At recipient read/open, project source content through the same social authority for that recipient, returning an explicit unavailable/restricted/expired state when appropriate. This preserves revocation and story expiry. A share must not widen a private post's audience. Define whether an otherwise valid message can carry an unavailable card for a recipient versus refusing that destination; make the decision explicit in the existing operation contract, not in a client guess.

### 2. Reuse social presentation/media on every message consumer

Return a bounded server-owned social card from existing message projection: canonical source identifier, content kind, author presentation, caption, ordered media descriptors and authorized resolution actions. Clients reuse current photo/video/Hac/story renderers and protected media loader. Web gets the same content card and authorized resolver through its existing actor boundary. Never convert the structured reference into translation input; only human commentary/caption presentation follows the existing translation authority.

Opening a card resolves current authorization and publication state, rather than trusting stale cached URLs. Test captionless media and multi-image ordering. A content-unavailable card must not be reported as successful playback.

### 3. Make external shares actually usable without leaking private media

Choose a server-owned canonical HTTPS post URL resolved through the existing social visibility authority, with an authenticated web view and app-link/universal-link routing to that same post. Retain the destination through login. Anonymous previews must be separately allowed by actual existing publication/audience rules; current mobile visibility is not blanket permission for public export.

For an explicitly permitted file export, use current authorized media retrieval to obtain bytes and native share-sheet file semantics (iOS transferable file; Android content URI/temporary read permission), with no auth token or storage secret in the share. This may be useful where the user requests the actual file, but it requires a clear source-export policy; do not silently transform every private story into a permanent public file. Link behavior after deletion/expiry is explicit. External chooser launch/share intent is not proof of destination delivery; preserve that distinction in analytics and UI.

### 4. Complete native file attachment consumption

Expose a bearer-authenticated mobile download adapter to the existing GetAttachmentForDownloadAsync + storage authority, retaining typed actor, conversation access and clean-scan gate. Add authenticated image/document/video opening in the existing native message attachment component. Keep pending/rejected scan states non-openable with meaningful status. Reuse the existing file storage and scan lifecycle; do not route through public social-media storage or relax clean-only access.

### 5. Verification gates

- Server: actual post/photo/Hac/story reference survives atomic persistence, duplicate client retry, reload and recipient projection. Wrong typed actor, private audience, blocking, revoked access, expired story, deleted post, processing/failed media must not reveal source assets. A captionless share still projects real media.
- Native transport/state: serialize source ID rather than heading; assert decoded card survives store refresh; actual UI open invokes protected resolver/player. Test iOS and Android internal sends to existing/new conversation plus retry; external item contains canonical URL or authorized file, not only caption/heading.
- Web: same reference card renders and opens through cookie/antiforgery-appropriate boundaries, with accessible unavailable states. Existing plain text/reply/translation/file messages retain behavior.
- File attachments: authorized clean bytes/download and native open; blocked cross-conversation actor, pending/rejected scan, deleted message; interrupted upload retry must preserve the existing message/attachment identity.
- Playback/end-to-end: representative image, multi-image post, video Hac, expiring story and PDF/file across web/iOS/Android, internal and permitted external destinations. Validate actual bytes/decoded frame or document open, not a label or share metric. No release until these checks and explicit user deployment approval.

Existing test foundations: AgentPortal.Tests/SocialFeedServiceTests.cs (visibility/engagement), SocialMediaStorageTests.cs and SocialMediaProcessingWorkerTests.cs (media readiness/storage), MessagingServiceTests.cs (attachment and actor authority), mobile messaging controller/translation contract suites, Legend-ios/LegendTests/MobileSocialContractTests.swift. Add behavior tests to these authorities and corresponding Android store/UI tests; do not freeze current text-only output as the desired contract.
