# Cross-platform authority and release verification — 2026-09-11

The production-base request exposed a real native transport defect. All three clients already reach `LegendFounderAiConversationService`, but web used NDJSON on the original response while native clients waited for final JSON and listened for progress on another connection. Android retained its ordinary 30-second read timeout. Progress on the separate GET could not keep the chat POST alive.

## Shared authorities inspected

| Concern | Existing authority and observed scope |
| --- | --- |
| Inference | Web `/founder/legend-ai/chat` and mobile `/api/v1/mobile/founder/legend-ai/chat` call the same scoped `LegendFounderAiConversationService.ReplyAsync`. iOS and Android use the mobile route. Provider policy, language identification, governed tools and native reasoning stay in that service and its existing authorities. |
| HTTP transport | `LegendFounderAiHttpTransport` contains the extracted web NDJSON framing, status mapping, safe execution handling and correlation. Both controllers delegate to it. No second inference service is introduced. |
| Authentication | Web retains cookie/Founder/antiforgery protection; mobile retains bearer scope, Founder authorization and selected-actor resolution. Different active member and agent profiles are not interchangeable identities. |
| Account data | Account-owned fields stay in existing AgentProfiles/ClientProfiles. Mobile social preferences stay in existing MobileProfileSettings. No parallel database, schema or copied account record was added. |
| Application language | Web and mobile localization controllers call the same `IApplicationLocalizationService`. `ControlledResourceAccessService.GetCanonicalPreferredLanguageAsync` reads the canonical actor preference; catalog retrieval and translation reuse remain server-owned. |
| API origin | Existing ignored Mac native build configurations for both iOS and Android specify `https://portal.mylegnd.com`. This is configuration inspection, not verification of every installed binary. |
| Azure storage target | Production-slot configuration inspection found masterapp-portal, masterapp-client and masterapp-parfait resolve to the same server/database target. Connection configuration was inspected only in process; credential values were never printed or retained. No application connection was used for SQL validation or writes. |

## Transport correction

New native requests opt into `application/x-ndjson`. Accepted, progress and heartbeat frames are advisory; only a terminal result can complete the request. Four-second heartbeats travel on that same POST. Structured unsuccessful results remain failures even though an accepted stream uses HTTP 200. EOF without a terminal result fails. Unknown advisory frames are ignored consistently. Stop cancels the original POST and the server joins the cancelled execution before disposing transport resources.

The old unused Android Retrofit chat declaration and the native parallel progress GET usages were removed. Existing server progress routes and mobile JSON status/error envelopes remain for compatibility. Client network timeouts were not raised, and no background response, provider bypass or durable-resume claim was added.

The iOS controlled tests exercised the existing URLSession transport/store with split frames, structured failure, incomplete streams and cancellation. Android tests cover equivalent framing, typed request/authentication and cancellation. An additional real TCP test uses the unchanged 30-second read timeout, emits eight heartbeats four seconds apart and receives the final response after more than 32 seconds. This is local transport evidence, not an Azure gateway, production account or native reasoning proof.

## Explicit limits

- Existing conversation IDs and visible history are client-local. Server discourse is scoped by canonical Founder actor and conversation UUID, but no cross-device transcript discovery/resume was added. Same backend does not establish shared visible history.
- Native screens still omit some web diagnostic metadata and explicit mutation-confirmation interaction. Central authorization remains enforced; missing controls were not bypassed.
- Source and controlled tests do not prove that the same account/profile succeeds through authenticated web, iOS and Android production requests. Those checks remain unexecuted.
- Native transport changes require new app artifacts and distribution. The existing signed Android AAB and working Mac production checkout were preserved. No archive, store upload, merge or deployment was performed.
- The nine frozen native capability failures remain a release blocker. The c1 restricted SQL observation completed 53 SELECTs without SQL errors but still failed two native cases at the 128-declaration bound and did not meet latency requirements. No SQL or curriculum runtime change was made in this transport repair.

## Release authority

The existing production workflow runs only on synchronization of a non-draft production PR. Its protected `security` check requires the unfiltered regression suite to finish with zero failures before merge and deployment. Administrators are subject to that protection. No gate, threshold, expected answer, evaluation exclusion or deployment authority was changed. The candidate remains a draft review until those requirements pass.

See `cross-platform-verification.json` for exact component identities, executed counts and evidence paths. The source-contract relocation and independent review are recorded in `shared-chat-transport-amendment.md`.
