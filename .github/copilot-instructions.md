# MASTERAPP Repository Instructions for AI Coding Agents

These instructions govern all AI work in `MYLEGND/masterapp`. They define operating discipline, not a substitute for inspecting the current repository.

## Authority and evidence

- The repository source, executable configuration, migrations, tests, workflows, durable contracts, and observed runtime evidence are authoritative.
- Do not rely on an old architecture summary, a previous conversation, a comment, a test name, or a presumed project layout without verifying it against the current branch.
- Label material conclusions `CONFIRMED`, `INFERENCE`, or `UNVERIFIED`.
- Never claim a path was inspected, a command ran, a test passed, a deployment completed, or production behavior was proven unless direct evidence supports that exact claim.
- Separate code defects, test defects, configuration/deployment failures, production-data failures, product decisions, and unverified hypotheses.

## Branch and change safety

- `production` is the default and release branch. Never edit, commit, force-update, merge, or deploy it directly.
- Before acting, inspect the current branch, base SHA, remote state, commit history, working tree, active PRs, and applicable repository instructions.
- Use a dedicated, descriptively named branch created from the explicitly approved base SHA.
- Preserve unrelated, uncommitted, unpushed, generated, and agent-owned work. Stop if scope overlaps active work or ownership is unclear.
- Keep each repair branch bounded to one coherent authority. Do not let multiple agents edit the same branch or overlapping logic simultaneously.
- Do not push, open/retarget/merge a PR, deploy, run migrations, mutate production data, change secrets, publish mobile builds, or change store/cloud configuration without the required explicit authorization.
- Never weaken or bypass a gate to make a build or test pass.

## Required investigation before implementation

1. Restate the requested outcome, constraints, exclusions, and proof standard.
2. Read all applicable instruction files.
3. Inventory the real solution/project topology and canonical build, test, workflow, deployment, database, web, iOS, and Android paths from the current tree.
4. Trace the complete authenticated runtime path for the behavior in scope.
5. Search every caller, interface, implementation, dependency registration, configuration key, persistence path, serializer, event contract, test double, workflow, and platform consumer relevant to the decision.
6. Identify the single existing authority making the first incorrect decision.
7. Search for duplicate, competing, shadow, fallback, override, stale, hardcoded, or platform-specific logic.
8. Define the smallest coherent repair, regression risks, rollback boundary, and exact acceptance criteria before editing.

## Architectural invariants

- Fix decisions at their authoritative source; do not mask defects in controllers, prompts, clients, tests, logging, or UI.
- Prefer consolidation or deletion of competing logic over another override.
- Do not create a new service, workflow, persistence path, inference path, provider path, validation authority, or abstraction until the existing authority has been inspected and proven unable to own the requirement.
- Preserve authentication, authorization, Founder/actor isolation, evidence provenance, contradiction handling, production eligibility, response authority, provider attribution, cancellation, idempotency, privacy, and cross-platform contract meaning.
- The server is authoritative for inference, evidence, authorization, provider policy, native-only enforcement, and response authority. Web, iOS, and Android must not reimplement those decisions.
- No prompt-specific, greeting-specific, answer-specific, fact-specific, President-specific, language-specific, user-specific, or test-fixture-specific production behavior.
- Do not solve timeout failures by making operations unbounded.
- Cancellation must not leave background mutation running. Retries must be bounded and idempotent.
- Telemetry must be actionable and correlated without exposing secrets, tokens, or raw sensitive content.

## LEGEND intelligence invariants

- Native-only execution must create and invoke zero OpenAI or other external conversational/provider clients. It must be server-enforced, explicitly attributed as native, and fail closed when governed evidence is insufficient.
- Provider output must never masquerade as native LEGEND reasoning, evidence, or articulation.
- Provider/research output may enter the existing governed candidate, evaluation, provenance, contradiction, eligibility, and promotion process only through its authoritative path. It is not automatically canonical proof.
- Reasoning, discourse, research, learning, and realization must use existing governed authorities and preserve proof-relevant lineage. Do not add a parallel evidence-to-answer system.

## Testing and proof

- Use the current repository and workflow definitions to discover canonical commands; do not assume old commands are still valid.
- Start with focused deterministic tests, then run only the broader stages required and authorized for the change.
- Record exact commands, environment category, configuration state without secrets, duration, counts, exit codes, skips, and artifacts.
- A missing environment variable, credential, database, provider, endpoint, fixture, device, or service is `NOT_CONFIGURED` or `BLOCKED`, never a pass.
- A silent return, caught-and-suppressed exception, conditional non-execution, stale artifact, or mocked substitute cannot satisfy a required gate.
- Unit, mocked/in-process, SQL-backed, provider-backed, authenticated live-production, and cross-platform/device evidence are distinct. Never substitute one layer for another.
- Match source SHA, built artifact SHA, workflow SHA, deployed SHA, and observed runtime SHA before making production claims.
- Tests must include forbidden behavior and adversarial cases, not only success paths.

## Custom-agent workflow

Repository custom agents are defined in `.github/agents/`.

Use them in this order:

1. `MASTERAPP Chief Architect` maps the system, failure ledger, repair groups, dependencies, branch/file boundaries, and acceptance criteria.
2. The relevant specialist implements only the assigned bounded repair group on its own branch.
3. `MASTERAPP Verification Engineer` independently validates the exact candidate SHA and challenges false-green evidence.
4. `MASTERAPP Release Reviewer` independently reviews the complete diff, evidence, security, governance, compatibility, rollback, and production readiness.
5. Only after those gates may Founder authorization be requested for the exact commit SHA.

Verification and Release Review must remain independent of the original implementation. A new commit invalidates prior exact-SHA approval and requires the affected gates to run again.

## Required handoff

Every change handoff must include:

- base and candidate SHA;
- scope and changed files;
- confirmed root cause and authoritative owner;
- diff summary and removed competing logic;
- exact tests executed and results;
- tests skipped, blocked, mocked, not configured, or not run;
- security, data, concurrency, cancellation, privacy, compatibility, migration, deployment, and rollback risks;
- live proof still required;
- explicit recommendation or stop condition.

Do not describe work as complete, fixed, flawless, all-green, or production-ready beyond what the recorded evidence proves.
