# Shared content backend contract

Existing web/mobile send and start requests accept optional `sharedPostId` (UUID). An empty commentary body is allowed only with a currently visible source reference. The original message and nullable `InternalMessage.SharedSocialPostId` are saved by the existing messaging transaction. A reused client-message ID cannot silently select a different source reference. No social media bytes, private contact fields, or source permissions are copied into a second authority.

Existing web/mobile message responses add optional `sharedContent`:

```
{ sourcePostId, status, contentType, body, authorDisplayName, media, url }
```

`status` is `available` or `unavailable`. Available fields are projected through the existing social visibility authority for the current typed recipient. `media` uses the existing SocialMediaAssetView field names and ordered asset IDs. Unavailable cards have null descriptive fields and an empty media list. `url` is `/Social/Posts/{sourcePostId}`. Message access does not grant source access; publication/readiness, private-profile, block, deletion and expiration rules remain in SocialFeedService.

`GET /api/v1/mobile/social/posts/{postId}` returns the existing privacy-aware MobileSocialPostDto for full native opening. Existing protected social media endpoints remain authoritative for bytes. The canonical signed-in web route `/Social/Posts/{postId}` renders encoded source content and image/video elements. Its `/Social/Media/{mediaId}` streams recheck the same social authority and support ranges. These are authenticated links, not anonymous media exports or bearer-token-bearing URLs.

`GET /api/v1/mobile/messaging/attachments/{attachmentId}` resolves the typed mobile actor, calls the existing messaging clean-scan/download authority, and returns the existing storage stream with MIME type, filename and range processing. No public-storage shortcut is added.

Source-only notification preparation uses the current recipient-visible card only to choose localized application copy (`Shared content` or unavailable). It never copies a private source caption, author contact field or media URL to notifications. Empty commentary is not a translation source. Message cards, rather than the notification label, deliver the actual content.

Controlled tests cover captionless source persistence, first/existing conversation sends, idempotent retry, actual media IDs, private/deleted-source revocation, source-only notification privacy, and clean/pending/rejected mobile attachment streaming. Integration owns the combined generated EF migration/snapshot, compilation and execution. This work is local and does not authorize deployment.
