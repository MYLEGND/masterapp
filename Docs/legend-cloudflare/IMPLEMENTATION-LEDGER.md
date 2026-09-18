# Cloudflare migration — authoritative integration ledger

Baseline: approved branch 5c6780bc3cb76e77667584f1fc53914d6afce128. Direct release 35391857902 is still running independently. No local model deletion or paid provisioning is authorized by this ledger. User authorizes paid service in principle; numeric budget and authenticated account access must be established before paid calls/provisioning.

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

Current transport accepts FounderMac/Mlx or AzureVm/Vllm receipts and uses a dedicated LegendLocalFoundation HTTP client. Conversation tooling lives in existing Founder conversation/tool authorities. No Cloudflare management credentials found in current environment; available plugin search returned none. Cloudflare official catalog/pricing documents list the proposed @cf engines; account access not verified. Previous specialist quota error exists; requested agent availability must be confirmed, not assumed.
