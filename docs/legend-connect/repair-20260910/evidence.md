# LEGEND repair evidence — 10 September 2026

Release status: blocked by native capability/performance verification. Restricted Azure SQL access is now verified; no deployment performed.

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

## Historical live prerequisites (superseded by the restricted-access verification below)

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


## Restricted Azure SQL access — completed with explicit user authorization

The production workflow's Azure OIDC retrieval route was inspected without using its application connection for validation. That connection authenticates as the SQL server administrator. The existing Entra-token command `python3 /private/tmp/legend-repair-access/inspect_entra_permissions.py` failed login with SQLSTATE 28000 / SQL 18456. Administrator use was confined to security-catalog inspection and the subsequently authorized security DDL; no application rows were read under administrator authority.

The database had no non-system SQL/Entra user available to reuse. The user explicitly authorized one contained `legend_candidate_validation` identity, CONNECT, object-level SELECT on required existing tables, and VIEW DEFINITION. It was created in existing MasterAppDb. A reviewed static dependency manifest contains 34 model-mapped tables; 33 passed the existing physical-source checks and received SELECT. `LegendLanguageContextRelationships` was excluded because of its persisted computed column, preserving the existing source guard. No schema-wide SELECT, db_datareader membership, application write permission, delegation, new workflow, new Azure identity, or change to another user's permissions was introduced.

Two actual validator prerequisites failed before any business-data observation: SQL 451 on metadata UNION column 2, then rejection of two metadata subpermissions implied by VIEW DEFINITION. SQL's own `fn_builtin_permissions('DATABASE')` confirmed VIEW SECURITY DEFINITION and VIEW PERFORMANCE DEFINITION are covered by VIEW DEFINITION. These narrowly reviewed harness corrections are recorded in `harness-amendment.json`; original freeze hashes remain preserved. The same newly created principal's unstored password was reissued after the failed setup attempts; no duplicate principal was created.

The final contained-user connection passed the existing identity and complete effective-permission checks: 1,548 metadata records, only CONNECT, SELECT and definition visibility permissions, no grant option, mutation, delegation or ownership authority. Both existing environment references were securely configured in LEGEND-Production-ReadOnly-Validation: LEGEND_PRODUCTION_SELECT_ONLY_CONNECTION and LEGEND_PRODUCTION_READONLY_FOUNDER_OID. Founder identity came from the existing Azure configuration. Environment protection/branch policies were compared before and after and are unchanged. No credentials or Founder identifier were printed or saved in source or evidence; the temporary credential-holding process has exited.

See `restricted-access-result.json` and `required-tables.json`. The former's validation_executed:false describes the provisioning checkpoint; subsequent SQL execution is recorded separately below.

## Exact-candidate live-data observation

Candidate 19f56e244f18cc63774c1ce27c8215c5e6b0c746, local Release assemblies validated by AssemblyInformationalVersion against exact commit. Existing `ProductionReadOnlyCandidateObservation` was selected explicitly; provider credentials were excluded. No production application write-capable connection was supplied to this test. Build passed with zero warnings and zero errors. A preceding SDK analyzer MissingMethodException failed its build; a full rebuild with all analyzers enabled passed. No analyzer was suppressed.

Observation executed 6 cases: 4 passed, 2 failed. Learning returned 2 rows in 265 ms, Machine Learning Lifecycle returned 50 English rows in 972 ms, governed-cohort count returned 3,357 eligible records in 98 ms. These are live SQL service-path observations; they do not reproduce the authenticated production HTTP 503 or establish the deployed revision.

Two positive native cases failed with SQL -2 timeouts: failed SQL command durations 15,317 ms and 15,247 ms, case durations 20,304 ms and 20,159 ms. Candidate retrieval and declared-source-slot stage windows contain those failures. The initial query candidates are in `LegendConnectCurriculum.cs`: indexed candidate retrieval at 4532/4604/4632, exact-anchor lookup at 4711, candidate projection at 5700, source-slot candidate lookup at 5795, declaration join at 5811, node projection at 5847 and relation projection at 5875. The artifact does not bind a failed fingerprint to one exact query or include its execution plan, lock state, or row-volume evidence. Root cause remains unconfirmed; do not assume a missing index, permissions defect, or downstream reasoning defect.

The native-only negative isolation case passed: unknown meaning produced an explicit unsupported result, zero provider clients/calls and no fabricated answer. It took 16,833 ms, so its safety pass does not close the 500 ms unsupported-latency gate. Across the observation: 36 SELECT commands (34 succeeded, 2 failed), zero blocked commands, zero SaveChanges attempts, zero provider clients and zero provider HTTP calls. The exported SqlFailureCount of 6 counts repeated observations of the same two failures; it is not six distinct failed SQL commands.

The outer FailedPhase/FailureCode misleadingly names the last visited isolation case when the final aggregate assertion fails. CaseResults and failed command records establish that the two positive cases timed out while isolation passed; retain this artifact limitation explicitly. No held-out prompts, expected answers, thresholds or exclusions were changed to obtain a pass.

See `candidate-observation.json` and `stage-timing-report.json`. Stage timings are one local candidate/live-data observation, not representative cold/warm distributions or browser-completed response latency. Authenticated Founder HTTP entry, translation/provider-enabled acceptance, controlled concurrency, packaged/deployed source identity, and postdeployment reproductions remain unverified. Release authority remains unchanged and no deployment was attempted.


## Final local regression and release decision

Tested code commit: `19f56e244f18cc63774c1ce27c8215c5e6b0c746`. Full suite completed in 5m31s: 2,248 results, 2,235 runner passes, 9 failures, 4 skipped records. All failures are the unchanged positive `LegendFounderAiHeldOutOperationMatrixTests` gates; all three corrected negative-result contracts and the new permission checks passed. Runner passes must not be equated with live execution: the opt-in inventory includes 24 environmental early-return passes (23 SQL, one provider), separately from four skips. The dedicated contained-principal SQL observation above executed separately and is not included in those full-suite counts. TRX: `/private/tmp/legend-repair-results/access-final-combined.trx`.

The final capability gate remains failed. SQL access/configuration is complete; query latency/root-cause correlation, positive native capability, authenticated production HTTP reproduction, representative latency distributions, and deployment/artifact/revision checks remain open. No deployment, push, PR merge, release-gate override, production data write or existing-user permission change was performed. The evidence-only follow-up commit does not change the tested application/test source; recorded candidate identity remains the actual tested code commit.

Original Mac production remains `262428f38c8e1dd0ee72a362983b277f493fd362`. Untracked Android instructions compare byte-for-byte with their backup. Signed Android AAB remains at the user's required path with SHA256 `cf8edaf824ebc3f5f4680e55a031e45f7f2e221333f27dfaa141729afe9b821d`. Build isolation was partial: several projects hardcode shared `/tmp/masterapp` generated output paths, and these were rebuilt serially; no claim is made that all generated .NET outputs were untouched.


## Continued integration after restricted-access verification

The exact live SQL observation remains candidate `19f56e24`; its timings do not establish performance of later source changes. No later candidate has been published or deployed. Automatic approval review rejected the public GitHub branch push; explicit publication approval is pending. The restricted credential is retained in the existing protected validation environment. No credential rotation, alternate export, workflow duplication, or production environment substitution was used to bypass this block.

Read-only SQL system metadata independently identified expensive indexed semantic lookup compilation and repeated legacy candidate/event reads. Static query tags now identify seven existing curriculum query authorities without recording input or parameter values. Indexed semantic lookup groups hashes with equal request multiplicity into one query branch while retaining per-hash matching and the complete-span check. Populated correctness cases and relational translation tests cover this transformation; Azure latency improvement is unverified.

Legacy reconciliation now performs two dependent reads for the selected batch, retaining interrupted-retirement recovery, exact source hash/language isolation and late-arrival handling. SQLite tests cover batches of one and twenty-five. Candidate readiness projects only the four columns used by its existing calculation, preserving all rows and counting semantics. No index, schema, capacity, timeout, governance or deployment changes were introduced.

The existing independently authored numeric, named-value and batch teaching fixtures now provide a governed controlled prerequisite. Frozen evaluation questions and expected answers were not used as teaching. This allowed limited anchor recognition in arithmetic and memory but did not complete their meaning contracts. All nine capability gates still failed at `909acc2c`: seven unknown-component outcomes, arithmetic and the first memory turn unsupported-transition outcomes. These are not nine independently exercised reasoning engines. Three original positive controls additionally test the combined curriculum to distinguish interaction defects from missing broader language evidence.

Candidate `909acc2c` full regression: 2275 total, 2261 runner passes, ten failures, four skipped, 343 seconds. Nine failures were capability gates; one obsolete source-text assertion was independently corrected in `892d2193`, without changing behavior requirements. Its focused rerun and the query regressions passed 13/13. Full solution build at `892d2193` passed with zero warnings/errors. A preceding focused invocation failed only repository discovery because the external test output directory requires the existing GITHUB_WORKSPACE setting; that invocation is retained separately. New combined-corpus controls are integrated at `1a4e7333`; their execution is recorded separately below.

### Combined curriculum and remaining admission boundary

At `1a4e733379d2af41007fcfec221216c8dbd2e570`, the full solution built with zero warnings/errors. All three original positive controls passed against the combined admitted foundation (8 seconds): numeric recombination with provenance, exact named-value reply and conversation recall, and homogeneous batch allocation with the complete certificate assertions. These are controlled local executions, not live Founder HTTP checks.

Independent source review found no general foundation corpus to enable and no demonstrated generic binding defect after those controls passed. The existing `SubmitFounderCurriculumManifestAsync` authority (`LegendConnectOperations.cs:5046`) requires verified Founder identity, enabled language and explicit semantic families using `LegendConnectContracts.cs:324–384`. Broader prose needs legitimate independent declarations: quantity/computation composition, priority and unique-site constraints, grounded fact-preserving realization, deduction premises, competing causal hypotheses, Haitian Creole meanings, scoped data-insufficiency intent, prose memory roles, and owned-record read intent. Existing deduction and epistemic fixtures are bounded controls, not evidence of arbitrary-language competence. Filling those gaps by teaching frozen questions, routing on their words or silently invoking an external provider is prohibited.

This evidence distinguishes a missing admitted language contract from a broken numeric, memory or batch engine. The first demonstrated failure remains meaning selection, so downstream general reasoning is not independently proven defective or successful. Required production HTTP reproduction, live latency distributions, held-out capability acceptance and deployed revision verification remain open.

Final combined regression at `1a4e733379d2af41007fcfec221216c8dbd2e570`: **2278 total, 2265 runner passes, nine failures, four skipped, 339 seconds**. All failures are the unchanged positive capability gates listed in verification-summary.json. Twenty-four runner passes are environment-gated cases without live execution credit (sixteen explicit resource messages and eight silent early-return paths); the remaining 2241 are local passing executions. No source assertion failures remain. Release gate remains blocked. Original Mac checkout remains production `262428f3` with only the preserved untracked Android AGENTS.md; signed AAB SHA256 remains `cf8edaf824ebc3f5f4680e55a031e45f7f2e221333f27dfaa141729afe9b821d`.

## Authorized publication and GitHub live observation

User explicitly approved public branch publication. Exact commit `16388fe014310388d73ba16f684b010c8eb8ad88` was pushed to `repair/legend-integrated-20260910` in MYLEGND/masterapp and verified through `git ls-remote`. Existing workflow341323858 was dispatched with that exact candidate and observation scope. Run34558337071 records matching candidate/workflow SHAs. No merge or deployment occurred. The prior publication blocker is resolved.

Configuration delivery, restore and build succeeded. GitHub regression reproduced 2278 total, 2265 runner passes, nine frozen capability failures and four skips (503seconds). The protected observation used its existing SELECT-only credentials and recorded verified permissions, 39SELECT commands, zero blocked commands, zero SaveChanges attempts and zero provider clients/calls. Six cases executed: four passed and two SQL-2 timeouts. The final workflow and evidence gates correctly failed; none of this is deployment approval or accepted complete production proof.

Both failures now identify `AnalyzeDeclaredSourceSlotsAsync`, operation `source_slot_declarations`, fingerprint e396919415f19101b1d0a343d0dd720c07b406e893eeaf0041fe61d2c38c1e82, at15070.48/15030.10ms. Their complete cases took18.229/21.615seconds. Prior indexed lookup stages completed but remain slow at1.627/5.080seconds. Negative native-only isolation passed its expected unsupported outcome at15.356seconds, failing the500ms performance target. Learning/lifecycle/cohort case durations were184.5/1175.5/36.0ms. No representative latency distribution or authenticatedHTTP proof is established.

Fresh read-only QueryStore metadata associates source-slot structural query77453/plan43180 with two executions ending03:36:44Z, max15001ms and364.6ms averageCPU. Its nested semi-join performs lexical occurrence and lexeme lookups after transition/example/family scans. QueryStore did not retain the diagnostic comment; structural correspondence and execution timing, not a retained tag, link this plan to the observed query. A candidate set-based admission transformation is under review; no speedup is yet verified. Only system metadata was inspected through existing administrator access; business-data validation continued exclusively through the restricted workflow identity.

Source-slot repair integrated at `d87725e2`: query admission now joins the UNION DISTINCT of exact anchor-example IDs and lexical-example IDs. All eligibility predicates, ordering, diagnostic tag and one global129row sentinel remain. Independent specialist review plus lead inspection verified equivalent membership, including shared-unit siblings. Full solution build passed with zero warnings/errors;25focused tests passed in13seconds (21relational query cases, three combined native controls, one existing source-scope assertion). Live performance is pending a subsequent exact-candidate workflow run; frozen capability gates remain unchanged.

## Governed learning continuity and research boundary repairs

At `8e365466`, the regression reproduced two active-lease resubmission failures; the two withdrawn-evidence negatives passed. Existing deduplication omitted CanonicalValidationProcessing and CurriculumAdmissionProcessing even when the unchanged teaching family and original canonical evidence remained valid. Commit `37991900` includes those two states while preserving exact packet comparison, current evidence revalidation and the global retained-proposal bound. No approval, promotion or retry gate changed.

The combined candidate also corrects false success for native-only blocked research and prevents established owned-record/conversation authority from being erased by research wording. Existing public-research routes remain under the same authority; unavailable classification does not become public-search authorization. Full solution build has zero warnings/errors;103focused tests passed in6seconds, including the four lease cases and positive/negative research controls. The recording critic and local fixture establish lifecycle behavior, not production learning. Full regression results are recorded separately in adaptation-verification.json.

Read-only Azure appsettings inspection observed an OpenAI key setting, with no key value exported. Internet research uses the existing OpenAIResponsesWebSearch adapter and must remain labelled external assistance. Acquisition flags were not explicitly configured in Azure appsettings; repository bootstrap defaults are disabled, but persisted runtime policy takes precedence, so these observations DO NOT establish effective acquisition state. No production setting or data was changed.

Final combined regression at `37991900`:2313total,2300runner passes,nine unchanged positive capability failures,four skips,329seconds. The existing24environment-gated runner passes do not establish live execution;2276other passing cases are local verification. No new regression failures. Release remains blocked by native capability, live performance and authenticated request-path evidence gaps.

## Final composition and live resource verification

Final tested source `5798e251`: zero-warning build; full suite 2363 total, 2350 runner passes, nine unchanged positive capability failures, four skipped (333 seconds). Twenty-four existing gated passes have no live execution credit. The independent admitted two-step arithmetic path now passes with current-evidence and foreign-pair rejection and real SQLite coverage. Malformed research claim shapes reproduced seven failures before repair; all 55 affected cases pass afterward.

Both resource facts passed separately on this exact candidate using existing credentials only in process memory. Research still returns InsufficientEvidence: one raw/admitted claim, zero bound claims. This establishes policy/connectivity, not an answer. SQL candidate validation, authenticated Founder HTTP, representative latency and release remain open. Detailed current records are final-verification.json, native-failure-audit.json, resource-verification.json and final-timing-report.json. Production and the original Mac checkout remain unchanged.
