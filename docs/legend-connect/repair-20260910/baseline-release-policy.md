# Authorized baseline release policy

## Current owner decision — 2026-09-13

The owner explicitly instructed: “I WANT IT ON PRODUCTION WITH THE FAILURES. THEN ONCE DEPLOYED YOU CAN GO AHEAD AND FIX THE REMAINING FAILURES.” This authorizes one release with individually recorded model-quality failures. It is not evidence those failures are repaired, that autonomous learning is complete, or that production inference has been activated.

The `founder-model-quality-20260913` profile replaces the expired nine-failure exception for this candidate. The manifest records the exact source revision, production base, full expected roster, frozen test files, eleven reviewed failure identities, first assertion sites, exact failure-message hashes and four existing opt-in skips. No security or SQL-proof failure is included. Advancing the production base or changing runtime source outside the existing enumerated policy paths expires the exception.

## Evidence and execution boundaries

Exact candidate discovery supplies the complete expected roster, retaining repeated display names for distinct theory cases. Console discovery renders astral characters directly; VSTest TRX represents those argument characters as UTF-16 surrogate escapes. The expected roster preserves the TRX representation. The discovery comparison against the previous complete 2,934-case artifact has exactly one added evidence-framing regression and no removed cases after that representation conversion.

Focused real-model execution records the model-quality cohort through the configured authenticated Cloudflare endpoint on the owner's Mac. The remaining manifest `Passed` entries are required CI expectations, not a claim that those tests ran locally. The automatic production workflow must execute a fresh, complete, unfiltered suite. Its validator checks every result, exact roster multiplicity, test/execution identity, counters, timestamps, runner diagnostics, source/base binding and failure hash. Focused results and discovery alone cannot authorize deployment.

Known model failures remain `Failed`; the release decision may report `AcceptedKnownBaseline`, with `allTestsPassed: false`. A new failure, changed failure message/site, missing case, new skip, incomplete result, runner failure or unauthorized source change rejects the exception. The calculator exception permits only the recorded missing requested action, not extra or unauthorized actions. Authorization, identity, provider isolation and evidence checks execute before waived answer-quality assertions, so an early semantic failure cannot conceal those checks.

The existing production PR synchronization workflow remains the sole release authority. Dependency/security scans, migrations, artifact/tree identity, production readiness and post-deployment provider/SELECT-only SQL proofs remain required and unmodified by the quality exception. No blanket continue-on-error, skipped security assertion, forged result or alternate deployment route is authorized.

## Approved read-only configuration wiring

The owner separately approved the passwordless `LEGEND Readonly Validation Config` identity. Its only role action is `Microsoft.Web/sites/config/list/action`; both assignable scope and assignment are restricted to the existing `masterapp-portal/config/appsettings` child. Its federation trusts only `repo:MYLEGND/masterapp:environment:LEGEND-Production-ReadOnly-Validation`. Only that GitHub environment's client ID changed. No production write/deployment role, password, paid compute, subscription or paid service was added.

Candidate and post-deployment SQL checks reuse the existing configuration exporter and controlled-model transport. The exporter validates the same configuration allowlist for shell and JSON formats; private files enforce mode 0600, reject links/nonregular targets, and are deleted before execution. Credentials stay process-scoped and out of public artifacts. Configuration-inspection receipts now reflect actual step outcomes. Three stale source contracts were corrected to require this approved read-only OIDC path, truthful receipts and retained mutation prohibitions; all three focused tests passed.

## Current limitations

The previous completed GitHub run 34793665680 recorded 2,920 passed, eleven failed and four unexecuted cases; merge and deployment were skipped. A subsequent local full run was canceled after three stale workflow-contract failures were identified and is explicitly not acceptance evidence. The final automatic run must establish current complete results and actual deployment identity.

The controlled model remains Qwen3-4B-Instruct-2507 in its pinned MLX 4-bit checkpoint. Model-quality failures, rejected training adapters and unperformed authenticated/device checks remain visible. A backend deployment does not distribute changed native binaries. Production inference enablement, authenticated Founder behavior, mobile delivery and actual promoted-checkpoint learning must each be reported from their own execution evidence.

## Frozen candidate evidence

Source `47532284be391a0eeb98b27224452fa3dd4a514d` is bound to production base `144567d7a39ea76d50f3d5efd3aae08f3595f85e`. The solution build before the source-only contract correction passed with zero warnings/errors; the final canonical test-project build also passed with zero warnings/errors. The three affected workflow contracts passed. Validator tests report 34 passed and one existing optional captured-run skip; that skip is not release evidence.

The focused configured-HTTPS model run executed 17 cases in 6 minutes 41 seconds: six passed, eleven failed, zero skipped. The private TRX SHA256 is `6478f9a170e6aaaa4353f08942e330fbb4b4daf9a8fb11403802cb84a6f3cf48`. The expected complete roster has 2,935 cases, including repeated theory display names, eleven exact permitted failures and four unchanged opt-in skips. Supplying the focused TRX to the real release validator correctly returned `Rejected: Full result roster differs from approved baseline`; only the forthcoming complete workflow run can satisfy acceptance. This document does not report that workflow, production deployment or authenticated Founder verification as completed.

Release-trigger reconciliation: the first reviewed policy push (`6d6da8fb`) did not start a workflow because PR #123 had been closed at 2026-09-14T01:09:38Z. Under the owner's subsequent explicit production authorization, the same PR was reopened. This policy-evidence follow-up synchronizes the already-open PR, which is the existing workflow's sole supported trigger. Opening/reopening alone does not deploy; no alternate trigger or direct production update was introduced.
