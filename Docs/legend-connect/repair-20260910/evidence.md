# LEGEND repair evidence — 10 September 2026

Release status: pending execution; not production-ready.

## Preserved baseline

Mac checkout `/Users/zacowen/MASTERAPP` remains on production `262428f38c8e1dd0ee72a362983b277f493fd362`. Its untracked `Legend-Android/AGENTS.md` remains untouched. Preservation directory `/private/tmp/legend-repair-preservation-20260910-173547` contains staged/working binary patches, copied untracked files for both available worktrees, refs, reflogs, unpushed commit list, worktree inventory, and a verified complete Git bundle. Ignored build artifacts remain in the original checkout. No reset, clean, pruning of worktrees, or branch deletion was used.

Integration branch `repair/legend-integrated-20260910` is isolated at `/private/tmp/legend-repair-integration-20260910`, created from freshly fetched production. Specialist branches preserve their separate changes.

## Source reconciliation

| Source | Finding | Disposition |
| --- | --- | --- |
| Validation `131daf7b` | 65-file cumulative repair from common base `75b773bb`; includes earlier `9fdb28a` and substantive PR99 repairs | Three-way integrated once as `9bdff6e9` |
| PR99 `6759dfed` | Mostly incorporated and extended by validation, including provider-policy and read-attestation contracts | No duplicate replay |
| PR108 `f8e789a1` | Missing three-file bounded diagnostic section/receipt repair | Applied once as `a7fb5809` |
| Follow-up `92f684b1` | Not recoverable from local objects, reflogs, unreachable objects, searched local artifacts or GitHub commit API | Equivalent parts identified in `0b78db82`; missing diagnostic behavior explicitly reconstructed |

Four shared files required three-way combination: `LegendConnectContracts.cs`, `MessagingModels.cs`, `AzureTranslatorService.cs`, and `LegendConnectTranslationRouter.cs`. The merge preserves production's newer calling and retained UI translation changes. Native application and design trees, mobile controllers, canonical production release workflow and migration files are unchanged by the repair integration.

## Confirmed failure records

### Cancellation propagation

Candidate `a7fb5809`, controlled local boundary tests. Detection could return a language or enter provider fallback after a graph authority completed with cancellation requested. First incorrect decision: `LegendConnectTranslationRouter.DetectLanguageAsync` consumed a completed authority result without rechecking cancellation. Repair adds checks before and after asynchronous boundaries; no language assumption, answer mapping or new runtime authority. Seven new cancellation cases require cancellation and zero forbidden downstream calls. Execution results recorded below when run.

### Diagnostic follow-up

Candidate `a7fb5809`, code-level reproducible contracts. Aggregate all-failed stage outputs can be treated as successful; equivalent null/default argument formats are assigned different scope identities; timestamp provenance is not independently checked; arbitrary exception messages can reach tool output; per-round tool availability can remain stale within the round. Existing authorities are being corrected with new negative tests. A returned failed observation must remain a domain failure even when execution completed normally.

### Request observation

Controller request logging previously lacked one explicit correlated start/end pair and separated execution/domain results. Existing `LegendFounderAiController.ExecuteAsync` now reuses the server Activity, records a correlated request boundary and completed service-response timing, and removes raw exception logging at that boundary. Streaming transport completion remains distinct from service completion; browser end-to-end latency is still unmeasured.

## Unexecuted live prerequisites

Browser runtime reports no available browsers. Existing Founder authentication cannot be accessed until the browser is connected. No cookie/session extraction or authentication workaround was attempted.

GitHub environment `LEGEND-Production-ReadOnly-Validation` has empty secret and variable name lists at inspection. Required secret `LEGEND_PRODUCTION_SELECT_ONLY_CONNECTION` and Founder identity configuration are absent. Process `LEGEND_PRODUCTION_READONLY_CONNECTION` and `LEGEND_PRODUCTION_READONLY_FOUNDER_OID` are also absent. No application write-capable credential was substituted; effective SQL permissions are unverified because no connection was opened.

The ordinary process includes an OpenAI key; SELECT-only native preflight must run in a subprocess without provider credentials. No value is recorded here.

## Evaluation integrity

`evaluation-freeze.json` records SHA256 of existing held-out/evaluation authorities before new repairs. Historical counts are not current evidence. Existing broad held-out fixtures contain language/identity seeds without admitted semantic curriculum; downstream capability is unexercised if that prerequisite fails. No expected answers are added as teaching. Comparative superiority requires the existing blinded benchmark gates; no such claim is made.

Six specialist responsibilities were covered by configured GPT-6 Astra agents in waves. Runtime thread limits prevented six separate specialist threads; existing configured agents were reused for language and evidence roles. Only the lead runs combined .NET builds/tests.

## Executed baseline (not the final candidate)

At `3c8bd760`: zero-warning build passed; 138 focused language cancellation, detection, lifecycle SQL translation, computation and evidence cases passed, zero failed/skipped. These are controlled local tests, not authenticated production observations.

At `24d1856c`: zero-warning build passed. Full regression: 2,195 total, 2,182 runner passes, nine failures, four skipped result records. Source/TRX audit identified 24 environmental early-return passes (23 SQL and one provider canary), leaving 2,158 other local passing executions. With four skips, 28 SQL/live opt-ins were unexercised. Nine held-out capability cases fail at `meaning_graph_component_unknown` / zero eligible evidence, so arithmetic/planning/rewriting/deduction/causality/Creole/memory/tool capability is not certified by this run. Elapsed full run: 4m30s. TRX `/private/tmp/legend-repair-results/combined.trx`.

Package vulnerability audit completed against NuGet: all nine solution projects reported no vulnerable packages. No dependency versions changed.

## Separate harness correction and independent review

The Mac's Bash 3.2 reproduced `set -e; [[ "" == x ]]; echo SURVIVED` exiting zero. The unchanged checker consequently reported `inputs_available` even though both SQL inputs were absent. This is a validator defect, not evidence of available credentials. Root and the evaluation specialist independently reproduced it. Commit `bdf11960` (integrated `94ae5c93`) adds explicit `|| exit 1` to eleven mandatory guards without changing reason codes, thresholds or assertions. The original held-out freeze remains intact; the runner's original hash remains in the freeze and this narrowly reviewed implementation amendment is recorded separately.

The existing simulation script gained an explicitly labeled configuration-only selection of six existing cases, not a replacement acceptance gate. Original runner fails the same missing-SQL assertion; repaired runner passes all six. GNU `timeout`, required by the existing full harness, was absent from macOS; coreutils was installed as a developer validation prerequisite, without changing the application dependencies or deadlines. The complete existing 92-case simulation then passed at `bdf11960`. This is harness evidence only: zero real SQL or application builds were executed by the simulated cases. Log `/private/tmp/legend-repair-shadow-full.log`.

## Additional defects established by independent review

Both native-only unsupported and native/provider unavailable results incorrectly used `Succeeded=true`. This labeled an inability to answer as domain success and could mislead clients and telemetry. The repair must preserve useful safe diagnostics while marking failure explicitly; this is not a test-harness correction.

Native exception stack/message strings also flowed through trim-only failure-detail handling to provider context and user output. Safe bounded reason codes and exception types replace that flow. No raw provider error body or exception message should be diagnostic evidence.

A completed tool function did not necessarily produce an available observation. Failed tool progress now has a distinct domain-failure stage; the existing stream transport tracks latest results by the canonical argument-scope identity and keeps independent failures in RemainingWork. Server response-completion timing is separate from service timing and cannot substitute for browser-receipt latency.
