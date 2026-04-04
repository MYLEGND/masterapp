# Security Hardening Roadmap (Top 5 Risks)

## Purpose
This document defines the implementation contract for fixing the top five production risks without changing intended behavior for authorized users.

## Guardrails
1. Block only unauthorized cross-scope access; preserve existing authorized workflows.
2. Add controller-level checks plus service-level enforcement (defense in depth).
3. Prefer anti-enumeration responses on unauthorized resource IDs (`NotFound`) unless route semantics require `Forbid`.
4. Verify each phase with build + focused regression tests before moving forward.
5. Ship migrations with deterministic rollback path and data preflight.

## Risk Matrix

### Risk 1: ClientApp Production IDOR
- Surface:
  - `ClientApp/Controllers/ProductionController.cs`
  - `/production/history/client`
  - `/production/add/client`
  - `/production/update`
  - `/production/delete`
- Intended behavior:
  - Caller can only read/write records for the effective client context.
  - `clientId` mismatches are denied.
  - Record `id` access must be scoped to effective client.
- Acceptance:
  - Unauthorized caller cannot read/update/delete another client's production row.
  - Authorized caller behavior unchanged.

### Risk 2: Analytics Team Scope Escalation
- Surface:
  - `AgentPortal/Controllers/WebsiteAnalyticsController.cs`
  - `ResolveScopeAsync(...)`
- Intended behavior:
  - Non-founder callers cannot elevate to global scope via `team=true`.
  - Founder retains explicit global/team and per-agent views.
- Acceptance:
  - Non-founder `team=true` is constrained to caller agent scope.
  - Founder behavior remains intact.

### Risk 3: Clients Quick-View Action/Commitment IDOR
- Surface:
  - `AgentPortal/Controllers/ClientsController.cs`
  - `Actions`, `Commitments`, `CreateAction`, `CreateCommitment`, `FulfillCommitment`, `BreakCommitment`
- Intended behavior:
  - Agent can only access/mutate actions and commitments for owned clients.
  - Unowned client IDs and commitment IDs are denied.
- Acceptance:
  - Cross-agent calls are denied.
  - Existing owned-client flows remain unchanged.

### Risk 4: Action Mutation Service Boundary Bypass
- Surface:
  - `AgentPortal/Services/ExecutionEngine.cs`
  - `AgentPortal/Controllers/ActionsController.cs`
- Intended behavior:
  - Update/delete/read-by-id mutations require actor ownership validation.
  - Controller checks are not the only trust boundary.
- Acceptance:
  - Non-owner action mutation attempts fail at service layer.
  - Owner actions continue to work without behavioral drift.

### Risk 5: Analytics Ingest Duplicate Race
- Surface:
  - `AgentPortal/Controllers/API/AnalyticsIngestController.cs`
  - `Infrastructure/Data/MasterAppDbContext.cs`
  - Infrastructure migrations
- Intended behavior:
  - Idempotency guaranteed by DB uniqueness on `ClientEventId` (non-null).
  - Duplicate insert under concurrency returns idempotent success.
- Acceptance:
  - Duplicate write no longer depends on app-level read-before-write race.
  - Existing analytics ingest payload contract stays unchanged.

## Phase Checklist

### Phase 0 (Current)
- Baseline build check for `AgentPortal` and `ClientApp`.
- Contract map + acceptance criteria documented.
- Define test targets for each risk.

### Phase 1
- Introduce shared ownership/normalization guards used consistently by controllers/services.

### Phase 2
- Implement Risk 1, 3, 4 endpoint and service-level ownership enforcement.

### Phase 3
- Implement Risk 2 scope-resolution hardening in analytics controller.

### Phase 4
- Implement Risk 5 DB uniqueness + ingest idempotent duplicate handling.

### Phase 5
- Full verification pass, summarize behavior deltas, and rollout notes.

## Rollout Readiness (Prod)
1. Run migration `20260404113000_AddAnalyticsClientEventIdUnique` in staging first.
2. Confirm no migration errors and validate analytics ingest still returns `status=ok` for new events.
3. Validate duplicate ingest replay returns `status=duplicate_ignored`.
4. Smoke test:
   - `ClientApp` production history/add/update/delete as owned client.
   - `AgentPortal` client quick-view actions/commitments for owned and unowned clients.
   - `WebsiteAnalytics?team=true` for non-founder remains agent-scoped.
5. Monitor for 24h:
   - `403/404` increase on hardened endpoints (expected initial rise from blocked unauthorized calls).
   - no increase in unhandled exceptions.
   - stable analytics ingest throughput and latency.
