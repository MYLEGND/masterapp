# Authorized baseline release policy

The owner explicitly authorized “baseline with nine known failures” on 2026-09-11. This is a limited release acceptance decision, not evidence that the nine failures are repaired, that LEGEND is fully autonomous, or that all platforms have identical features.

## Boundaries

The existing production PR synchronization workflow remains the sole release authority. The protected security job executes the complete unfiltered .NET regression suite and validates its fresh TRX, runner exit status, complete test roster, counters, identities and outcomes. The manifest binds the exception to runtime source `5e2fca048d1cee2480b1f577d445cfb9d9d58c4e` and production base `262428f38c8e1dd0ee72a362983b277f493fd362`. Only the seven enumerated release-policy/documentation/verification files may differ. Advancing production expires the exception.

The nine held-out failures retain their failed outcomes. Seven report `meaning_graph_component_unknown`; two report `semantic_transition_not_supported`. Exact identities, safe reasons and message hashes are frozen. Additional failures or skips, changed failure messages, missing tests, incomplete results, stale results and unauthorized runtime changes reject the exception. Four existing opt-in tests remain unexecuted in the ordinary suite and are not live-validation credit. A future genuinely zero-failure run uses the same structural validator without inheriting this one-time runtime exception; new skips are still rejected.

Authorization, actor isolation, provider policy, database permissions, migrations, artifact/tree identity, deployment readiness and post-deployment proof requirements remain in force. No blanket continue-on-error or test-result relabeling was introduced.

## Verification harness corrections

The canonical post-deployment SQL matrix previously could obtain the application's database connection and did not require the SELECT-only principal check outside isolated mode. It now uses the existing `LEGEND-Production-ReadOnly-Validation` environment and its existing restricted connection/Founder references. The existing permission authority verifies effective database permissions before business reads in both modes. The matrix retains its release-workflow authority and must explicitly prove its SQL principal. No new credentials, database, workflow or deployment authority was created. The environment currently has no reviewer protection rules; its existing configuration was preserved.

The old `UnifiedProductionFlow_RecognizesBothCurrentDotnetTestSuccessFormats` source-contract test required console success strings. Complete structured TRX validation replaced that parser, so this stale assertion failed in the initial final-suite run. Its replacement verifies the unfiltered TRX invocation, captured runner status, manifest/source/base/freshness arguments and absence of blanket failure suppression. An independent specialist reviewed and approved this harness correction. Only that roster identity and the changed contract-file hash were refreshed; held-out prompts, expected answers and assertions were unchanged.

Raw test logs, discovery output and TRX remain runner-private. Published release/provider/SQL receipts contain bounded identities, counts, safe reasons and hashes rather than raw exception messages or prompts.

## Cross-platform scope and remaining evidence

Web and mobile Founder chat use the existing shared conversation service and shared HTTP streaming transport. iOS and Android consume one authenticated streaming POST with cancellation and explicit terminal/error handling. They do not implement separate reasoning engines. Prior verification on unchanged runtime source recorded 39 Android tests and 50 iOS contract tests passing. Native releases still require new distributed artifacts. Conversation-history discovery/resumption and all web-only UI capabilities are not proven identical across platforms.

Prior restricted SQL observation at `c1e4172f` completed all 53 SELECT calls without SQL failure, but its six-case observation had two native failures and exceeded the unsupported-request latency target. This is not a passing production proof. Authenticated Founder HTTP reproduction and final-candidate live provider/SQL/post-deployment checks remain unexecuted until their authorized workflow runs. The four opt-in skips cannot stand in for these checks.

## Final local verification

Candidate `3debcadf1857ddabb7f9cd7a394ce3db1edcb53a` built in Release with zero warnings and zero errors (52.64 seconds). Its complete unfiltered regression run completed in 6 minutes 11 seconds: 2,371 total, 2,358 passed, nine failed, four skipped. The actual runner exit was 1. The committed manifest validator returned `AcceptedKnownBaseline`, retaining those nine failed outcomes and four unexecuted cases. All 18 validator checks passed, including rejection cases and an actual captured full run. Workflow YAML parsed and the patch passed whitespace checks.

The initial pre-amendment run recorded 2,371 total, 2,357 passed, ten failed and four skipped; its only additional failure was the separately reviewed stale console-parser assertion described above. Both runs and build logs were preserved locally. Final documentation-only commits do not alter the tested runtime or verification sources.

At documentation commit time, GitHub production remained `262428f38c8e1dd0ee72a362983b277f493fd362`; no merge, deployment or final-candidate live checks had run. Publication and deployment must be established by the subsequent production workflow, not inferred from this local baseline decision.
