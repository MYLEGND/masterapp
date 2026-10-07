# LEGEND Production Deployment

The protected `legend/approved-changes` lifecycle is the **sole production deployment authority** for MASTERAPP.

Production application publication must flow through:

`.github/workflows/all-intentional-direct-release-20260918.yml`

No local shell script, developer workstation command, diagnostic workflow, manual Azure publication, alternate migration command, or model/tool permission may substitute for that authority.

## Canonical flow

1. Start from the exact current `legend/approved-changes` head on an isolated branch.
2. Merge only after the trusted lifecycle accepts the exact candidate head and every required validation gate is green or canonically reused.
3. Derive affected applications from the single release-target authority in `scripts/validation-resume.py`.
4. Reuse the exact validated immutable package when its content identity remains valid; otherwise build only the invalidated package components.
5. Prepare one immutable all-target transaction before any application publication.
6. Submit selected Azure application targets concurrently through `scripts/deploy-approved-app.py`.
7. Treat upload acceptance and first-pass exact-live observation as non-terminal publication evidence. They must never be labeled deployment success; only durable receipt reconciliation plus exact live runtime provenance can establish that truth.
8. Never replay an ambiguous upload. Preserve successful siblings and reconcile only the unresolved target state.
9. Finalize durable receipts through the single bounded finalizer in `scripts/deploy-approved-app.py`.
10. Require live runtime provenance and the canonical post-publication checks before the lifecycle can close successfully.

## Timing invariants

The release control plane owns these bounds:

- application publication reconciliation: 420 seconds maximum per target, with targets running concurrently;
- durable receipt finalization: 90 seconds per attempt, at most three attempts;
- finalization retries only unresolved targets;
- production release job fail-safe: 30 minutes;
- successful publication evidence is preserved and must not be replayed merely because a sibling is unresolved.

These values are protected by the trusted-base control-plane integrity guard. A candidate that attempts to restore legacy timing, duplicate finalization, serialized publication, alternate deployment authority, or mutable workflow-generation behavior must fail before merge.

## Database and configuration changes

Production migration and configuration work is part of the same governed release transaction. Only the canonical release child authorities may perform those writes. Developer/local database utilities are not production authorities.

## Diagnostics

Production diagnostic workflows are read-only observers. They may inspect runtime state, logs, metrics, provenance, and provider status, but they may not upload executables, change application files/settings, restart applications, or publish application bytes.

## Failure and recovery

- Fail closed when live revision, package identity, provider state, or durable evidence is ambiguous.
- Preserve exact successful child receipts.
- Resume only the failed or unresolved boundary when canonical evidence proves reuse is safe.
- Do not create a second release workflow, alternate deployment script, emergency publication shortcut, or hidden model/tool bypass.
- A control-plane repair must itself pass the trusted protected-branch integrity guard and required validation before becoming authority.

The protected branch, canonical release workflow, immutable transaction evidence, and live provenance together define production truth.
