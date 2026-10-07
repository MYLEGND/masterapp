# LEGEND AI engineering operating contract

This is the canonical repository-level operating contract for AI-assisted engineering in
`MYLEGND/masterapp`. It exists to maximize reasoning quality, creativity, speed, and
autonomy **without allowing convenience, uncertainty, or a failed validation/release to
rewrite protected system truth**.

This file governs operating behavior. It does not grant permissions and never overrides
server authorization, GitHub protections, security/privacy policy, executable contracts,
or Founder-only approval boundaries.

## 1. Truth hierarchy and freshness

Never treat a conversation, memory, old audit, comment, test name, or documentation summary
as current system truth merely because it sounds authoritative.

For every material decision, establish truth in this order:

1. current exact repository/ref state and the user's explicit current request;
2. executable authorization, security, data, runtime, and source contracts;
3. canonical tests, validation topology, and workflow definitions;
4. immutable package/operation/receipt evidence and observed live provenance;
5. current canonical documentation;
6. historical notes, summaries, conversations, and assumptions.

If these disagree, investigate the disagreement. Do not average conflicting evidence or
silently choose the version that makes the task easier.

Label material conclusions as `CONFIRMED`, `INFERENCE`, or `UNVERIFIED`. Unknown is a
valid engineering state. Never convert missing evidence into a pass.

## 2. Creativity is expected; authority drift is not

The protected boundaries below are **constraints, not design templates**.

Inside the user's requested outcome and the verified authority boundary, think aggressively:
improve UX, simplify architecture, remove unnecessary work, optimize performance, redesign
presentation, invent better algorithms, consolidate code, improve observability, and propose
superior solutions. Do not preserve mediocre implementation merely because it already exists.

However, creativity must not manufacture a second authority or weaken a protected one.

The governing distinction is:

- **Creative solution space:** presentation, behavior, algorithms, composition, performance,
  product experience, implementation strategy, and canonical refactoring inside the owned
  domain.
- **Protected authority space:** release control, authentication/authorization, privacy,
  tenant/owner isolation, immutable event/action identity, provider credentials/destination
  authority, production data mutation, protected runtime forms/signals, and any other
  server-owned integrity boundary.

Solve boldly inside the first. Change the second only when the task genuinely requires it and
the required human/security/Founder authority explicitly permits that exact change.

## 3. Canonical Git and production release truth

`legend/approved-changes` is the **sole protected Git release authority**.

There is no second mutable `production` release branch and no branch-promotion model.
Production state is established by immutable release evidence plus exact live runtime
provenance.

Canonical references:

- `Docs/releases/branch-lifecycle.md` — single-branch lifecycle and queue semantics.
- `DEPLOYMENT.md` — production deployment contract.
- `scripts/validation-resume.py` — validation topology, release targets, preservation rules,
  and canonical release inventory.
- `scripts/release-lifecycle.py` — trusted approved-branch integration/lifecycle authority.
- `.github/workflows/all-intentional-direct-release-20260918.yml` — sole web publication
  workflow.
- `scripts/deploy-approved-app.py` — canonical application publication/finalization authority.

For ordinary application/system work:

- start from the exact current `legend/approved-changes` head on one bounded isolated branch;
- never edit the protected branch directly;
- never create or restore a second release branch, deployment workflow, manual Azure publish
  path, alternate migration authority, shadow finalizer, or bypass status;
- never rebuild or reinterpret a candidate merely to make release machinery accept it;
- never modify release-control code as a reaction to an application validation failure,
  Azure publication failure, ambiguous provider response, or live verification failure.

The known-good release control plane is a protected system. AI may inspect it, diagnose it,
reason about it, and prepare a proposed control-plane change. **AI may not mutate the protected
release-control architecture unless the Founder explicitly approves that specific
control-plane change.**

A model-generated approval, inferred intent, old approval, broad mission statement, or desire
to "get it deployed" is not permission to redesign the release control plane.

All `.github/workflows/**` are governance-sensitive because a workflow can create an
alternate authority. Any workflow capable of validation, packaging, secret/cloud mutation,
merge, migration, publication, or deployment must be treated as protected control-plane
surface.

## 4. Release behavior agents must preserve

The release system is intentionally designed so delivery problems are resolved **around the
candidate**, not by rewriting the machinery.

Preserve these semantics:

- validation is exact-head and dependency-aware;
- compatible successful evidence is reused;
- only failed or dependency-invalidated validation reruns;
- affected applications come from canonical repository ownership/inventory, not guesses;
- validated immutable package identity is preserved through release;
- selected application targets publish through the canonical publisher;
- successful targets remain successful when a sibling is unresolved;
- ambiguous writes are reconciled from durable remote identity and are never blindly replayed;
- finalization is a single canonical bounded authority;
- unresolved-only reconciliation never authorizes wholesale restart;
- live success requires exact runtime provenance and applicable post-publication proof;
- unknown state fails closed instead of being presented as success.

Do not optimize release by weakening these properties. Optimize application changes so they
enter this system cleanly.

## 5. Required reasoning before implementation

Before changing source, build a compact evidence-backed model of the task.

1. **Outcome:** state what the user actually wants to experience or accomplish.
2. **Protected constraints:** identify what must remain unchanged.
3. **Exact base:** verify current protected head, candidate branch, active overlapping work,
   and relevant instructions.
4. **Authority map:** trace authenticated entry -> authorization -> canonical decision owner ->
   persistence -> events/providers -> clients -> tests -> packaging/release/live proof as
   applicable.
5. **Impact set:** identify every application, shared library, contract, schema, provider,
   workflow-owned validation lane, and platform that can be affected.
6. **Failure classification:** distinguish code defect, contract defect, test defect,
   configuration/provider condition, data/migration state, deployment drift, authorization
   denial, expected policy behavior, and unknown.
7. **Competing logic search:** find duplicates, overrides, fallbacks, compatibility shims,
   shadow paths, copied policies, stale helpers, hardcoded special cases, and platform drift.
8. **Solution alternatives:** consider multiple valid approaches. Choose the one that best
   satisfies the user while reducing authority count, state ambiguity, coupling, and future
   maintenance.
9. **Validation plan:** define focused proof, affected regressions, negative/adversarial cases,
   compatibility checks, and live proof before editing.
10. **Release-safety check:** confirm the planned change does not require altering protected
    release control. If it appears to, stop and classify why before touching it.

Do not perform repository-wide cleanup simply because a nearby defect exposed old code.
Remove a competing path only when its canonical replacement is verified and the removal is
inside scope.

## 6. Canonical architecture rules

### Absolute canonical-source invariant

This rule is higher priority than implementation convenience, speed, local compatibility, or
an agent's preferred design:

- Every behavior has **one canonical decision owner** and every durable fact has **one
  canonical writable source of truth**.
- Never solve a defect by adding an override, monkey patch, stacked stylesheet/script,
  duplicate file, parallel service, shadow workflow, second registry, copied policy,
  alternate event path, competing cache/state store, fallback authority, compatibility
  fork, hidden special case, or a second implementation that can independently decide or
  mutate the same truth.
- Never leave the old incorrect path active and place a new path in front of or behind it.
  Repair the canonical owner itself, then remove the superseded competing logic once the
  replacement is proven.
- Never create a new file merely to override a canonical file when the canonical owner can
  be corrected directly.
- Never use CSS/JS/controller/prompt/test/configuration patches to mask an incorrect decision
  owned elsewhere. Follow the data/authority chain to the first incorrect decision and fix
  that owner.
- A compatibility adapter is acceptable only when a real deployed contract requires it and
  it is a **one-way, non-authoritative adapter** into the canonical owner: it may translate
  shape, but it may not own policy, durable state, business truth, retries, authorization,
  or an independent write path. Its necessity and removal boundary must be explicit.
- If two sources can disagree about the same fact, the architecture is not finished. Resolve
  the ownership conflict rather than adding synchronization between competing truths.
- If a proposed solution requires "temporary" duplication, a second writer, a shadow path,
  or an override to make the new behavior win, reject that design and find the canonical
  repair instead.
- Tests must prove both that the canonical path works **and that the superseded/alternate
  path cannot still execute**.

The desired end state after every repair is simpler than before: fewer authorities, fewer
writable paths, less ambiguity, and one obvious source to inspect when truth is questioned.

- Fix the **first incorrect decision at its authoritative owner**, not the final symptom.
- Prefer one authority with clear adapters over several nearly-equivalent implementations.
- Delete or consolidate obsolete competing logic instead of stacking another override.
- Do not add a service, datastore, queue, workflow, retry system, provider layer, event map,
  or abstraction until current owners are inspected and proven unable to own the requirement.
- Compatibility logic must have an explicit reason, bounded lifetime/identity, and no ability
  to become a second writable source of truth.
- Preserve backward/forward contract meaning where deployed clients or persisted data require
  it; do not preserve architectural mistakes merely for convenience.
- Make cancellation, deadlines, retries, idempotency, concurrency, partial success, and
  terminal failure explicit for consequential operations.
- A timeout must not be "fixed" by making an operation unbounded.
- An ambiguous write must be reconciled; never assume retry is harmless.
- Observability must expose stage/reason/correlation/provenance without exposing credentials,
  private content, raw sensitive values, or another owner's data.
- Server-owned authorization and business truth stay server-owned. Web/iOS/Android may present
  and request; they must not independently redefine authorization, provider selection,
  production eligibility, conversion truth, or protected runtime execution.

## 7. Additional non-negotiable integrity rules

### Proof integrity

- Never make a failing candidate look green by deleting, skipping, renaming, weakening, narrowing,
  mocking, short-circuiting, or changing the expected result of a valid test/check.
- Never change timeout/retry/error handling merely to hide a real failure or convert unknown into
  success. Fix the owning defect or classify the dependency honestly.
- A test may change only when independent evidence proves the test/expectation is stale or wrong;
  preserve or strengthen the behavior it was intended to protect.
- Never suppress exceptions, swallow terminal errors, replace an error with an empty/default
  success object, or make a consequential operation "best effort" when the canonical contract is
  fail-closed.
- Required proof stays required. Local, mocked, cached, historical, or lower-layer evidence cannot
  substitute for the exact evidence class the contract requires.

### Immutable identity and lineage

- Stable identities are architecture, not implementation detail. Preserve canonical IDs, keys,
  routes, owner/scope identity, node IDs, action/event names, binding IDs, schema identity,
  correlation/dedupe identities, provider request identity, and publication/version lineage unless
  the task explicitly requires a governed identity migration.
- Never generate a new identity to escape a conflict, broken reference, duplicate, or validation
  failure. Resolve the canonical identity/lineage problem.
- If an identity must change, define the migration, compatibility boundary, affected readers and
  writers, historical interpretation, rollback, and proof before implementation.
- Never infer or backfill historical identity, ownership, attribution, consent, or business outcome
  from incomplete evidence.

### Generated artifacts and configuration authority

- Generated, compiled, cached, vendored, copied, exported, or published artifacts are never the
  editable source of truth when a canonical source exists. Change the generator/source and
  regenerate; do not hand-edit the artifact to make behavior differ.
- Configuration has one owner per setting. Do not duplicate the same setting across code, workflow,
  environment, JSON, database, client, and provider layers with independent precedence rules.
- Secrets/credentials are never copied into source or alternate configuration to bypass the
  canonical secret authority.
- Environment-specific behavior must be expressed through the canonical configuration authority;
  do not add hostname/user/device/tenant/test-specific production branches to force one environment
  to behave differently unless that distinction is an explicit product/security contract.

### No hardcoded special-case production behavior

- Never hardcode a particular user, Founder, tenant, business, domain, campaign, prompt, answer,
  language, device, browser, timestamp, test fixture, or current incident as the production fix.
- A concrete incident may reveal a general invariant; fix that invariant at its owner and prove both
  the reported case and representative neighboring cases.
- Feature flags, allowlists, compatibility switches, and exception lists may not become hidden
  permanent overrides. They require one canonical owner, explicit semantics, bounded scope, and a
  reason they are part of the intended product/security model.

### Scope, blast radius, and shared capability ownership

- Change only the smallest coherent authority/impact set, but inspect every dependent consumer that
  can be semantically affected.
- Shared capabilities must remain shared. Do not fix one app by copying the capability into that app
  when a shared authority already owns it.
- A local presentation difference is allowed; a local reimplementation of shared business,
  authorization, measurement, provider, persistence, or release truth is not.
- Do not bundle unrelated cleanup, formatting churn, opportunistic refactors, or speculative
  architecture changes with the requested repair.
- Before merge, prove unrelated applications/owners/platforms either remain unaffected or are
  intentionally included in the validated impact set.

### No temporary production debt

- Debug probes, temporary branches in production logic, local overrides, emergency fallbacks,
  migration scaffolding, compatibility shims, and diagnostic instrumentation must not silently
  become permanent architecture.
- If temporary code is genuinely required, its owner, activation condition, removal condition, and
  inability to become a second authority must be explicit and validated.
- Never call a workaround "temporary" as justification for violating the one-canonical-source rule.

## 8. Website Studio and public-site protection

Website creativity should be maximal **without breaking runtime truth**.

Current canonical principles include:

- `WebsiteContentDocument` v3 is the writable website-content source.
- Master Source is a server-generated inspection projection, not a write surface.
- Selected Source and Canvas must converge on the same canonical node/state.
- Stable node identity and protected system semantics are not design decoration.
- Protected runtime forms, actions, signals, bindings, and executable destinations cannot be
  retargeted through visual/source editing.
- Editable copy, layout, styling, responsive behavior, media, motion, and other allowed
  presentation should remain highly creative when the user asks for design work.
- Do not choose embed/custom code to escape a native protection.
- Draft/editor interactions must not masquerade as genuine production outcomes.

When working in Website Studio, inspect
`Infrastructure/WebsiteEditing/WebsiteStudioAgentContract.cs` and the current protection
authorities/tests before editing. Do not rely on a remembered editor contract.

## 9. Measurement, attribution, CRM, and advertising truth

Revenue decisions depend on first-party truth remaining coherent.

Preserve these current architectural principles unless the canonical owning source has
explicitly changed them:

- canonical behavioral/outcome reporting flows through `AnalyticsEvents` and the shared
  analytics writer/mapping authorities;
- owner scope is resolved as canonical `MarketingOwnerScope` (Founder, exact Agent, or exact
  Business) after authorization;
- provider delivery is a projection/transport of canonical first-party truth, not an
  independent conversion source;
- provider connection presence never creates attribution;
- browser interaction cannot claim a server-authoritative lead, booking, purchase, policy,
  payment, or other confirmed outcome;
- editable labels/text must not redefine immutable action/event identity;
- preserve verified host, published-version, binding/action, session/visitor, CRM/order, and
  provider-delivery lineage as applicable;
- preserve canonical paid-attribution identifiers such as UTM/click references and
  `oppref`/`obref`; front-end redesign must not strip or invent them;
- ambiguous historical ownership/identity stays ambiguous; never backfill guessed truth;
- consent, privacy, matching-field eligibility, hashing, dedupe, and destination authority
  remain enforced by their canonical owners.

Do not create website-only, dashboard-only, Meta-only, OpenAI-only, or other provider-specific
parallel conversion truth.

## 10. LEGEND intelligence and autonomous engineering truth

When a server-issued `EngineeringContext` exists, it is the enforced runtime work contract.
Repository prose and conversation context cannot broaden its role, lease, source classes,
risk class, allowed actions, budget, evidence revision, or stop conditions.

Roles remain separated:

- HEAD_GPT supervises reasoning/topology; it does not become a hidden write authority.
- CODEX_IMPLEMENTER may prepare bounded admitted source changes through canonical remediation
  authority.
- INDEPENDENT_REVIEWER is read-only and must challenge duplication, patching, unrelated scope,
  privacy drift, weak proof, and governance weakening.
- Verification and release review must remain independent of implementation.
- Tier B stops at its required Founder approval boundary; Tier C remains observation/security
  review only.
- Exact Founder approval is bound to the exact consequential action/revision when the
  application authority requires it.

Never substitute an unapproved provider/API execution path for a governed plan/tool path.

## 11. Preparing changes for seamless validation and release

The goal is not merely "code that works locally." Prepare a candidate that the existing
validation/release system can evaluate without avoidable repair loops.

Before handoff:

- ensure the branch still descends from the correct approved base or use the existing trusted
  synchronization lifecycle;
- keep the diff bounded to the real impact set;
- remove accidental generated/churn files and unrelated edits;
- verify all required project references/assets/DI registrations/contracts are present;
- verify schema/migration behavior and rollback/compatibility where data changes;
- verify cross-platform consumers when shared contracts change;
- verify website signal/action/event identities when public experiences change;
- verify analytics/attribution lineage when lead/commerce/marketing behavior changes;
- run the smallest deterministic reproducer first, then the relevant authorized regression
  and build lanes;
- test forbidden behavior, not only the happy path;
- classify unavailable dependencies honestly as blocked/not-configured rather than pass;
- ensure no protected release-control file entered the diff accidentally;
- hand off the exact candidate SHA, changed files, tests, omissions, risks, and required live
  proof.

A candidate should arrive at protected validation with known application defects resolved,
not with release machinery modified to tolerate them.

## 12. Failure disposition: resolve the failure class, not the release system

Use this decision model:

- **Application/source defect:** repair the canonical application owner on the retained
  candidate branch; rerun only invalidated proof.
- **Shared contract defect:** repair the authoritative shared contract and every affected
  consumer; prove compatibility.
- **Test defect/false-green:** prove the test is wrong before changing it; never weaken an
  assertion simply to obtain green.
- **Configuration/provider condition:** diagnose the canonical configuration/provider owner;
  do not encode environment accidents into application or release logic.
- **Transient validation infrastructure failure:** resume/retry through existing bounded
  validation authority while preserving compatible green evidence.
- **Ambiguous publication/write:** reconcile durable operation/runtime identity for the same
  candidate; never blindly replay.
- **Terminal publication failure:** preserve successful siblings, retain the candidate and
  evidence, diagnose the failed target, and repair the actual defect. Do not redesign the
  release flow.
- **Migration/data failure:** correct the authoritative migration/data contract in a new
  candidate when needed; never improvise a production-only schema mutation path.
- **Live provenance mismatch:** block completion and determine what is actually running.
- **Unknown/unclassified:** fail closed, preserve state/evidence, and investigate.
- **Confirmed release-control defect:** inspect read-only, produce a minimal isolated proposal
  with proof and rollback, and obtain explicit Founder approval **before** mutating protected
  release-control architecture.

No failure class authorizes an AI to make the release system fit its preferred implementation.

## 13. Founder approval and consequential actions

AI may inspect, diagnose, reason, recommend, simulate, and prepare proposed changes within
authorized source scope.

AI must not treat itself as the Founder.

Where the system requires Founder approval, approval must come through the authorized
Founder-controlled boundary and be bound to the exact action/revision as required. Never infer
approval from urgency, previous deployments, broad product intent, or a model's own judgment.

In particular, protected release-control mutation requires explicit Founder approval of that
specific control-plane change.

## 14. Verification and claims

Evidence classes are not interchangeable. Unit, mocked/in-process, SQL-backed,
provider-backed, authenticated live-production, and physical-device evidence prove different
things.

Never claim:

- a test ran when it was skipped or silently returned;
- a provider worked when it was mocked;
- production behavior from a local build;
- live deployment from a workflow merely starting;
- exact release success without matching candidate/package/deployed/live identity;
- "all green", "fixed", "complete", "flawless", or "production-ready" beyond actual evidence.

After deployment, require applicable exact runtime provenance and the original functional
reproducer/live acceptance proof.

## 15. Required engineering handoff

Every implementation handoff must include:

- requested outcome and protected constraints;
- exact base and candidate SHA;
- changed files and affected applications/platforms;
- confirmed root cause / authoritative owner, or explicit feature-design rationale;
- competing logic removed or intentionally retained and why;
- data/migration/config/provider implications;
- analytics/action/attribution implications where applicable;
- exact tests/checks executed and their evidence class/results;
- tests blocked, skipped, mocked, unconfigured, or not run;
- security/privacy/authorization/concurrency/cancellation/compatibility risks;
- release-control-plane status: `UNCHANGED` unless explicit Founder-approved control-plane
  work is the task;
- live proof still required;
- recommendation or stop condition.

The objective is a system that stays intellectually flexible at the application/product layer
while its critical truth, security, measurement, and release authorities remain stable,
auditable, and hard to accidentally rewrite.
