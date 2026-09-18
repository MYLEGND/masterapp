# Cloudflare runtime — candidate, not deployed

Checked 2026-09-18. Every production registry entry is disabled and unqualified. Authenticated read-only model-schema retrieval succeeded for all five candidates through pinned Wrangler 4.135.0 using the existing account login. No paid inference, provisioning, live performance measurement or model enablement was performed. Tests inject simulated provider replies; their success is not a Cloudflare canary or an intelligence evaluation.

The user subsequently approved a **$10 total qualification ceiling and $30/month production ceiling, including fixed/platform and all other charges**. This resolves the missing numeric decision; account access and enforceable total-cost allocation remain prerequisites for live operation. Do not ask again whether paid service is acceptable.

## File map and integration

| Path under `Legend-Cloudflare` | Responsibility |
| --- | --- |
| `src/runtime/registry.mjs` | One versioned, operator-owned candidate registry, capability/context/cost routing, integer micro-USD estimates |
| `src/runtime/adapter.mjs` | Workers AI binding request adapter and strict response normalization; no external provider URLs |
| `src/runtime/reliability.mjs` | Deadline/cancellation helpers and isolate-local circuit health hints |
| `src/runtime/orchestrator.mjs` | One bounded model/tool loop and progress/final SSE stream |
| `tests/runtime/runtime.test.mjs` | Executable simulations of budgets, cancellation, routing, tools, failures and streaming |
| `tests/runtime/fixtures/account-schema-snapshot.json` | Read-only authenticated schema evidence, exact raw schema hashes and relevant JSON pointers; no credentials |
| `tests/runtime/schema.test.mjs` | Adapter field compatibility checks against the captured schema projection |
| `tests/runtime/fixtures/held-out.v1.json` | Twelve prepared, unrun cases: four reasoning, four multi-turn and four code diagnosis |
| `tests/runtime/qualification.mjs` | Offline receipt scoring and cost/latency summary; no network calls or qualification promotion |
| `tests/runtime/qualification.test.mjs` | Simulation-only evaluator integrity checks |
| `tests/runtime/qualification-mode.test.mjs` | Qualification admission, scope, expiry, budget and production-isolation simulations |

Run `node --test Legend-Cloudflare/tests/runtime/*.test.mjs` from the repository root. No npm dependencies, local model, .NET build or Cloudflare credentials are needed.

The lead-owned Worker authenticates once with Security's `createSecuritySession(request, env)` and calls `orchestrate({envelope, context, env, signal, budget, toolBroker, onEvent})`. It must call the session's `close` in a finally block. Security owns replay, scope authentication, atomic account/user/tenant/request budgets and concurrency. Azure remains the canonical conversation and business-data store. Runtime keeps only request-local messages and an isolate-local health hint; it stores no conversations or learned claims.

The version is `legend-cloudflare.v1`. The signed envelope carries `requestId`, millisecond `issuedAt/expiresAt`, `scope` (account/tenant/user/session/conversation IDs, roles, authorization version), `task` (kind, messages, tool schemas, required capabilities), `limits` (deadline, completion tokens, iterations, model calls, tool calls, micro-USD) and `stream`. Operator policy may only reduce caller limits. This implementation supports text message content; vision ingestion and generated images are not enabled.

The existing `AgentPortal/Services/LegendFounderAiConversationService.cs` has its own provider/tool loop. Cloudflare mode must bypass that loop and delegate the full request once. Azure's existing tool authority must be reached through the fresh-authorized broker. Enabling this runtime under an existing Azure provider round would create a second loop and is not an accepted integration.

## Security ports

`budget.reserve(context, {requestId,reservationId,maxCostMicrousd,deadlineUnixMs})` atomically returns `{reservationId,reservedMicrousd}`. `budget.settle(context,{reservationId,actualCostMicrousd,usageKnown})` returns `{chargedMicrousd,usageKnown}`. Atomic reservation completes before model dispatch; cancellation during reservation causes a zero-use settlement without dispatch. Unknown usage debits the whole reservation and retains the active lease until the request deadline. An accounting failure cannot be converted to success.

`toolBroker.execute({context,call:{id,name,arguments},idempotencyKey,signal})` must reauthorize against Azure at execution and enforce approvals, scope, tool-specific schema, execution budgets and idempotency. It returns `{output,usage:{costMicrousd,costEvidence}}`; settled failures attach the same `usage` to their error. The broker enforces cancellation internally and settles before returning so runtime includes unknown execution debits. Runtime validates every tool name and call ID in a batch before executing its first call. It executes sequentially, never accepts a callback URL from model output, and never executes shell/code in a Worker or Azure process. Runtime limits individual tool output to 32 KiB and propagates approved receipts. Missing tool execution service fails closed. Reservation and idempotency identifiers are SHA-256 hashes of the request/operation identity.

All model attempts reserve the selected model's entire input context capacity plus the configured output cap at uncached rates. This is conservative admission control, not an expected bill. Actual reported tokens settle the charge; missing/malformed usage terminates the request and keeps the reservation. Reasoning consumes the provider completion cap where supported. Billing semantics and cap enforcement require per-model live qualification before enablement.

There are no automatic inference or tool retries (retry limit zero), no pending local queue, and no fallback endpoint. New rounds retain the same scoped request transcript. Production model choice requires operator-enabled state, an unexpired passing account canary and held-out qualification, compatible capability/context, budget and circuit health. The separately deployed qualification mode below permits initial evaluation without inventing those passing flags. Circuit hints are per isolate; durable budgets/concurrency are the global guard. Production routing is presently role-first then price; measured latency and quality optimization remains blocked on live data.

Cancellation stops awaiting further model output and prevents later tools. The Workers AI binding does not provide a verified backend cancellation guarantee here; the full debit and concurrency lease remain for unknown work. Progress/final SSE uses backpressure and emits exactly one terminal result. Provider tokens are buffered, output shapes are validated, and reasoning items are excluded. This is event streaming, not token streaming or a semantic truth verifier. No disconnected stream is replayed automatically.

## Official catalog snapshot and candidate economics

All five exact IDs appear as Cloudflare-hosted in their linked official catalog pages. Gateway availability alone is not used as hosting evidence. Authenticated schemas are available; account-specific inference admission, region/residency, numerical performance, actual generation wire behavior and service terms remain unqualified. Prices are USD per million uncached input/output tokens; prompt-cache discounts are deliberately excluded from reserves.

| Exact engine | Candidate role | Context | Input / output | Example: 8,000 input + 2,000 output |
| --- | --- | ---: | ---: | ---: |
| [`@cf/qwen/qwen3-30b-a3b-fp8`](https://developers.cloudflare.com/workers-ai/models/qwen3-30b-a3b-fp8/) | Efficient | 32,768 | $0.0509 / $0.335 | $0.0010772 |
| [`@cf/openai/gpt-oss-120b`](https://developers.cloudflare.com/workers-ai/models/gpt-oss-120b/) | General synthesis | 128,000 | $0.35 / $0.75 | $0.00430 |
| [`@cf/zai-org/glm-5.3-flash`](https://developers.cloudflare.com/workers-ai/models/glm-5.3-flash/) | Coding; later vision | 1,310,720 | $0.15 / $0.50 | $0.00220 |
| [`@cf/zai-org/glm-5.3`](https://developers.cloudflare.com/workers-ai/models/glm-5.3/) | Architecture | 1,310,720 | $1.40 / $4.40 | $0.02000 |
| [`@cf/deepseek-ai/deepseek-v4-pro-0813`](https://developers.cloudflare.com/workers-ai/models/deepseek-v4-pro-0813/) | Difficult reasoning | 1,048,576 | $1.32 / $3.96 | $0.01848 |

Each catalog page documents tools, reasoning and streaming; GLM Flash additionally documents vision. Authenticated GLM and DeepSeek schemas expose `max_completion_tokens`, `reasoning_effort`, `store`, tool schemas and structured output options. Qwen/GPT OSS message input declares `max_tokens`; GPT OSS also declares a Responses input variant with an opaque JSON output schema. Qwen declares both chat and text-completion outputs, and the adapter handles `choices[].text` for the latter. Nonterminal Responses states fail closed. This adapter does not claim schema-constrained structured responses or vision are qualified. Exact account binding output and enforcement of billing caps must still be captured during each canary.

Schema evidence was obtained with `wrangler@4.135.0 ai models schema MODEL`, a read-only [Get Model Schema API](https://developers.cloudflare.com/api/resources/ai/subresources/models/subresources/schema/methods/get/) call. Raw CLI JSON SHA-256 values are `051be5c446a13239ca48b4078bc6a993356003fec256ab26e81a11e6205ae4e0` (Qwen), `73c13bd8e6e62cc66721c0a7bed4b5281ebedec5fd8d72f45b37800c2a0c123b` (GPT OSS), and `9ad8d83d3c9729659907a38b9f8c07f814f245a72b20d751c78a611224b4ae40` (the identical common schemas returned independently for GLM, GLM Flash and DeepSeek). The committed projection records retrieval time, hashed account identity and source JSON pointers. It is a compatibility snapshot, not a complete JSON Schema validator or proof that inference is enabled.

The [Workers AI rate limits](https://developers.cloudflare.com/workers-ai/platform/limits/) checked September 18 list default text generation at 300 requests/minute, with paid-only models at 20 requests/minute/account/model under standard billing or 50 using prepaid Gateway credits. The latter billing path is not configured. No dedicated GPU capacity is provisioned.

At an illustrative 10,000 successful requests/day, 30 days, one 8k/2k call each, model-only cost would be approximately $323.16 Qwen, $1,290 GPT OSS, $660 GLM Flash, $6,000 GLM or $5,544 DeepSeek. This example exceeds the approved ceiling and must not be provisioned. Two calls per successful task doubles these examples; failed attempts still cost money. These are estimates, not workload measurements. A complete spend ceiling must additionally include Worker CPU/requests, Durable Object requests/storage, embeddings, source derivatives, tool execution/builds, artifact storage/retention, network and background jobs.

The [Workers Standard price](https://developers.cloudflare.com/workers/platform/pricing/) is $5/month including 10 million requests and 30 million CPU milliseconds; overages are $0.30/million requests and $0.02/million CPU milliseconds. [Durable Objects paid pricing](https://developers.cloudflare.com/durable-objects/platform/pricing/) includes 1 million requests and 400,000 GB-seconds/month, with overages of $0.15/million requests and $12.50/million GB-seconds; storage has separate allowances. These are account-shared allocations, so existing account consumption must be checked.

A conservative production allocation is $5 fixed + $5 platform/other reserve + at most $20 for all metered inference/tools combined, pending account verification. At the example token volume, $20 alone is about 18,566 Qwen calls or 4,651 GPT OSS calls, before retries/tools. This allocation is a proposal within the approved ceiling, not a claim of a platform hard cap. If qualification requires a new $5 paid subscription, deduct it from the $10 qualification total before running experiments; never treat the whole $10 as inference credit. Runtime's atomic ledger covers model/tool debits, not subscription fees or platform meters. Deployment must reserve those fees and enforce platform workload limits with billing reconciliation before claiming the total ceiling is enforced. Missing account evidence keeps engines disabled.

## Licenses and data handling

Upstream license evidence: [Qwen3 Apache 2.0](https://huggingface.co/Qwen/Qwen3-30B-A3B), [GPT OSS license](https://huggingface.co/openai/gpt-oss-120b/blob/main/LICENSE), [GLM Flash MIT](https://huggingface.co/zai-org/GLM-5.3-Flash/blob/main/LICENSE), [GLM 5.3 custom license](https://huggingface.co/zai-org/GLM-5.3/blob/main/LICENSE), and [DeepSeek V4 Pro MIT](https://huggingface.co/deepseek-ai/DeepSeek-V4-Pro/blame/main/LICENSE). Exact hosted revisions and applicable terms must be recorded with each canary; a family license is not proof of the precise hosted build. No weights are downloaded or redistributed by this change.

Cloudflare's [data usage policy](https://developers.cloudflare.com/workers-ai/platform/data-usage/) says customer content is not used to train models or improve services without explicit consent, and storage services can retain content when separately used. This code uses no response cache, no Gateway logs, no prompt logging and no canonical storage. Application logging, authorized retrieval/redaction, deployment telemetry retention and region requirements must be verified separately; no residency guarantee is inferred from the word hosted.

## Qualification and release gates

`LEGEND_RUNTIME_MODE` defaults to `production`. Production ignores qualification fields in requests and the qualification JSON binding; it cannot route a disabled engine. To perform the first real evaluation, the lead must deploy a **separate qualification Worker with a dedicated Azure service-signing key**, set `LEGEND_RUNTIME_MODE=qualification` and `LEGEND_DEPLOYMENT_ENVIRONMENT=qualification`, and supply operator-owned `LEGEND_QUALIFICATION_POLICY_JSON`:

| Required policy field | Enforcement |
| --- | --- |
| `version` | Exactly `legend-qualification.v1` |
| `accountId` | Matches deployment `LEGEND_ACCOUNT_ID` and authenticated scope |
| `modelId` | Exactly one existing Cloudflare-hosted registry candidate; requests cannot choose it |
| `tenantId`, `allowedUserIds` | Dedicated test tenant and nonempty allowlist of at most 32 authenticated users |
| `requiredRole` | Exactly `LegendQualification`, derived by the Azure service and verified in signed context |
| `serviceKeyId` | Matches the dedicated key ID that authenticated this request |
| `suiteSha256` | Operator-selected 64-character lowercase SHA-256; request overrides are ignored |
| `expiresAt` | Millisecond expiry within the next 24 hours; request deadline may not outlive it |
| `lifetimeCostMicrousd` | Positive authorized **metered remainder** after reserving subscription/platform/other charges |

Qualification additionally requires `LEGEND_BUDGET_POLICY_JSON.period=lifetime` and a positive account cap no greater than `lifetimeCostMicrousd`. The code enforces a generic safe numeric maximum; the lead sets the much smaller user-approved ceiling and all-charge reserves. All qualification candidates, suite revisions and key rotations must share the **same account-named lifetime Durable Object ledger and namespace**. Do not create a fresh namespace or rename its account identity to reset spend. That ledger and ordinary authenticated replay/budget reservations remain mandatory before each provider attempt.

Qualification runs the existing `orchestrate` and Workers AI adapter, locks routing to the operator-selected candidate, returns `executionMode:qualification` plus `qualificationSuiteSha256`, and never mutates registry `enabled` or passing qualification flags. It requires the same hosted identity, context/output bounds, cost reservation, cancellation and circuit checks. Initial qualification rejects nonempty tool catalogs. It therefore does **not** qualify repository editing, build/test sandbox execution, approvals, canonical memory persistence, or the four-language acceptance matrix. Those remain separate release gates, and the twelve-case suite is not full application acceptance.

No qualification Worker has been deployed or called by this specialist. Account billing/remaining all-charge allowance is still a lead release prerequisite. These configuration controls do not make a schema GET or simulated fixture a passing live evaluation.

Before an operator changes any `enabled:false` registry entry, independently capture authenticated account access, exact executed model identity, accepted input/output shape, token/reasoning accounting, configured limits, licensing/processing approval, and a live canary. Store a dated, expiring qualification tied to the deployed registry revision. Never let a request or model mint qualification flags. Fixture qualifications exist only in tests.

Predeclared acceptance: at least 90% held-out task completion; zero fabricated completed actions or citations; zero cross-scope disclosure or unauthorized side effects; every enabled engine passes live account and application canaries with local inference unreachable. Include arithmetic, reasoning, code repair, repository diagnosis, research, tools, durable multi-turn memory and English/Haitian Creole/French/Spanish review. Record failures, token use, total cost per successful task and latency distributions. Inspect cancellation, output truncation, quota exhaustion, request/tenant/account budgets, concurrency, disconnects, idempotency and provider outages. Simulated fixtures cannot count toward these scores. Relative Astra quality is unverified.

The prepared twelve-case fixture freezes prompts before any live run. Send only `qualificationUserTurns(caseId)` under normal production instructions, preserve each actual assistant reply, and never include fixture IDs, answers or evaluation rubrics in provider input. The offline summary requires the exact suite SHA-256, rejects duplicate/substituted cases and counts missing cases as failures. Objective answers use strict scoring; diagnostic judgments require a named reviewer and notes against the fixed rubric. The summary labels simulated versus reported-live receipts and always returns `releaseQualified:false`: it does not authenticate receipts or replace independent release review. In-context multi-turn evaluation does not prove persisted Azure memory. No prompt tuning or live model evaluation was performed against these cases.

Outstanding release blockers include account billing/allowance verification and all-charge budget allocation, live engine qualifications, fresh-authorized Azure tool callback integration, approved cloud execution environment, full application loop cutover, derivative knowledge integration and final cross-platform acceptance. Nothing in this module authorizes deletion of the Mac runtime. Local cleanup requires cloud acceptance first and an exact resource manifest owned by the lead.
