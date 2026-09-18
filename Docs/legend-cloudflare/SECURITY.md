# Cloudflare security boundary

Status: implemented, component-tested locally; neither live security qualification nor production cutover is claimed here. Azure remains the identity, object permission, canonical conversation and tool side-effect authority. Cloudflare stores only bounded replay/accounting metadata. It does not have a second role, approval or business-record database.

## File map

| File | Responsibility |
| --- | --- |
| `Legend-Cloudflare/src/security/authenticate.mjs` | Exact-byte Azure service signature, scope/time/limits validation, immutable authorized context |
| `crypto.mjs` | Web Crypto HMAC/SHA256, bounded body reading, canonical action serialization |
| `governance.mjs` | `LegendGovernance` Durable Object; atomic replay, request/user/tenant/account spend and concurrency |
| `session.mjs` | Authenticate, durably claim replay, then expose scoped budget/tool ports |
| `tool-broker.mjs` | Fixed Azure callback, fresh execution authorization receipt, exact action digest, execution cost reservation |
| `sandbox.mjs` | Explicit `cloud_sandbox_unverified` denial; no local/Azure script execution fallback |
| `errors.mjs` | Safe error codes and private/no-store failure responses |
| `tests/security/*.test.mjs` | Independent boundary, accounting, replay and tool adversarial tests |
| `tests/security/fixtures.mjs` | Synthetic identities/keys and deterministic transaction simulation only |
| `tests/security/workerd.integration.mjs` | Actual local workerd/SQLite atomicity and persisted restart replay, no cloud calls |
| `tests/security/application.integration.mjs` | Actual integrated Worker transport, failure, replay and private-stream checks |

## Inbound v1 wire contract

`POST /v1/legend/respond`, JSON only, no query string or content encoding, maximum 1 MiB. The shared envelope version is `legend-cloudflare.v1`:

- `requestId`; integer Unix-millisecond `issuedAt` and `expiresAt`, lifetime at most 120 seconds.
- `scope`: `accountId`, `tenantId`, `userId`, `sessionId`, `conversationId`, nonempty `roles`, `authorizationVersion`. Every field originates from Azure server identity/ownership resolution, never message text or a client-selected identity. Founder source access grants no customer-record access.
- `task`: `kind`, `messages`, `tools`, `requiredCapabilities`. Runtime owns model/task schema validation. Both Responses and chat function-tool catalog shapes are accepted.
- `limits`: `deadlineUnixMs` no later than expiry, `maxOutputTokens`, `maxIterations`, `maxModelCalls`, `maxToolCalls`, `maxCostMicrousd`.
- Boolean `stream`.

Signature headers are `X-Legend-Key-Id`, `X-Legend-Timestamp`, `X-Legend-Nonce` and `X-Legend-Signature`. Timestamp equals `issuedAt`; maximum future clock skew is 5 seconds. Nonce is 22–128 base64url/alphanumeric characters. Signature is lowercase hexadecimal HMAC-SHA256 over this UTF-8 sequence, separated with LF and **no trailing LF**:

```text
legend-service.v1
POST
/v1/legend/respond
<keyId>
<issuedAt decimal milliseconds>
<nonce>
<lowercase SHA256 hexadecimal of the exact request body bytes>
```

`LEGEND_SERVICE_KEYS_JSON` is a secret JSON object mapping key IDs to base64-encoded 32–128-byte keys, allowing bounded rotation. Browser/mobile/model input never receives these keys. `LEGEND_ACCOUNT_ID` is the fixed account authorization fence. Different deployment environments MUST use different service/callback keys and Durable Object namespaces: the v1 signature binds path, not hostname, so reusing keys would permit cross-environment replay. Changing a tenant, timestamp, path or even body whitespace breaks the signature. Authentication returns deeply frozen `{envelope, context}`; context includes signed scope and opaque body/context digests. No function logs prompts, keys or callback bodies.

The Worker calls `createSecuritySession(request, env)` **before** retrieval, models or tools. This claims both request ID and nonce durably. It returns `{envelope,context,budget,toolBroker,close}`. Ports accept the exact authenticated context object, rejecting caller context substitution. The Worker closes the session in `finally`; close prevents additional reservations and does not release uncertain execution leases.

## Atomic accounting and lifecycle

Bind `LEGEND_GOVERNANCE` to the exported `LegendGovernance` class with a SQLite Durable Object migration. The client always selects `legend-account:<LEGEND_ACCOUNT_ID>`. A single account object is necessary to transact across all four limits; independently sharded user/tenant objects cannot atomically enforce an account ceiling. All counter decisions occur inside `storage.transaction` using its transaction handle. No external I/O runs within a transaction.

Required operator-owned `LEGEND_BUDGET_POLICY_JSON` fields:

```json
{
  "period": "lifetime",
  "requestMicrousd": 0,
  "userMicrousd": 0,
  "tenantMicrousd": 0,
  "accountMicrousd": 0,
  "requestConcurrency": 0,
  "userConcurrency": 0,
  "tenantConcurrency": 0,
  "accountConcurrency": 0,
  "retentionMs": 86400000
}
```

The zero values above are deliberately disabled examples, not approved production defaults. `period` is `lifetime`, `calendar-month` (UTC), or `fixed` with an explicit `periodMs` of 60,000–2,678,400,000. Retention is explicitly 120,000–2,678,400,000 milliseconds. Concurrency is 0–10,000 per dimension. Spend is integer micro-USD: $1 = 1,000,000 micro-USD.

The user authorized **$10 total qualification** and **$30/month production, all charges included**. Qualification therefore requires `lifetime`, with no reset or automatic phase extension. Production requires `calendar-month`. The account inference/tool cap must be smaller than the approved total by explicitly allocated fixed/platform/storage/network headroom. These counters do not cap Cloudflare's invoice, subscription fee, unauthorized incoming traffic cost or unrelated account usage. Lead must reconcile provider usage and prevent enabling a phase whose remaining all-in allowance is insufficient.

Runtime port:

```js
await budget.reserve(context, {
  requestId, reservationId, maxCostMicrousd, deadlineUnixMs
}); // { reservationId, reservedMicrousd }
await budget.settle(context, {
  reservationId, actualCostMicrousd, usageKnown
}); // { chargedMicrousd, usageKnown, overReservation }
```

Reservations are one-shot; repeated reservation IDs are denied rather than granting a second dispatch. The full worst-case cost is charged before work. Known actual cost returns only the unused amount and releases its concurrency lease. Unknown cost is permanently charged at the reservation and retains its lease until the **request** deadline, including across object restart, cancellation and request close. A later conflicting settlement cannot refund an unknown debit. Repeating the same settlement is idempotent. Observed overspend is recorded, never clipped to the reservation, and causes a typed failure with cost evidence.

Account, tenant and user counters use the chosen period; the request cap spans its entire execution. Concurrency persists across period boundaries. At most 128 reservations can belong to one request. Expired records are removed through indexed, bounded alarms; old expiry pointers cannot delete renewed records. Request, nonce, lease and periodic quota records have explicit retention. Lifetime qualification counters intentionally retain pseudonymous accounting totals so cleanup cannot replenish a total authorization. No prompt, source content, tool arguments/results or approval objects enter this store.

## Azure tool callback contract

All tool calls require a currently published signed tool name and a fresh response from the same existing Azure authority. There is no copied Founder role classifier in Cloudflare. Invocation rejects missing configuration, canceled/expired context, substituted scope and model-selected callback targets. `LEGEND_TOOL_CALLBACK_ENABLED` defaults to disabled unless explicitly `true`.

To enable a **verified** callback, configure the fixed HTTPS `LEGEND_AZURE_TOOL_CALLBACK_URL`, separate `LEGEND_TOOL_CALLBACK_KEY_ID` and secret base64 `LEGEND_TOOL_CALLBACK_SECRET`, fixed `LEGEND_DEPLOYMENT_ENVIRONMENT`, and an explicit positive `LEGEND_TOOL_MAX_COST_MICROUSD`. Redirects, plaintext, local/IP destinations and nonstandard ports are rejected. The callback receives fresh signed scope and current authorization version; no bearer session or production credential is forwarded.

`toolBroker.execute({context,call:{id,name,arguments},idempotencyKey,signal})` requires lowercase SHA256 of `${requestId}:tool:${call.id}` for its idempotency key. It reserves execution cost independently of model cost and returns `{output,usage:{costMicrousd,costEvidence}}`; errors after dispatch carry their conservative `usage` too. Runtime must await the broker's cancellation/accounting handling and include those charges in the task total.

Callback JSON version `legend-tool-callback.v1` carries `requestId`, `scope`, `contextDigest`, `call`, `actionDigest`, `environment`, `idempotencyKey`, `issuedAt`, `expiresAt` (maximum 30 seconds), `maxCostMicrousd`. Its signature uses the same header/message construction as above with the configured callback path and distinct secret. Azure verifies that signature, current identity/session/revocation, object ACLs and tool-schema rules **at execution**, then durably coordinates approval consumption and idempotency before side effects.

`actionDigest` is lowercase SHA256 of sorted-key canonical JSON:

```js
{ version: 'legend-tool-action.v1', scope, requestId, environment, name, arguments }
```

Arrays retain order. All action resources, revisions/diff digests and environment therefore participate in approval identity. The authenticated Azure UI/authority must approve this exact digest with an expiry and atomically consume it; `confirmed`, `approved` or similar model arguments convey no authorization. Retries must return the durable original receipt or a definite non-executed failure. An HTTP timeout cannot establish rollback and is never retried automatically by this broker.

A successful TLS callback receipt must contain `version:'legend-tool-receipt.v1'`, exact `requestId`, `contextDigest`, `toolCallId`, `actionDigest`, `idempotencyKey`, current `authorizationVersion`, literal `reauthorized:true`, `output` and `usage:{known,costMicrousd}`. Invalid, stale or cross-scope receipts disclose no output and are charged conservatively. Cloudflare's digest matching is a binding check, not approval authority or proof that an unimplemented Azure callback exists.

**Existing Azure limitation:** `LegendFounderToolAuthority` currently accepts `FounderAiMutationAuthorization(CorrelationId)` and consumes it using an instance-local `HashSet`. That does not implement exact action/resource/environment/diff approval or durable replay. Cloud mutation callbacks must stay disabled until the Azure owner implements and verifies those semantics in the existing authority. This file does not claim its simulated denial test proves live Azure mutation security.

## Cloud execution and release gates

`executeGeneratedCode` always returns `cloud_sandbox_unverified`. No shell tool, local process runner, Azure app-process execution or Mac fallback is provided. Before enabling code tools, verify an account-accessible cloud image digest and pinned checkout, non-root execution, filesystem containment, CPU/memory/time limits, default-deny egress, no production credentials, safe artifact delivery and measured cost/idle shutdown. Repository scripts are untrusted. Cloudflare Sandbox exists, but its availability does not prove this repository's .NET/iOS/Android toolchains or controls work there.

Authoritative references checked September 18, 2026: [SQLite-backed Durable Object storage transactions](https://developers.cloudflare.com/durable-objects/api/sqlite-storage-api/), [Durable Object implementation rules](https://developers.cloudflare.com/durable-objects/best-practices/rules-of-durable-objects/), [Sandbox runtime](https://developers.cloudflare.com/sandbox/concepts/containers/), [Sandbox limits](https://developers.cloudflare.com/sandbox/platform/limits/), [Sandbox outbound controls](https://developers.cloudflare.com/sandbox/guides/outbound-traffic/). Account access, deployment and actual non-root/egress behavior require separate evidence.

Run `node --test Legend-Cloudflare/tests/security/*.test.mjs` from the repository root. Current local component result: 33 passing, zero failing. These use real Web Crypto and production module code with synthetic data and a transaction-compatible storage simulation. `tests/security/action-digest-vectors.json` provides four frozen synthetic canonicalization/digest vectors for the Azure implementation; Unicode escaping and ECMAScript number encoding must match, rather than hashing default `System.Text.Json` serialization.

An additional actual local workerd test passed using workerd `1.20260918.1` and Miniflare `5.20260918.0-alpha`: 20 concurrent signed requests admitted exactly five reservations under a 100-microUSD account ceiling, rejected the other 15 atomically, then still rejected replay after process/runtime recreation with persisted SQLite storage. This test has no AI binding and blocks all external Worker HTTP. Run `node --test Legend-Cloudflare/tests/security/workerd.integration.mjs` with lead-installed Miniflare; alternatively set `LEGEND_MINIFLARE_MODULE` to the absolute installed package entrypoint. It uses a temporary test directory, disposed and deleted afterwards, and may require localhost socket permission. Missing Miniflare fails this dedicated test explicitly. The reservation-only test fixture must never be deployed.

After lead integration run `node --test Legend-Cloudflare/tests/security/application.integration.mjs`; `LEGEND_INTEGRATION_ROOT` optionally identifies the exact other checkout to review. Three checks passed against integrated candidate `248e939f`: forged-tenant denial before inference, unqualified-model failure/closure/persisted replay, and private/no-store SSE failure delivery. The earlier source-assembly result was repeated against the actual integrated files. Rerun after material boundary changes before release.

None of these tests establishes live inference, Azure tool authorization, cloud sandbox verification or complete application acceptance. Required remaining gates include actual account model canaries, deployed Durable Object checks, Azure callback integration, source ACL/revocation/deletion, artifact isolation, complete held-out tasks and application regressions.

Independent runtime review identified and resolved missing tool cost totals, composite ID bounds, and stream-reader cancellation propagation. The runtime now awaits broker settlement and includes callback error charges. Runtime commit `3e233f2c` also resolves observed overspend and lost-settlement-acknowledgment reporting; its code and three additional regression tests were reviewed. Security implementation still requires lead review and exact-candidate integration checks.

## Azure authority reuse review, September 18, 2026

The smallest safe callback uses the existing `LegendFounderToolAuthority` registry and executor. Its `IsReadOnly` classification is the initial publication boundary; Cloudflare must not copy that classification into its own permission registry. Azure verifies the callback signature, resolves the canonical active actor and owned request/conversation from its existing authorities, rechecks current permissions and operation deadline, and only then calls `ExecuteAsync`. The signed callback's role strings do not create a `ClaimsPrincipal`. Current external-provider policy must also be preserved for research/tools.

Existing reusable safeguards:

- `AgentPortal/Services/FounderSoftwareRemediationService.Batch.cs`: `StageBatchAsync` hashes the complete proposal into a durable operation ID; `FounderSoftwareRepairBatch.Revision` is an EF concurrency token. Staging/publication claim the record before GitHub writes, recheck the exact source/base/head/PR and active remediation authority, and leave uncertain writes locked as `OutcomeUnknown`. Keep this existing execution authority and its GitHub App/Key Vault credentials inside Azure.
- `FounderSoftwareRemediationService.cs`: `RequireActiveAuthorityAsync` rereads the durable global revocation switch with `AsNoTracking`. `RequestReleaseAsync` observes validation and explicitly does not authorize deployment. `ReleaseApprovedAsync` delegates to the same exact-head publication path. These are useful execution checks, but they do not establish an expiring authenticated per-action approval.
- `Infrastructure/Messaging/MessagingService.FounderAi.cs`: canonical ownership checks require the active Founder manager/profile; request fingerprints, unique `ClientMessageId` keys, immutable terminal receipts and `OutcomeUnknown` prevent a repeated conversation operation from silently repeating work. Reuse this canonical conversation/operation identity for callbacks.
- `FounderDiagnosticsController`: existing CSRF-protected Founder forms submit exact proposal JSON or PR/SHA with explicit confirmation. They can host exact-action review without moving approval authority to Cloudflare.

The concrete missing approval semantics are authenticated user/session, exact action digest/resource/environment/revision, expiry, atomic consumption and a durable execution receipt. The existing repair singleton has operation/revision fences, but no per-user/session approval fields; `FounderAiMutationAuthorization(CorrelationId)` only adds an in-memory replay set. Neither grants a safe generic cloud mutation capability.

The agreed implementation reuses a bounded typed v2 extension of the **existing canonical turn receipt** for a short-lived server delegation only. `MessagingService.GetFounderAiOperationDelegationAsync` returns it only for the current owned, open, pending operation and active Founder/profile. The exact-action approval and terminal execution receipt use one narrowly scoped `FounderAiActionAuthorization` entity in the same canonical Azure database, subordinate to `LegendFounderToolAuthority`; it stores no independent role/permission policy. Approval state is not inserted as an assistant/service response that could prematurely complete a conversation.

The existing turn parser uses `UnmappedMemberHandling.Disallow`. The coordinated messaging change explicitly admits typed v2 delegation metadata while preserving v1 history; old v1 records never grant a callback delegation. The approval UI must display the exact proposed action before creating the digest-bound approval; a broad `FounderCommandConfirmed` value sent before model-selected arguments exist cannot satisfy this requirement.

### Durable exact-action implementation

`LegendFounderToolAuthority.ExecuteAsync` remains the only registry/dispatcher. Its optional `serverDerivedScope` routes through `LegendFounderToolAuthority.Approvals.cs`, then the same execution core. Scope presence makes the durable path mandatory. The old correlation/HashSet route exists only for explicit transitional local/Teacher callers without cloud scope, and must be removed before deleting the local foundation. A cloud authorization cannot enter that fallback, including the restricted research path.

The authenticated exact-action review endpoint calls `IssueCloudActionApprovalAsync(founder, scope, toolName, argumentsJson, expiresUtc, cancellationToken)`. It is an internal service API, not a model tool or callback issuance route. It validates the existing tool schema and current canonical delegation, stores a unique digest binding the action and all wire scope fields, and additionally binds the operation fingerprint and delegation deadline. Expiry cannot outlive the at-most-120-second pending delegation. No broad confirmation Boolean or correlation grants approval; every tool classified as a mutation by the existing registry requires a matching record.

For execution, `FounderAiToolCall.IdempotencyKey` must equal Worker SHA256(request ID + `:tool:` + call ID). The authority checks the authenticated principal's `sid`, current Founder/profile, and every field of the persisted operation delegation. It claims `Approved → Executing` with the database `Revision` concurrency token before dispatch, then rechecks the pending operation immediately before entering the existing service authority. Successful terminal output is bounded and stored as `Completed`; the same idempotency key can read that receipt after fresh authorization. Changed arguments, resource revisions, tenant/user/session/environment, role snapshot, authorization version or operation never reuse it. In-flight or uncertain execution remains consumed. Exceptions, cancellation, oversize output, or lost persistence acknowledgment never return an approval to `Approved`.

Approval/receipt writes use isolated canonical database contexts so they cannot accidentally persist unrelated tracked business changes. The migration adds a unique action-digest index, a concurrency token, ownership lookup and expiry indexes, plus the existing conversation foreign key. No migration has been applied by this security workstream. The lead owns the generated designer/model snapshot and combined .NET verification.

Read tools use the same durable execution receipt because a replayed research/read request may incur cost even without a business mutation. Only the existing registry's read classification permits automatic `ReadExecution` admission after current scope authorization. A distinct `AuthorizationKind` prevents that admission from being reinterpreted as `FounderApproval` after a classification change. Reads use the same unique-digest/CAS fence, terminal receipt replay and permanent uncertain-outcome consumption. No browser/model confirmation or new permission store is involved.

`LegendFounderCloudActionApprovalTests.cs` contains 40 focused cases, including real SQLite concurrent claims/read admission/unique constraints, fresh-authority durable replay for reads and mutations, uncertain execution, exact argument/scope changes, revoked profile or terminal operation, legacy/model confirmation denial, isolated persistence, Worker digest interoperability, and rejection of numeric precision loss during canonicalization. At this commit these .NET cases are authored but **not run**; a .NET build slot is coordinated by the lead. The previously reported Worker tests remain a separate verification result.

Session revocation has a precise limit: the canonical messaging authority has no independent identity-provider logout/revocation registry. The callback must derive its principal from the trusted persisted short lease and recheck current Founder/profile and pending-operation state; arbitrary callback role claims must never become a principal. Closing/completing the operation or deactivating the profile revokes the next callback. Immediate identity-provider session revocation cannot be claimed solely from a persisted `sid` snapshot; its maximum residual authority is the bounded lease unless the existing identity owner supplies a current revocation signal. Keep any operation requiring stronger semantics disabled until that signal is wired and tested.

An additional coding-agency blocker exists in `FounderSoftwareRemediationService.PrepareAsync`: `actorMode == "legend"` always refuses repairs and recommends OpenAI Teacher because retained software competency is absent. A Cloudflare callback must not bypass it by masquerading as Teacher. The owner must replace that gate with verified bounded cloud execution/test evidence while preserving existing authorization and publication checks. Cloud execution is still unverified, so mutation/code tools remain disabled.

## Read-only account/billing verification, September 18, 2026

Wrangler `4.135.0 whoami --account <authorized account> --json` succeeded through its existing encrypted keychain login. No token was extracted. The observed OAuth scopes were `user:read`, `offline_access`, `account:read`, `workers_scripts:write`, `ai:write`. Account type `standard` is an account classification, not proof of a Workers Free/Paid subscription. The installed Wrangler command surface exposes no billing/subscription reader.

Plan status and accrued billing remain **unverified**: the official [subscription API](https://developers.cloudflare.com/api/resources/accounts/subresources/subscriptions/methods/get/) requires Billing Read or Billing Write; [billing permissions documentation](https://developers.cloudflare.com/billing/understand/billing-permissions/) identifies Billing Read for billing/subscription/invoice visibility. The present OAuth scopes do not include it. Public [Workers pricing](https://developers.cloudflare.com/workers/platform/pricing/) lists a $5/month paid plan, but that does not establish this account's active plan or bill.

Required account evidence is the actual Workers plan, active recurring base/add-on subscriptions and currency, billing-period/renewal boundaries, and current accrued/unbilled Workers AI, Workers/Durable Objects, storage/network/Containers charges. Check other charges sharing the approved allowance before allocating the $10 qualification or $30/month production budget. No subscription was changed, resource provisioned, paid inference called, invoice retrieved or account billing cap verified during this review.
