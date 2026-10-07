# MASTERAPP instructions for AI coding agents

`AGENTS.md` is the canonical repository-level AI engineering operating contract.
Read it **before** acting. If this file, a custom agent file, an old document, a
conversation, or a historical summary conflicts with `AGENTS.md` plus current
executable repository truth, stop and resolve the conflict rather than choosing the
more convenient instruction.

This file is intentionally thin so mutable architecture facts do not drift across
multiple instruction copies.

## Mandatory current-system truths

- `legend/approved-changes` is the sole protected Git release authority.
- There is no second mutable `production` release branch or branch-promotion lifecycle.
- Production truth is established by canonical release evidence and exact live provenance.
- Follow `Docs/releases/branch-lifecycle.md` and `DEPLOYMENT.md` for current release
  mechanics; never reconstruct release behavior from memory.
- Ordinary application/product work must not modify the protected release-control plane.
  AI may inspect, diagnose, recommend, and prepare a proposed release-control change, but
  protected release-control mutation requires explicit Founder approval of that specific
  control-plane change.
- Never add a second deployment workflow, manual Azure publication path, alternate
  finalizer/recovery authority, shadow validation system, or bypass status.
- A validation or publication failure is not permission to adapt the release system to
  the candidate. Resolve the actual failure class through the existing canonical path.

## Required operating method

1. Verify the exact current protected base and active work.
2. Read `AGENTS.md` and all domain-specific executable/canonical contracts that own the
   requested behavior.
3. Restate the user outcome, protected constraints, and proof standard.
4. Trace the end-to-end authority and impact set before editing.
5. Search for duplicate, shadow, fallback, override, stale, hardcoded, or platform-specific
   competing logic.
6. Consider multiple solutions and choose the one that best satisfies the user while
   reducing authority count and future drift.
7. Implement on one bounded isolated branch; never edit the protected branch directly.
8. Preserve security, authorization, privacy, owner isolation, event/action identity,
   measurement lineage, data compatibility, cancellation/idempotency, and cross-platform
   contract meaning.
9. Run focused deterministic proof first, then only the affected authorized validation
   lanes. Test forbidden behavior as well as success.
10. Hand off exact SHA/evidence honestly. Do not self-promote a local or mocked result into
    production proof.

## Creativity

Do not interpret governance as a request for conservative or mediocre implementation.
Within the verified authority boundary, actively improve product quality, design,
performance, usability, architecture, maintainability, and developer/operator experience.
Protected invariants define what must remain true; they do not dictate a single creative
solution.

## Custom agents

Custom agents live in `.github/agents/`. Their specialist instructions refine role and
scope but do not override `AGENTS.md`.

Use independent roles where applicable:

1. Chief Architect maps authority, impact, alternatives, risks, and acceptance criteria.
2. Specialist implements the bounded canonical change.
3. Verification independently proves or rejects the candidate.
4. Release Reviewer independently challenges architecture, evidence, security, and
   production readiness.
5. Founder approval is requested only where the governing authority requires it, and must
   be bound to the exact consequential action/revision.

Any new commit invalidates exact-SHA review/approval evidence to the extent defined by the
canonical validation and release authorities.
