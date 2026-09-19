# GitHub candidate validation contract

GitHub branches and GitHub-hosted Actions are the execution authority. This
candidate helper extends the existing Founder software remediation review; it
does not create an orchestrator, dispatch a job, approve a change, merge a branch,
deploy, or call an inference provider. Containers work is paused and is not an
acceptance gate. No Containers permission is implied by historical sandbox work.

## Existing authority and the missing validation job

`AgentPortal/Services/FounderSoftwareRemediationService.cs` already verifies the
requested PR head, configured base, open state, and observed checks. Its check
receipt explicitly denies protected merge authority. The batch/completion partials
retain the reviewed revision, published branch/tree, production workflow evidence,
and live deployment provenance. Those services remain the authority.

The existing `agentportal-production-deploy.yml`,
`legend-production-readonly-diagnostic.yml`, and
`all-intentional-direct-release-20260918.yml` use production environments,
credentials, or OIDC. They are not suitable unchanged for candidate execution.
A privileged review must introduce a trusted, fixed validation job before this
helper can run there. The helper is not a substitute for that review.

GitHub recommends minimum token permissions, immutable action references, and
care with privileged workflows that check out untrusted code. Candidate validation
must not use `pull_request_target` or a privileged `workflow_run` job to execute
the candidate. See [GitHub secure use reference](https://docs.github.com/en/actions/reference/security/secure-use).

## Request, authority, and exact identity

The existing authenticated remediation authority supplies this exact JSON shape:

```json
{
  "version": "legend-candidate-validation.v1",
  "repository": "owner/repository",
  "baseSha": "<40 lowercase hex>",
  "candidateSha": "<40 lowercase hex>",
  "patchSha256": "<64 lowercase hex>",
  "requestId": "<existing request id>",
  "profile": "cloudflare-contracts",
  "trustedWorkflowSha": "<40 lowercase hex>",
  "approvalActionDigest": "<64 lowercase hex>"
}
```

Unknown fields are rejected. There are no caller commands, test filters, runner
labels, dependency overrides, secret names, or credential inputs. The initial
profile requires `trustedWorkflowSha` to be an ancestor of `baseSha`, and `baseSha`
to be an ancestor of `candidateSha`. The exact base can be the accumulated staging
batch head that the existing authority selects with compare-and-swap checks;
the candidate cannot choose a different base. Both checkouts must match their exact SHA and be
clean, with no untracked or ignored files. The candidate must be detached and
descend from the baseline. Required test files must remain present.

`approvalActionDigest` is a binding supplied by the existing authority, not proof
of consent by itself. Before dispatch, that authority must verify fresh approval
and ensure the actual candidate contains exactly the approved full-file changes
against the trusted baseline. A proposal's canonical `changeSetSha256` over its
immutable path/content replacements is distinct from `patchSha256`. Neither
digest may be substituted for the other. The helper verifies Git identity and
diff bytes; it does not verify the approval signature or mint permission.

Compute `patchSha256` from the exact binary stdout of:

```sh
git -c core.quotePath=true diff --binary --full-index --no-ext-diff --no-textconv --no-renames --src-prefix=a/ --dst-prefix=b/ "$BASE_SHA" "$CANDIDATE_SHA" --
```

The digest covers the exact approved `baseSha..candidateSha` patch. Privileged
path and file-mode checks also cover the cumulative `trustedWorkflowSha..candidateSha`
tree difference, preventing earlier staged changes from bypassing those checks.
The canonical digest includes complete blob identities and binary differences.
No newline conversion, decoded text, rename detection, external diff, or textconv
is applied to those bytes. The helper runs this command independently.

## Fixed profile and protected changes

The first profile accepts changes only to three runtime implementation modules
(`adapter.mjs`, `orchestrator.mjs`, `reliability.mjs`), the exact Cloudflare .NET
transport and shared LegendConnect contract, Cloudflare test/fixture files, five
named .NET test classes, and Markdown in this documentation directory. The exact
allowlist is in the trusted helper. Changed entries must be regular, nonexecutable
Git files; symlinks and submodules are rejected.

Workflow definitions, scripts/controller code, build targets/project files,
dependency manifests/locks, registry and deployment configuration, signing,
authorization, budget implementation, and other unknown paths require privileged
review. A foundational branch that changes these paths cannot qualify itself
through this narrow profile. Reviewed baseline changes must land through the
existing protected authority before subsequent narrow candidates execute.

The fixed tests are:

- Nine pure Node test files covering runtime/schema/qualification fixtures,
  request authentication, approval interop, tool broker, governance, and the Worker
  entrypoint with simulated bindings. There are no live model calls, Wrangler
  operations, npm scripts, or workerd provisioning in this profile.
- `.NET` tests in `LegendCloudflareTransportTests`,
  `LegendCloudflareToolCallbackTests`, `FounderSoftwareRepairBatchTests`,
  `FounderSoftwareRepairCompletionTests`, and `FounderRemediationRevocationTests`.
  `LegendCloudflareLiveQualificationTests` is excluded. Restore and build still
  execute trusted baseline project targets and candidate source code; therefore
  the entire job must have no production credentials.

Node and .NET must both discover passing tests; skipped/failed results or any
missing .NET class fail the helper. This is focused contract validation, not full
application, native iOS/Android/macOS, language-quality, or live-model acceptance.
Use the existing `scripts/diagnostic-project-impact.py` when broader validation is
needed; do not infer native coverage from this profile.

## Trusted workflow integration

The lead owns workflow YAML and .NET integration. Dependencies are Python 3
standard library, Git, setup-node 24, Docker, and the pinned official .NET 10 SDK image. No Python/npm install is needed
for this helper. Pin reviewed action revisions and SDK versions in the trusted
workflow; version changes need privileged review.

The required job properties are:

1. Use a fresh standard GitHub-hosted Linux x64 runner for this profile. This
   Docker profile rejects macOS; a separately reviewed hosted macOS profile is
   needed for native validation. It must never fall back to the Founder's Mac.
   Capacity/cost checks precede dispatch; this helper does not authorize spend.
2. Use job permissions `contents: read` only, no `id-token: write`, production
   environment, secrets inheritance, deployment credentials, caches, or writable
   repository token. Never expose the privileged dispatch credential to the job.
3. Check out the trusted revision at `trusted/` and exact candidate at `candidate/`,
   with `persist-credentials: false`, sufficient history to verify ancestry, and
   no submodules. Fetching source is a trusted preparation step. Candidate scripts
   must not perform checkout or bootstrap dependencies with credentials.
4. Create the authority-approved request under `$RUNNER_TEMP`, outside either
   checkout. Do not build JSON by interpolating untrusted strings into shell code.
   Invoke only the script from the trusted checkout. The helper compares
   `repository` with the workflow's `GITHUB_REPOSITORY`.
5. Set a fixed job timeout slightly above the helper's 15-minute command deadline
   (for example, 20 minutes including setup). One approved run only; no automatic
   retries, expanded matrix, or dispatch loop. The job timeout/ephemeral hosted
   runner remain the external cleanup boundary for hostile candidate processes.
6. Upload only the named receipt/log/TRX files from `$RUNNER_TEMP/legend-validation`
   using a pinned action, short retention, and failure upload enabled. Do not
   upload HOME, dependency caches, the runner filesystem, or arbitrary candidate
   paths. Treat every artifact as untrusted data.

From the workspace root:

```sh
python3 -B trusted/scripts/legend-candidate-validation.py verify \
  --request "$RUNNER_TEMP/request.json" \
  --trusted-root "$GITHUB_WORKSPACE/trusted" \
  --candidate-root "$GITHUB_WORKSPACE/candidate" \
  --output "$RUNNER_TEMP/legend-validation/plan.json"

python3 -B trusted/scripts/legend-candidate-validation.py run \
  --request "$RUNNER_TEMP/request.json" \
  --trusted-root "$GITHUB_WORKSPACE/trusted" \
  --candidate-root "$GITHUB_WORKSPACE/candidate" \
  --output "$RUNNER_TEMP/legend-validation/receipt.json"
```

`run` verifies the exact history and paths again and requires GitHub-hosted Linux
run/attempt identities. It uses the official .NET SDK image pinned by digest in
the helper and verifies that the setup-node24 binary executes inside it. Restore
has network access but sees only the trusted source. Candidate tests run in
non-root containers with no network, read-only source/root filesystems, no host
credentials/socket/parent-evidence mounts, dropped capabilities, no new
privileges, and fixed CPU/memory/PID/file/time limits. State uses bounded temporary
storage; cleanup is awaited on success and failure. Logs and XML are bounded
before reading; only a regular bounded TRX file is copied out. Test-file and
fixture edits require privileged review, so a normal candidate cannot delete or
weaken the trusted baseline tests.

Candidate reports remain untrusted statements about correctness. The protected
parent receipt and GitHub run identity demonstrate what was executed, not that a
candidate cannot mislead its own tests. Independent code/test review remains a
release requirement. Container compatibility and cleanup still need a real
hosted run; synthetic tests alone do not establish those facts.

## Evidence and acceptance

The receipt repeats the request, exact-patch `changedFiles`, and cumulative
`cumulativeChangedFiles`, and adds `runId`, `runAttempt`,
`runnerOS`, `conclusion`, `testCounts`, and `artifactSha256`. The latter is SHA256
of UTF-8 JSON for the receipt excluding `artifactSha256`, with keys sorted and
separators `,` and `:` and no trailing newline. It detects byte/content mismatch;
it is not a signature or authenticated test attestation. `mergeAuthorized` and
`deploymentAuthorized` are always false.

The existing authority must independently retrieve GitHub run/job evidence:
repository, immutable trusted workflow path/revision, expected event, exact
approved candidate input, run/attempt IDs, required job conclusion, and artifact
identity. Arbitrary matching check names, a candidate-written success JSON, or
counts alone do not authorize merge. Keep artifact parsing in a trusted context
that never executes artifact contents. Protected merge/deployment remain with
the existing reviewed production workflow and completion checks.

Local validation: `python3 -B scripts/test-legend-candidate-validation.py` runs
synthetic temporary Git repositories and fabricated test-report fixtures. These
tests establish helper behavior only. No hosted job, .NET build, production
credential, inference call, or paid resource is used by that test suite.
