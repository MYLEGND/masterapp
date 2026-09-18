# Cloudflare migration — authoritative integration ledger

Baseline: approved branch 5c6780bc3cb76e77667584f1fc53914d6afce128. Direct release 35391857902 completed successfully, including all-target verification, on 2026-09-18. User approved $10 total qualification and $30/month production including all platform charges. Cloudflare OAuth account identity verified as 84bf137174ac3966d6d24e9806f0c392; encrypted credentials remain outside Git. User confirmed the $5/month Workers subscription; current unbilled platform usage remains unverified. The isolated qualification Worker has executed real paid model calls; production Cloudflare activation and Mac cleanup have not occurred. Application budgets are not a provider invoice hard cap.

## Owners and isolation

- Lead: /private/tmp/masterapp-cloudflare-20260918. Shared contracts, .NET inference transport/DI/task contracts, conversation integration, all manifests/lockfiles/workflows/migrations, client response attribution, ledger, release and cleanup.
- Runtime: /private/tmp/masterapp-cloudflare-runtime-20260918. Exclusively Legend-Cloudflare/src/runtime/*, Legend-Cloudflare/tests/runtime/*, Docs/legend-cloudflare/RUNTIME.md. Own Worker model adapter/registry/loop/deadlines/usage. Do not edit shared .NET interfaces or root manifests.
- Knowledge: /private/tmp/masterapp-cloudflare-knowledge-20260918. First read-only trace existing repository/knowledge authorities and propose exact ownership before edits. No second canonical store, no broad repository upload. Own source-retrieval privacy tests after allocation.
- Security: /private/tmp/masterapp-cloudflare-security-20260918. Exclusively Legend-Cloudflare/src/security/*, Legend-Cloudflare/tests/security/*, Docs/legend-cloudflare/SECURITY.md. Own signed Azure-context validation, replay/budget enforcement contract proposal and independent adversarial review. No root manifest or production writes.

## Initial interface constraints v1

Existing ILegendConnectModelInferenceTransport, LegendModelTaskRequest and LegendModelEvaluationGenerationResult remain .NET execution authority. Azure owns identity, permissions, canonical state and tool-side-effect authorization. Cloudflare is a separately attributed hosted inference dependency, never native/offline/Mac-controlled inference.

Worker endpoint POST /v1/legend/respond accepts a versioned request with requestId, issuedAt/expiresAt (<=120 seconds), server-derived actor/tenant/session/conversation scope, model task, allowed tool schemas, deadlines and caller budget ceiling. Exact body is authenticated by Azure service signature; browser/mobile never receive signing secrets. Tool callback URL is server-configured only; callbacks require fresh authorization. No untrusted context may choose endpoints, model IDs, budget or privileged tool permissions.

Worker security exports authenticateRequest(request, env) -> { envelope, context }. Fail closed if signature/scope/time invalid. Replay and budget reserve/commit must be atomically persisted before inference/tool side effects, never process-local claims. Runtime asks Security for exact reserve API before coding against it. Contracts must return explicit typed failure, model/provider identity, token usage/cost evidence, and tool results; no synthetic success or hidden fallback.

Model registry is one versioned source; listed models start unqualified/disabled until account canaries and held-out tests pass. No external model vendors via AI Gateway masquerading as Cloudflare hosting. No local-model fallback.

One active orchestration path: Cloudflare mode must delegate the request loop rather than stack a second loop on Azure's existing loop. Existing Azure tool dispatcher is reused through a reauthorized broker, not copied. Cutover/removal remains blocked until this is demonstrated.

## Acceptance before cutover

Zero cross-user/tenant leakage, zero unauthorized side effects, zero local inference calls. Real account canary for each enabled model. >=90% held-out task completion on a predefined representative set, no fabricated completed actions/citations, scoped retrieval evidence, four-language rubric review, bounded cancellation/timeout/retry, correct budget exhaustion. Simulator/mock results cannot count as live Cloudflare proof.

## Current verified findings

The original local transport remains intact. Cloudflare mode uses the same model-execution interface and a separate signed HTTP client; independent/native-only policies reject it. Cloudflare OAuth account access and five candidate catalog/schema entries were verified. Four agents total were active in this phase, with the ownership boundaries above. Credentials stay outside Git.

## Integrated evidence

Runtime integrated at 31122cf6 and pricing documentation at 4b8acd20. Lead reran 20 runtime simulations: 20 passed, zero failed. These do not establish live model quality or Cloudflare storage behavior. Worker entry point delegates to one authenticated cloud loop; Azure integration and tool callback remain incomplete.


## Integrated update — 2026-09-18

Implemented (not production-activated): signed Azure transport; canonical persisted conversation delegation; distinct signed tool callback; exact-action SQL approval/one-dispatch ledger; original-schema cloud read exposure with the same privacy predicate at execution; conservative Durable Object spending/replay/deadline enforcement; immutable source retrieval; typed provider error/usage receipts. Callback array results are preserved instead of throwing. The existing scoped AgencyCommandService is passed into the callback authority. Optional cloud tool loops are bounded to three model calls/four tool calls under the existing request ceiling. Mandatory governed reads and conversational writes remain closed until their end-to-end evidence and approval lifecycle are complete.

The new action table migration was regenerated through EF together with its designer and model snapshot. The unapplied hand-written migration is superseded, not retained as a second active migration. No production database migration has run.

Live evidence is retained under `evidence/2026-09-18`. GPT-OSS-120B ran via the real .NET transport against the isolated Cloudflare Worker with local and other model clients inaccessible in the harness. This is NOT yet the full authenticated Founder conversation pipeline. At 1024 output tokens, the first run retained a truncation failure; an additional diagnostic retained its real charge/error. At 4096, 20/20 responses completed across 12 scenarios. Frozen scoring gives five objective passes, one strict-JSON failure, six independent-human-review-required cases. No release qualification was granted. In-context history does not prove persisted memory, and fictional code diagnosis does not prove MasterApp source understanding.

Known provider charges: $0.011414. Earlier ambiguous failure retains $0.045568 conservative reservation; total metered debit $0.056982. Remaining $3 model/tool allotment: $2.943018. After allocating the confirmed $5 platform fee, $4.943018 remains from $10 BEFORE unverified platform charges; $2 of this remains reserved for those charges and sandbox work. Do not describe this as a verified invoice balance. No budget increased. Qualification costs must also count in the same month's production limit.

Simulation/local evidence: prior 84 Worker tests, 111 durable approval/delegation/history tests, 118 transport/foundation/repository tests; these sets overlap and must not be summed. Post-array-fix focused run passed 78/78. The latest privacy/tool-loop integration passed 97/97 focused tests with no skips. EF has-pending-model-changes reports no changes since the generated migration. Local Durable Object workerd testing is not a cloud concurrency qualification. No cloud sandbox job has executed.

Release: existing direct release 35391857902 succeeded at 5c6780bc. Cloud qualification Worker version 2435c78b-115f-4f5a-be46-72947295879e is deployed independently; Azure production routing is unchanged. Mac serving/checkpoints are preserved. No migration changes have been pushed to the approved branch or deployed to Azure.

Remaining work/blockers:
- Automatic approval review rejected adding containers:write OAuth scope because it grants persistent billable resource management; specific scope approval is pending. Do not bypass it. Proposed sandbox reservation <=$0.50 fits inside the existing reserve, not a new budget.
- Cloud sandbox isolation requires a real seccomp/process-cleanup canary before private code is admitted; a local image build alone does not establish that.
- Generic exact-action review needs durable non-executable proposals and a NEW operation after approval. Never revive a terminal turn, extend an expired execution lease, or treat FounderCommandConfirmed/model flags as exact action consent. Existing diagnostic review/PR/SHA authorities remain available.
- Actual Azure-to-Cloudflare-to-Azure tool execution, persisted multi-turn Founder session, multilingual qualitative review, isolated editing/build/test, workload costs and final production canary remain unverified.
- Current billing totals are not visible through the existing OAuth token. The $5 plan confirmation alone does not verify all-in spending headroom or an invoice hard cap.
