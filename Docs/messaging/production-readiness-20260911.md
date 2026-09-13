# Messaging and social release candidate — 2026-09-11

## Status and scope

Prepared locally; **not deployed, pushed, or approved as fully live-verified**. The user explicitly holds deployment. Existing Mac production checkout and signed Android release bundle remain untouched. Candidate code is `c00c77a8` on `repair/social-creation-20260911` in `/private/tmp/legend-social-creation-20260911`.

The candidate corrects the demonstrated IIS media-upload routing failure, retains the previous durable messaging/translation repairs, adds bounded chat paging and refresh ownership, accurately bounds read acknowledgments, provides latest-read/newer-sent receipt styling and server-authoritative reactions, and renders authorized shared content and attachment bytes across web, iOS and Android. Blob video processing extends the existing storage authority. An additive messaging migration is prepared, not applied.

The same backend owns recipient translation policy, conversation membership, read boundaries, reaction state, and shared-content visibility. Native clients use their platform presentation over those contracts. One canonical design/localization catalog is packaged for all clients, including retained entries needed by older installed versions. No alternate inference, translation, storage database, or deployment authority was introduced.

## Verification

- Full combined .NET suite at `dfcbcef7bbd82ca9dfce0285364a3c598f769845`: **2,430 passed, nine failed, four skipped; 2,443 total**. Each of the nine failing test identities and error-message SHA-256 hashes exactly matches the previously accepted native-AI baseline. They remain failures, not waived passes. The four opt-in cases were not executed and do not establish live SQL validation.
- Final change `c00c77a8` only preserves canonical localization entries for older installed versions and changes catalog generation accordingly. Generation is idempotent; all previous entries are retained; 5,186 source/context identities are unique. All 17 focused localization tests passed after this change. The final Release build completed with zero warnings and zero errors. Independent review passed. Refreshed packages are recorded in the accompanying local verification JSON.
- iOS simulator: **85 passed, zero failures** after the final catalog update.
- Android: **51 unit tests passed, zero failures/errors/skips**; debug APK built locally.
- Messaging JavaScript: **16 passed**, plus syntax validation. Tests cover actual extracted runtime functions and controlled transport/DOM behavior, including races, receipts, sharing and retry ownership.
- Blob orchestration: nine executed tests passed on this Mac. These test the processor contract and safe cancellation/conflict handling; they are not real production codec validation.
- EF reports no pending model changes. The generated idempotent migration script adds one nullable shared-post reference and the message reaction table; no production schema was changed.

Full results: `/private/tmp/legend-chat-results/chat-full-final.trx`. Baseline comparison: `/private/tmp/legend-messaging-results/messaging-reviewed-full.trx`. Local packages, prepared migration SQL, source bundle and machine-readable verification are under `/private/tmp/legend-chat-release-candidate/`. There is no newly signed App Store archive or Play release bundle in this preparation.

## Remaining release and evidence gates

1. **Shared storage:** both deployed apps currently select their own local media disks. The client lacks a managed identity. Configure the existing private container through existing settings, grant the client only container-scoped read access, and migrate/verify retained portal assets and previews without deleting originals. These live changes are held with deployment. See [the exact prerequisites](../social/shared-media-production-prerequisites-20260911.md).
2. **Upload and processing:** the production IIS StaticFile misrouting is reproduced and its configuration correction is packaged. Only an authorized deployment followed by authenticated upload/processing/playback checks can prove the live correction. Existing release smoke checks were extended locally, not executed remotely. A memory-only production codec probe timed out after 30 seconds and did not prove codec success.
3. **Messaging translation:** the reported saved languages are English for both Founder profiles and Haitian Creole for the recipient. Affected-account entitlement/quota and live bidirectional translation remain unverified. No test messages were sent to other people. Founder browser access was unavailable.
4. **Latency:** local paging, batching, coalescing and ownership tests pass. Production completed-response latency distributions are not measured after these changes. Cold uncached translation can still require serial provider work; no Instagram/Apple Messages performance equivalence is claimed.
5. **Baseline AI:** the nine known native reasoning failures remain outside this messaging/social repair; four opt-in tests remain skipped. This candidate does not establish autonomous LEGEND readiness.
6. **Release order:** after explicit authorization, use the existing release authority for required configuration/migration, reviewed source, matching artifacts, deployment and original authenticated reproductions. Do not count source tests or a diagnostic-only check as cross-platform live success.

No production deployment, business-data write, storage migration, permission grant or remote configuration change was performed by this preparation. The working production checkout remains `262428f38c8e1dd0ee72a362983b277f493fd362` with its pre-existing untracked Android instructions preserved.
