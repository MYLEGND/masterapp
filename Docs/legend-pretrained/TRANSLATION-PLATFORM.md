# Translation and platform implementation evidence

Baseline: `144567d7a39ea76d50f3d5efd3aae08f3595f85e`; shared integration candidate remains uncommitted at specialist handoff. Specialist requested `gpt-6-astra`, `high` reasoning through confirmed agent invocation. No deployment, provider-backed tests, production data reads, database writes, or mobile publishing performed by this specialist.

## Confirmed authorities and contract matrix

| Responsibility | Authority | Consumers |
| --- | --- | --- |
| Source language | `LegendConnectTranslationRouter.DetectLanguageAsync`, existing language registry and `AzureTranslatorService` provider | Founder reply source resolver, messaging body detection |
| Preferred recipient language | `IControlledResourceAccessService` canonical persisted preference | Messaging routing and `ApplicationLocalizationService`; platform catalog clients |
| Translation and reuse | `LegendConnectTranslationRouter`, existing `LegendTranslationAlignments` retained persistence | Messaging, app catalog, notifications, conversation translation callers |
| Azure HTTP | `AzureTranslatorService` only | Router; native clients do not possess credentials |
| Response authority | Existing server reply's optional `responseAuthority` string | Web append/reload rendering; iOS response/transcript model and label; Android response/transcript model and label |
| Hosted primary inference label | Server value `HostedFoundation` | All three platforms display `LEGEND · hosted foundation`, separately from `LegendAi`, `GovernedResearch`, `OpenAITeacher`, `SystemDiagnostic` |

Unknown authority values remain compatible and do not acquire a native label. No new client routing, policy, provider selection, Azure caller, translation store, or schema is introduced. The new label enters the existing shared retained-copy manifest using its generator. iOS authority rendering now resolves through its existing `LegendLocalized` presentation authority; Android already uses `legendLocalized`. Web's existing DOM catalog observer translates static authority copy.

## Direct fixes

1. **Azure automatic source detection bypassed the confidence authority.** `/detect` rejected scores below 0.50, but single and batch `/translate` accepted their automatic detection without score validation. Extracted the existing detection parser inside the existing Azure client and use it in all three paths. Unknown/low-confidence source returns a failed result with no translated content; explicit valid sources remain authoritative. Invalid declared sources now fail before provider calls rather than silently turning into automatic detection. Malformed or out-of-range detection scores fail explicitly.
2. **Cancelled retained-translation waiters could not stop waiting independently.** Extend the existing coalescer contract with a cancellation token, update both router callers, and let joined requests stop waiting. Only completion of the owning factory removes the in-flight entry; a cancelled waiter cannot release it and permit a duplicate charge. The owner continues awaiting its cancellation-aware factory so scoped dependencies are not disposed while their work is running. No detached background task is introduced.
3. **Reordered concurrent batches shared an ordered result list.** The batch coalescing key sorted identities although return values follow request order. Preserve identity order in the coalescing key. Existing sorted provider-capacity reservation identity remains the cross-instance duplicate charge fence. Tests exercise reversed concurrent requests and assert each result order.
4. **Hosted primary answering lacked an honest cross-platform label.** Add `HostedFoundation` to the existing authority rendering on web, iOS, Android. No self-hosted status is claimed.
5. **Web app-copy localization could alter conversation content.** Its catalog observer excludes `[data-user-content]`, but the Founder conversation body lacked that marker. Mark the actual body span while leaving the authority label eligible for app-copy localization. The server's language and explicit response instructions therefore survive subsequent DOM localization and streaming final-result rendering.

Existing retained keys already include stable source ID, source hash, source revision, source/target languages, translation context, placeholder contract, provider/version, reuse scope, and scope hash. Existing messaging source detection explicitly refuses to infer body language from a sender preference. Existing same-language bypass and approved scoped memory precedence are preserved. Semantic text normalization is unchanged; changing its persistence semantics would require separate evidence and migration analysis.

## Verification performed

- `node --check AgentPortal/wwwroot/js/legend-founder-ai.js`: exit 0.
- `git diff --check`: exit 0 at pre-handoff source review.
- `ruby scripts/generate-application-copy-manifest.rb`: exit 0, 5,731 entries; inspected output contains exactly the added hosted label and catalog-version update. No `--rewrite` option used.
- Added `TranslationRequestCoalescerTests`: cancelled waiter/fence preservation, scope isolation/failure release, reversed batch result order through the router with mocked approved evidence and zero provider use.
- Extended `AzureTranslatorServiceTests`: single/batch confidence parity (Haitian Creole high/low scores and malformed/out-of-range scores), invalid source zero-call assertion.

.NET tests were **NOT RUN by this specialist**: shared worktree had no restored test assets and lead owns coordinated builds. Lead/verification must run the focused translation tests plus existing `ApplicationLocalizationArchitectureTests` and `MobileMessagingTranslationEndToEndTests`. Existing suites contain approved retained reuse, scope/revision separation, same-language bypass, translation structure/provenance, retained corruption recovery, recipient preference, and ambiguity cases; their existence is not a pass.

Native iOS/Android builds, device rendering/accessibility, real Azure Haitian Creole quality/latency/cache charges, authenticated Founder language preference flows, candidate-code production-data checks, and the new foundation metadata lifecycle are **UNVERIFIED at handoff**. A string-compatible UI source change does not prove a published mobile release. Backend governed response details remain the runtime owner's scope.

Rollback is ordinary version control of these files; existing records, provider/version identity, schema, registrations, routes, and release workflow remain intact. No speculative retained-cache migration or new paid infrastructure was added.

## Additional bounded multilingual research correction

Lead authorized `LegendConnectInternetResearch.cs`, `LegendConnectOperations.cs`, and `LegendConnectGovernedInternetResearchTests.cs` after cross-agent diagnosis. The search prompt previously simultaneously required exact source-language quotes and response-language statements. It now explicitly preserves original-language quotes and omits unsupported cross-language claim proposals while retaining source discoveries.

`BuildResearchEvidencePacket` previously manufactured `EvidenceExtractionLanguageDeclared` receipts without running any translation. Removed this receipt creation at its authoritative source. Existing admissibility still requires independently validated proposal-bound translation provenance; an Azure output alone would not satisfy that requirement and is never relabeled `GovernedTranslationValidated`.

After normal lineage validation and evidence assessment, a session with no admissible material evidence and retrieved cross-language documents now returns `internet_research_cross_language_translation_unavailable`, preserving original documents, citations, source/query/page receipts and timings. Sessions with sufficient same-language admissible evidence continue even when ancillary sources differ. The runtime can then take the policy-permitted next action.

Added two mocked full-operations tests using Haitian Creole question `Verifye valè mezi sa a avèk sous piblik yo.` and an English original document: no claim proposals and an adversarial translated proposal without validated provenance. Assertions cover explicit failure, zero manufactured translation receipts, source preservation, and no canonical knowledge rows. These added tests await the lead's stable combined build/test run. This is honest service-limitation behavior, **not** completed cross-language research translation or measured Haitian Creole model quality.

## Follow-up metadata parity correction

Independent review found that the initial hosted label fix retained authority but dropped additive capability metadata. Existing web persisted message objects, iOS decoded response and transcript objects, and Android decoded response and transcript objects now retain all six server fields: `foundationModel`, `foundationHosting`, `externalAnsweringUsed`, `escalationUsed`, `researchState`, `learningState`. Existing web localStorage saves these fields; native platforms retain them in their existing conversation state (no new durable mobile store added).

All three existing response bubbles now display recognized server research outcomes, explicit escalation use, and confirmed teaching submission/review or evidence-needed states. They do not infer promotion, native independence, or research from response wording; unknown/null metadata remains unavailable. Debug model settings and backend reason strings are not shown in the status line. Seven status labels were added through the canonical shared copy generator, for 5,738 entries total.

Added Android optional-field JSON roundtrip/legacy compatibility test and iOS streamed final response-to-transcript metadata test. `node --check AgentPortal/wwwroot/js/legend-founder-ai.js` and `swiftc -frontend -parse Legend-iOS/Legend/Features/Home/LegendApplicationShell.swift Legend-iOS/LegendTests/MobileNativeContractTests.swift` both exited 0, as did `git diff --check`. Swift parsing is syntax evidence only; native build/device tests and Android test execution remain unperformed by this specialist.

## Executed native gates (supersedes earlier pending native status)

The lead subsequently authorized native execution. Android debug app assembly and unit-test compilation passed; 10 focused tests passed with zero failures/errors/skips (`FounderAiMobileContractTest`: 4, `FounderAiChatStreamTest`: 6). Gradle completed in 131 seconds, exit 0. The initially requested `MobileContractSerializationTests` class does not exist; no test result is claimed for it. The first attempt failed after 7 seconds due to absent ignored local `legend.properties`; the existing checkout's non-secret runtime configuration was copied into the ignored candidate location, without changing signing settings or source configuration.

iOS app and test bundles compiled with Xcode 26.6; all five selected Founder authentication/streaming/terminal-state/cancellation/metadata tests passed on the iPhone 17 Pro iOS 26.5 simulator, zero failures, 0.134 seconds test execution, xcodebuild exit 0. Signing was disabled, cached Swift packages reused, and candidate DerivedData isolated. These tests use mocked transports; they prove native code/contract execution, not live production behavior or end-user layout quality.

Exact commands, result counts, artifacts and source SHA-256 values are recorded in `NATIVE-VERIFICATION.json`. No signed release was built/published, no project signing configuration changed, and no production mutation occurred. Existing unrelated running simulators were not changed or shut down.

The integrated adversarial cross-language test initially failed: the new limitation branch counted `MaterialEvidence`, which intentionally includes `ObservationOnly` rows for provenance. Corrected the authoritative branch to consult existing `Admissibility` dispositions (`ControllingEvidence` or `CorroboratingEvidence`). Unsupported translated observations no longer bypass the explicit limitation. The adversarial test assertions were not changed; a rebuilt candidate rerun is required.

## User-corrected local-primary contract (pending combined verification)

The corrected shared contract agreed with lead/runtime is `responseAuthority=LocalFoundation`, `foundationHosting=LegendControlled`, `externalAnsweringUsed=false`, `escalationUsed=false` for controlled local primary inference. All three existing client presenters now show `LEGEND · local foundation`; historical `HostedFoundation` records retain their explicit hosted label. Existing response fields `modelAssistanceState`, `modelVersion`, `modelTrainingRunId`, and `modelProvenance` are retained through the same DTO/transcript paths, with no new lifecycle fields. Only a server-projected Applied model with a run identity displays `Promoted model applied`; an installed base checkpoint alone displays no training/promotion claim. Native contract tests distinguish local pretraining from trained-run lineage. Earlier native PASS evidence applies to its recorded file hashes; these follow-up changes require updated native execution.

Independent read-only external-boundary inventory:

- Conversation provider client: owned by runtime; it must become optional explicit escalation, with controlled local generation reusing the same ReplyAsync orchestration. Local transport endpoint/model must be pinned; HTTP redirects must not escape to an external answering endpoint.
- `LegendConnectOperations.TryApplyPromotedReasoningModelAsync` and translation router currently guard external promoted model inference under NativeOnly. Their shared active-model/inference interfaces do not carry provider policy and DI currently names an OpenAI transport. Changing caller reachability must not silently bypass these guards.
- `IRetainedTranslationService` and provider batch translation do not currently accept request provider policy. Independent conversation must not enter these policy-unaware external translation paths. Existing single translation/detection supports explicit provider policy.
- Public research search is an external generative boundary (`LegendConnectConfiguredReadOnlySearchTransport`); Operations research decision and execution both enforce policy. Page retrieval is external retrieval rather than answering. These capabilities remain separately attributed.
- `LegendConnectAutonomousGapPlanner.SelectApprovedGapAsync` is SQL-only. Autonomous teacher/critic, training upload/job creation and evaluation judge are actual external OpenAI calls. `legend_activate_autonomous_learning` toggles global lifecycle settings without a request provider policy; a new local tool loop must not treat retention permission as permission to enable external learning. Reported to lifecycle owner.
- `LegendBlindBenchmarkRuntimeAuthority.ExecuteLegendAsync` omitted provider policy when invoking the nominal native conversation inference and then emitted native candidate provenance. This can admit hosted promoted-model inference in a supposed independent candidate. Reported to lead/runtime for explicit policy repair. Its baseline and judge are deliberate separate external calls and require separate authorization/attribution.
- Selected-section diagnostics use Founder-authorized data projection; no generative call was found in that section read. Broad readiness/provider-capacity reads already forward provider policy. Their absence of generative calls is not evidence that all external non-generative tooling is forbidden.

No paid model or translation canary was run during this correction. Training-rights and local checkpoint admission contracts remain with the learning/runtime owners for independent review when delivered.


## Partial responses and teacher training disposition

Web persisted messages and native transcripts now retain the existing `reason` plus the separately approved `escalationDisposition` field. The actual `provider_output_incomplete` reason displays “Partial answer: output limit reached”; a produced partial answer remains visible without presenting it as completed evaluation evidence. Actual `Restricted` teacher material displays “Teacher material restricted from training,” independently of an awaiting-review learning candidate. Unknown dispositions do not produce invented lifecycle states. All labels use the existing shared application copy catalog (5,743 entries after this update). Android roundtrip and iOS streamed transcript assertions retain these states alongside local/external provenance.

Independent `LegendLocalFoundationSecurityTests` exercise strict local-client selection with external credentials present, missing/NativeOnly policy rejecting unknown models without any HTTP client, public/ambiguous endpoints, mismatched model/revision/adapter/hosting receipts, redirected actual destinations, malformed terminal and nested output data, and incomplete output excluded from completed evaluation text. These are controlled transport fixtures; execution awaits the stable combined candidate build. Node syntax and Swift app/test parsing passed after the metadata updates. Earlier native execution results do not cover these new source hashes.


Independent follow-up review confirmed the compiler now traces `CurriculumExample → CurriculumFamily → TeacherProposal` to exclude hosted teacher-derived material even after `SystemValidatedMachine` canonicalization. Rights attestations remain hash-bound and distinct from factual validation. Existing promoted translation/reasoning callers retain their NativeOnly guards; their hosted task transports now receive explicit ProviderEnabled only behind those guards. Controlled ordinary conversation uses the shared registry resolver. A promoted local translation model remains blocked by the existing blanket NativeOnly translation guard; this is an outstanding functional limitation, not evidence that external translation is allowed in independent mode.


## Final local-primary native gate

The final client source above was rebuilt and executed after the MLX training evaluation released memory. Android debug app assembly and focused unit tests passed in 34 seconds: `FounderAiMobileContractTest` 5/5 and `FounderAiChatStreamTest` 6/6, zero failures/errors/skips. iOS app/test bundles compiled; all five selected Founder stream/authentication/terminal-state/cancellation/metadata tests passed on the same simulator, 0.155 seconds test execution and 92.101 seconds runner time. No signing or project configuration changed. Node and Swift syntax checks also passed. `NATIVE-VERIFICATION.json` now records the final exact source hashes, commands, logs and xcresult; all captured source hashes were verified unchanged after both runs. Earlier evidence remains nested in the record for traceability. This validates mocked native application flows, not production deployment, real end-user layout, or model promotion.


## Final failure-copy source correction

Independent tracing found that unsuccessful responses never enter the web conversation body marked `data-user-content`: stream consumption throws into the ordinary status surface. The actual defect was concatenating the backend English summary with raw diagnostic fields, which prevented exact shared-catalog lookup. Web now preserves the backend summary alone; iOS retains the summary and applies `LegendLocalized` in its status presenter; Android retains the summary for the existing `legendLocalized` status card. Typed response diagnostics remain available on their contracts. No reason-to-message map or Azure caller was added.

Four stable backend native-only/local-model failure literals now use the existing `ApplicationCopyText.Source` marker at their authoritative declarations. The existing copy generator includes marked AgentPortal Services sources; the catalog contains 5,747 entries. Existing cached catalog presentation handles those exact messages; unknown unregistered text still retains its source rather than being silently sent to another provider. Four direct checks against the actual web failure formatter passed, covering exact-summary preservation, typed diagnostic retention, empty-error fallback, and missing-envelope fallback. Native gates require the subsequent recorded rerun for this final source change.


The final failure-copy change was rebuilt and reverified: Android debug assembly plus 11 focused tests passed in 15 seconds; iOS app/test compilation plus all five selected tests passed, 0.068 seconds execution and 30.368 seconds runner time. Updated exact hashes and logs in `NATIVE-VERIFICATION.json` match the tested files after both runs. These are mocked application-flow checks and catalog source-path verification; no new live Azure call or translated production UI claim is made.


## Independent-answering request and controlled remote serving

Existing web/iOS/Android requests now carry optional `externalAnsweringBlocked` separately from strict `nativeOnly`. The independent-answering control allows separately authorized research/translation while the backend denies externally hosted generative inference; strict mode continues to block every external provider. Existing conversation state retains the flag; changing the control starts a clean conversation. The `LocalFoundation` authority identifier remains compatible, but the visible label is now “LEGEND-controlled model,” which does not imply the user's device hosts it. Catalog generation produced 5,755 entries. No new Azure caller, translation store, policy map, or model workload was introduced.

Seven checks of the actual web conversation-state function passed. Final Android assembly and 12 focused contract/stream tests passed in 32 seconds. iOS app/test bundles compiled and all six selected tests passed (0.084 seconds execution; 32.255 seconds runner), including the actual authenticated request body carrying `externalAnsweringBlocked=true` and `nativeOnly=false`. Exact hashes/commands/logs are recorded in `NATIVE-VERIFICATION.json` and matched after both gates. Server tests for known Azure translation, unknown policy-unaware boundaries, generative research zero-call/no-receipt behavior, and controlled remote receipt identity await the lead's combined .NET execution. They use recorded transports and are not remote model acceptance.
