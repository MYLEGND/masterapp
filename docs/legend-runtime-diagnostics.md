# LEGEND runtime diagnostics

The existing `scripts/run-legend-production-shadow.sh` runner and
`.github/workflows/legend-production-readonly-diagnostic.yml` workflow own these
diagnostics. They do not deploy, approve a release, teach expected answers, or
replace the application's decision authorities.

## Select the boundary being tested

| Scope | Executes | What a successful result establishes |
| --- | --- | --- |
| `canonical_matrix` | Existing native conversation matrix over guarded production SQL; isolated local conversation persistence | The exercised native requests satisfied their assertions on the specified candidate and production evidence. Includes a request with no declared source language. |
| `observation` | Existing production section, corpus, and direct-native observations, including populated English machine-learning lifecycle records | The bounded data/service operations worked. This is not an authenticated HTTP or browser test. |
| `provider_resources` | Existing OpenAI catalog canary plus registered Azure translation and governed research probes | Actual provider boundaries executed under the selected policy. Their observation database is local and temporary. This does not prove native intelligence or production SQL access. |

Native-only policy intentionally forbids external assistance. A policy rejection
in a native-only control is expected; the corresponding provider-enabled probe
must independently execute. OpenAI catalog acceptance does not prove reasoning
quality. Azure returning a translation does not establish its semantic quality.
Research can correctly report insufficient evidence or unresolved conflict; that
is different from answering the question.

## Run an exact candidate

Build the clean requested revision before running a scope:

```bash
export LEGEND_VALIDATION_CANDIDATE_SHA="$(git rev-parse HEAD)"
export LEGEND_VALIDATION_WORKFLOW_SHA="$LEGEND_VALIDATION_CANDIDATE_SHA"
export LEGEND_VALIDATION_NONCE="$(python3 -c 'import uuid; print(uuid.uuid4())')"
export LEGEND_VALIDATION_SCOPE=canonical_matrix
export LEGEND_PRODUCTION_OBSERVATION_REQUIRED=true
dotnet restore AgentPortal.Tests/AgentPortal.Tests.csproj
dotnet build AgentPortal.Tests/AgentPortal.Tests.csproj -c Release --no-restore \
  -p:SourceRevisionId="$LEGEND_VALIDATION_CANDIDATE_SHA"
bash scripts/run-legend-production-shadow.sh
```

The existing validation environment supplies the selected credentials. Never
paste credential values into a report or replace the SELECT-only principal with
the application's write-capable connection. A missing configuration result is
`NOT_CONFIGURED`, not a successful production test. Source revision, executed
assembly revision, run identity, receipt freshness, and actual test execution
must agree.

## Read a failure

Start with `diagnostics/legend-shadow/summary.json`. The detailed SQL/native
evidence is in `observation.json`; provider scopes produce `resource-openai.json`,
`resource-azure.json`, and `resource-research.json`. Raw test logs remain under
the runner's private directory and must not be published as sanitized evidence.

The native evidence records the authority method and source file, stage,
observed outcome/reason, elapsed time, and observed language, graph, slot, and
evidence counts. Missing counts remain unobserved; they are not silently zero.
SQL evidence records normalized query fingerprints, outcomes, durations, and
available exception/error metadata without SQL text, parameter values, or row
contents. A successful reader-open event does not mean row materialization
succeeded. Captured query-iteration failures must also be examined.

Per-case resets preserve cumulative isolation accounting. Bounded records expose
dropped counts and truncation. A later successful operation must not erase a
captured failure. An optional candidate prefilter failure followed by the normal
full scan is recorded without being misidentified as a later terminal failure.

Use `FailureDiagnosis` together with `DiagnosticEvidence`:

| Observed result | Required next action |
| --- | --- |
| Missing selected configuration | Configure that named input, then rerun the exact candidate. No inference was exercised. |
| SQL command or row-materialization failure | Use the captured error, query fingerprint, and owning stage to reproduce the failing query. Do not infer a missing column or timeout cause from an HTTP 503 alone. |
| No matching anchors, no eligible nodes, or missing slot declarations | Inspect the recorded admission prerequisite and teach through the existing governed curriculum path. Do not insert a held-out answer. |
| Unknown meaning components or unsupported graph relations | Repair the demonstrated grounding/relationship prerequisite; later calculation or planning has not yet been tested by that failure. |
| Applied processing bound exceeded | Use the observed count and actual bound to locate excess work. Do not raise a limit merely to make the test green. |
| Provider policy blocked | Check the request's intended policy. Never count external output as native success or silently promote it to canonical knowledge. |
| Insufficient or truncated diagnostics | Preserve the uncertainty and capture the missing boundary before claiming an exact root cause. |

An observed failing method is not automatically a proven code defect. Diagnostic
reports deliberately separate observed failure from a proposed repair. A repair
is verified only when the original reproducer and relevant regression tests pass
on the repaired candidate. There is no automatic "exact fix" claim.

## Verification of the diagnostic itself

`LegendProductionDiagnosticEvidenceTests` exercises actual EF interception and
deliberate failures, including swallowed errors, row materialization, privacy,
bounded retention, immutable snapshots, and correct authority attribution.
`scripts/test-legend-production-shadow.py` adversarially checks the runner's
evidence contract. These local tests validate instrumentation and evidence gates;
they do not count as live production SQL or provider execution.

The existing held-out operation matrix now attaches the same runtime capture to
new JSONL records. Historical records cannot acquire observations retroactively.
Authenticated HTTP behavior, production latency, memory persistence, and the
established deployment gates still require their own actual execution.
