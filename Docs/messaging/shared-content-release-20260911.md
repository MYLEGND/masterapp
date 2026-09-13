# Chat priority, original shared content, and release efficiency

Implementation candidate: `1f409a06f8ba81444c8713715d92377b389c1544`, branch `repair/call-experience-20260911`, based on `e3e34ee4`.

## Repairs

- Selected chats preempt inbox/history work. Native navigation retains known identity/cached messages and cancels obsolete history and actual Android attachment transfers. Circular chat/history loaders are replaced with nonblocking presentation; network completion is not represented as instantaneous.
- Shared social messages open their original post through the existing social authority and preserve post, story expiry, HAC, visibility, moderation, and actor rules. Native clients reuse their existing social detail and mutation paths. Web replaces the former raw preview with an interactive original view using the same service for reactions, comments/replies, save, repost, and share.
- Shared media envelopes are transparent, with readable captions. Images have authenticated bounded previews; other attachments retain actual file viewers and file sharing. External social shares use canonical access-controlled links. Sharing a link does not grant content access.
- Web mutations retain antiforgery checks. Access revocation clears content; uncertain mutation outcomes are not automatically retried. Internal author/comment phone fields are excluded. Author images require current post visibility.
- Both web hosts now use one localization controller base and one browser script. Existing client web copy and new labels enter the canonical application-copy manifest. User names, captions, and comments are excluded from UI-copy translation.
- Messaging projects shared content after resolving the recipient actor once per page, retaining per-post authorization.
- The protected release workflow reuses candidate-bound EF startup binaries instead of recompiling them during migration. Candidate/tree identity and hashes of AgentPortal, Infrastructure, Domain, and Shared must match the published package. Migration, security, and deployment gates remain intact.

## Verification

- iOS native/social contracts: 94 passed. Android focused chat/shared-content tests: 9 passed, including cancellation of a real held attachment transfer.
- Web behavior: 50 passed. Manifest generation is deterministic (5,655 entries; 412 newly admitted static application-copy entries); user content is excluded. Combined .NET: 2,520 total, 2,507 passed, nine failed, four skipped, duration 6m11s. Comparison against `/private/tmp/legend-chat-latency-final/full.trx` shows no added/removed failures, unchanged failure-message hashes, and identical skipped identities. Evidence: `/private/tmp/legend-sharing-release-full/full.trx` and `baseline-comparison.json`.
- A preliminary focused backend run passed 185 tests. A test assertion in the first run expected an exact MVC subclass; it was corrected to check the actual HTTP status while preserving the moderation requirement. No held-out requirement changed.
- Windows artifact upload/download and EF target restoration require CI execution. The removed duplicate compilation took approximately 132 seconds in an inspected production run; no end-to-end workflow speedup is claimed before measurement.
- Browser visual verification is unavailable: the browser runtime returned no connected browsers. No authenticated candidate production latency or physical-device weak-network measurement has run for these final changes.

## Release constraints

The earlier full local suite has exactly nine known failures and four skipped tests. Its release waiver is source-bound and cannot automatically authorize arbitrary new source. The independent SQL production proof has separate observed failures; it is not covered by the nine-failure local baseline. No test, expected answer, release gate, or evidence authority is weakened by this change.

Production deployment, app-store uploads, and `legend-release` have not run for this candidate. Native changes require new app builds after the release prerequisites are satisfied.

## Publication and policy review

Automatic approval review rejected `git push -u origin repair/call-experience-20260911`: it did not accept the existing release request as authorization for this exact source publication to MYLEGND/masterapp. No push, merge, deployment, or indirect publication followed.

The existing source-bound manifest also predates this candidate and current production base. Its exact nine/four exception cannot be reused merely by ignoring the source check. One frozen file contains an earlier, separately reviewed UI contract change: `LegendFounderAiContractTests.LegendConnectPage_KeepsHeroVisibleAndOpensSectionsInAccessibleModals` replaces the obsolete accordion expectation with the explicitly requested accessible modal layout. That diff changes no native evaluation behavior. The other three frozen files are unchanged. Any authorized manifest renewal must bind the reviewed UI contract hash and final source/base, retain exact failure messages and skips, and leave SQL proof requirements unchanged.

The eight SQL-proof failures are identical across runs 34657690811 and 34661489031, including reason sets, and predate this candidate. Both runs deployed successfully before that mandatory final proof failed. These are overall-run failures, not evidence that those deployments rolled back or were blocked. Candidate-specific live proof remains unexecuted.

Baseline-validator safeguards: 17 passed; one optional actual-capture test not configured (separate from the four .NET skips). No live evidence is inferred from that check.
