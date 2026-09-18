# Cloudflare runtime — candidate, not deployed

Checked 2026-09-18. Every production registry entry is disabled and unqualified. No authenticated account inspection, paid inference, provisioning, live performance measurement or model enablement was performed. Tests inject simulated provider replies; their success is not a Cloudflare canary or an intelligence evaluation.

## File map and integration

| Path under `Legend-Cloudflare` | Responsibility |
| --- | --- |
| `src/runtime/registry.mjs` | One versioned, operator-owned candidate registry, capability/context/cost routing, integer micro-USD estimates |
| `src/runtime/adapter.mjs` | Workers AI binding request adapter and strict response normalization; no external provider URLs |
| `src/runtime/reliability.mjs` | Deadline/cancellation helpers and isolate-local circuit health hints |
| `src/runtime/orchestrator.mjs` | One bounded model/tool loop and progress/final SSE stream |
| `tests/runtime/runtime.test.mjs` | Executable simulations of budgets, cancellation, routing, tools, failures and streaming |

Run `node --test Legend-Cloudflare/tests/runtime/*.test.mjs` from the repository root. No npm dependencies, local model, .NET build or Cloudflare credentials are needed.

The lead-owned Worker authenticates once with Security's `createSecuritySession(request, env)` and calls `orchestrate({envelope, context, env, signal, budget, toolBroker, onEvent})`. It must call the session's `close` in a finally block. Security owns replay, scope authentication, atomic account/user/tenant/request budgets and concurrency. Azure remains the canonical conversation and business-data store. Runtime keeps only request-local messages and an isolate-local health hint; it stores no conversations or learned claims.

The version is `legend-cloudflare.v1`. The signed envelope carries `requestId`, millisecond `issuedAt/expiresAt`, `scope` (account/tenant/user/session/conversation IDs, roles, authorization version), `task` (kind, messages, tool schemas, required capabilities), `limits` (deadline, completion tokens, iterations, model calls, tool calls, micro-USD) and `stream`. Operator policy may only reduce caller limits. This implementation supports text message content; vision ingestion and generated images are not enabled.

The existing `AgentPortal/Services/LegendFounderAiConversationService.cs` has its own provider/tool loop. Cloudflare mode must bypass that loop and delegate the full request once. Azure's existing tool authority must be reached through the fresh-authorized broker. Enabling this runtime under an existing Azure provider round would create a second loop and is not an accepted integration.

## Security ports

`budget.reserve(context, {requestId,reservationId,maxCostMicrousd,deadlineUnixMs})` atomically returns `{reservationId,reservedMicrousd}`. `budget.settle(context,{reservationId,actualCostMicrousd,usageKnown})` returns `{chargedMicrousd,usageKnown}`. Atomic reservation completes before model dispatch; cancellation during reservation causes a zero-use settlement without dispatch. Unknown usage debits the whole reservation and retains the active lease until the request deadline. An accounting failure cannot be converted to success.

`toolBroker.execute({context,call:{id,name,arguments},idempotencyKey,signal})` must reauthorize against Azure at execution and enforce approvals, scope, tool-specific schema, execution budgets and idempotency. It returns `{output,usage:{costMicrousd,costEvidence}}`; settled failures attach the same `usage` to their error. The broker enforces cancellation internally and settles before returning so runtime includes unknown execution debits. Runtime validates every tool name and call ID in a batch before executing its first call. It executes sequentially, never accepts a callback URL from model output, and never executes shell/code in a Worker or Azure process. Runtime limits individual tool output to 32 KiB and propagates approved receipts. Missing tool execution service fails closed. Reservation and idempotency identifiers are SHA-256 hashes of the request/operation identity.

All model attempts reserve the selected model's entire input context capacity plus the configured output cap at uncached rates. This is conservative admission control, not an expected bill. Actual reported tokens settle the charge; missing/malformed usage terminates the request and keeps the reservation. Reasoning consumes the provider completion cap where supported. Billing semantics and cap enforcement require per-model live qualification before enablement.

There are no automatic inference or tool retries (retry limit zero), no pending local queue, and no fallback endpoint. New rounds retain the same scoped request transcript. Model choice requires operator-enabled state, an unexpired passing account canary and held-out qualification, compatible capability/context, budget and circuit health. Circuit hints are per isolate; durable budgets/concurrency are the global guard. Routing is presently role-first then price; measured latency and quality optimization remains blocked on live data.

Cancellation stops awaiting further model output and prevents later tools. The Workers AI binding does not provide a verified backend cancellation guarantee here; the full debit and concurrency lease remain for unknown work. Progress/final SSE uses backpressure and emits exactly one terminal result. Provider tokens are buffered, output shapes are validated, and reasoning items are excluded. This is event streaming, not token streaming or a semantic truth verifier. No disconnected stream is replayed automatically.

## Official catalog snapshot and candidate economics

All five exact IDs appear as Cloudflare-hosted in their linked official catalog pages. Gateway availability alone is not used as hosting evidence. Account-specific availability, region/residency, numerical performance, exact wire behavior and service terms remain unqualified. Prices are USD per million uncached input/output tokens; prompt-cache discounts are deliberately excluded from reserves.

| Exact engine | Candidate role | Context | Input / output | Example: 8,000 input + 2,000 output |
| --- | --- | ---: | ---: | ---: |
| [`@cf/qwen/qwen3-30b-a3b-fp8`](https://developers.cloudflare.com/workers-ai/models/qwen3-30b-a3b-fp8/) | Efficient | 32,768 | $0.0509 / $0.335 | $0.0010772 |
| [`@cf/openai/gpt-oss-120b`](https://developers.cloudflare.com/workers-ai/models/gpt-oss-120b/) | General synthesis | 128,000 | $0.35 / $0.75 | $0.00430 |
| [`@cf/zai-org/glm-5.3-flash`](https://developers.cloudflare.com/workers-ai/models/glm-5.3-flash/) | Coding; later vision | 1,310,720 | $0.15 / $0.50 | $0.00220 |
| [`@cf/zai-org/glm-5.3`](https://developers.cloudflare.com/workers-ai/models/glm-5.3/) | Architecture | 1,310,720 | $1.40 / $4.40 | $0.02000 |
| [`@cf/deepseek-ai/deepseek-v4-pro-0813`](https://developers.cloudflare.com/workers-ai/models/deepseek-v4-pro-0813/) | Difficult reasoning | 1,048,576 | $1.32 / $3.96 | $0.01848 |

Each catalog page documents tools, reasoning and streaming; GLM Flash additionally documents vision. GLM and DeepSeek schemas expose completion-token caps and structured output options. This adapter does not claim schema-constrained structured responses or vision are qualified. GPT OSS documentation has shown both chat and Responses conventions: normalization accepts both, but the exact account binding response must be captured during its canary.

The [Workers AI rate limits](https://developers.cloudflare.com/workers-ai/platform/limits/) checked September 18 list default text generation at 300 requests/minute, with paid-only models at 20 requests/minute/account/model under standard billing or 50 using prepaid Gateway credits. The latter billing path is not configured. No dedicated GPU capacity is provisioned.

At an illustrative 10,000 successful requests/day, 30 days, one 8k/2k call each, model-only cost would be approximately $323.16 Qwen, $1,290 GPT OSS, $660 GLM Flash, $6,000 GLM or $5,544 DeepSeek. Two calls per successful task doubles these examples; failed attempts still cost money. This is an estimate, not a budget approval or workload measurement. A complete spend ceiling must additionally include Worker CPU/requests, Durable Object requests/storage, embeddings, source derivatives, tool execution/builds, artifact storage/retention, network and background jobs. Those services have no workload allocation here. Security rejects missing configured limits; do not turn estimates into authorized ceilings.

## Licenses and data handling

Upstream license evidence: [Qwen3 Apache 2.0](https://huggingface.co/Qwen/Qwen3-30B-A3B), [GPT OSS license](https://huggingface.co/openai/gpt-oss-120b/blob/main/LICENSE), [GLM Flash MIT](https://huggingface.co/zai-org/GLM-5.3-Flash/blob/main/LICENSE), [GLM 5.3 custom license](https://huggingface.co/zai-org/GLM-5.3/blob/main/LICENSE), and [DeepSeek V4 Pro MIT](https://huggingface.co/deepseek-ai/DeepSeek-V4-Pro/blame/main/LICENSE). Exact hosted revisions and applicable terms must be recorded with each canary; a family license is not proof of the precise hosted build. No weights are downloaded or redistributed by this change.

Cloudflare's [data usage policy](https://developers.cloudflare.com/workers-ai/platform/data-usage/) says customer content is not used to train models or improve services without explicit consent, and storage services can retain content when separately used. This code uses no response cache, no Gateway logs, no prompt logging and no canonical storage. Application logging, authorized retrieval/redaction, deployment telemetry retention and region requirements must be verified separately; no residency guarantee is inferred from the word hosted.

## Qualification and release gates

Before an operator changes any `enabled:false` registry entry, independently capture authenticated account access, exact executed model identity, accepted input/output shape, token/reasoning accounting, configured limits, licensing/processing approval, and a live canary. Store a dated, expiring qualification tied to the deployed registry revision. Never let a request or model mint qualification flags. Fixture qualifications exist only in tests.

Predeclared acceptance: at least 90% held-out task completion; zero fabricated completed actions or citations; zero cross-scope disclosure or unauthorized side effects; every enabled engine passes live account and application canaries with local inference unreachable. Include arithmetic, reasoning, code repair, repository diagnosis, research, tools, durable multi-turn memory and English/Haitian Creole/French/Spanish review. Record failures, token use, total cost per successful task and latency distributions. Inspect cancellation, output truncation, quota exhaustion, request/tenant/account budgets, concurrency, disconnects, idempotency and provider outages. Simulated fixtures cannot count toward these scores. Relative Astra quality is unverified.

Outstanding release blockers include real account access and numeric budgets, live engine qualifications, fresh-authorized Azure tool callback integration, approved cloud execution environment, full application loop cutover, derivative knowledge integration and final cross-platform acceptance. Nothing in this module authorizes deletion of the Mac runtime. Local cleanup requires cloud acceptance first and an exact resource manifest owned by the lead.
