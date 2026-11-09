# MASTERAPP in the existing cloud workspace

Use the existing permanent Codespace and its universal image. This procedure
prepares repository dependencies and runs portable checks. It does not create
another environment, change spending, start an application, migrate a database,
open a public port, push code, or deploy.

The current preparation checkout is `/workspaces/masterapp-cloud`, on
`isolation/cloud-workspace-readiness-20261109`, in the existing permanent
Codespace `bookish-fiesta-97pj77j5vpwj3pjv6`. It was admitted from release commit
`6eed2f4ab7adad92a07f999295582dcb00355181`. These identify this preparation task;
future tasks must use their current admitted branch and exact native state.

## 1. Preserve the current task and native Git state

Follow `AGENTS.md` and the existing `scripts/sync-published-checkout.py` session
preflight before implementation. Use the authorized branch, accepted base, current
native inventory, and existing recovery evidence. Never replace local-only work
with a remote snapshot. The helper below does not replace source admission or the
existing sync/finish process.

## 2. Inspect and prepare

Run from the admitted checkout:

```sh
python3 scripts/dev-workspace.py doctor
python3 scripts/dev-workspace.py bootstrap
```

The existing Codespace must have the selected .NET 10 SDK, Node 24/npm, Python
3.9+, native Git, and at least 6 GiB free. The helper does not replace SDKs or the
universal image. It restores the repository's EF tool and solution packages,
installs the existing locked website dependencies without npm lifecycle scripts,
and installs Playwright 1.65.0 plus its matching Chromium in a private workspace
cache. The npm Playwright version and package integrity are checked before its
CLI runs.

If Chromium reports missing Linux system libraries, use the explicit supported
installation step and rerun the affected browser check:

```sh
python3 scripts/dev-workspace.py bootstrap --install-browser-deps
```

`bootstrap` is restricted to the existing Linux Codespace and refuses on macOS.
No Linux installation runs merely because these files are pulled onto the Mac.
After cache loss or a Codespace rebuild, inspect prerequisites and rerun bootstrap
as needed; no custom image or lifecycle hook is introduced. Existing lockfiles
remain authoritative. Missing package/network
access is a dependency to resolve through the existing settings, not a reason to
change budgets or access controls.

### Codex sign-in

The Codespace setup installs `@openai/codex@0.167.0`. Check the actual installation
with `codex --version`, then run `codex login --device-auth` and complete the
ChatGPT-account sign-in on your phone. The account owner completes any authentication prompt;
do not paste credentials into chat or switch this workflow to API-key billing.
After sign-in, start `codex` from the admitted checkout. If a rebuild removed the
CLI, restore the same approved version with the command already used in this
Codespace:

```sh
npm install --global @openai/codex@0.167.0 --no-audit --no-fund --registry=https://registry.npmjs.org
```

This is an explicit restore command after a rebuild, not an automatic bootstrap
step. The workspace helper does not manage or replace the CLI.

## 3. Run the existing checks

```sh
python3 scripts/dev-workspace.py check
```

This builds **MASTERAPP.sln** in Release and runs the .NET suite with the explicit
CI-only exclusion below, the existing website/jsdom and renderer tests, and the
existing synthetic Playwright layout test. **MasterApp.slnx is incomplete for
this purpose:** it omits Protect, Parfait, and the test project.

Run only the relevant checks when valid evidence already covers the others:

```sh
python3 scripts/dev-workspace.py check --scope website
python3 scripts/dev-workspace.py check --scope browser
python3 scripts/dev-workspace.py check --scope dotnet --filter 'FullyQualifiedName~LocalProtectPublicationTests'
```

The website scope builds current assets with
`npm run build --prefix Legend-Website` before its tests, including when selected
on its own. The .NET scope always builds the current candidate first. A test filter adds a
restriction; it does not remove the CI exclusion. A zero-test .NET run is a
failure. Failures stop the selected sequence and are not automatically retried.

For solution compilation in Linux Codespaces, the helper uses
`DOTNET_PROCESSOR_COUNT=1` and `DOTNET_GCHeapHardLimit=0x100000000`. This follows the
existing [constrained-build approach](https://github.com/MYLEGND/masterapp/blob/6eed2f4ab7adad92a07f999295582dcb00355181/docs/parfait-canonical-transition/business-scoped-payments.md#L62)
with values verified in this workspace. `Infrastructure/Infrastructure.csproj`
compiled successfully in Release on the existing 8 GiB Codespace with this
profile and all source and analyzers retained. Full solution and test results
require their own completed checks. The heap setting limits each .NET process's
GC heap and bookkeeping to 4 GiB; it does not limit
total process RSS or aggregate workspace memory. Solution builds also disable
MSBuild node reuse with the existing `-nr:false` convention. These compiler
controls apply only to the solution build. They do not establish
the cause of a compiler exit code 143.

### Exact CI-only exclusion

The local .NET run excludes:

`AgentPortal.Tests.CrmHistoryTests.HostedSyntheticHistorySupportsDesktopMobileKeyboardAndProtectedEndpoints`

That existing test requires the genuine admitted GitHub Actions runner, its event
metadata, ChromeDriver and Google Chrome. The native-component browser rendering
gate is likewise left on its existing CI path. The helper does not fabricate
`GITHUB_ACTIONS`, `RUNNER_OS`, event metadata or CI results. It sets only the
existing `GITHUB_WORKSPACE` repository-path input required by source-contract
tests whose outputs live outside the checkout. This is not a CI identity claim.

The portable Playwright test uses synthetic markup, production styles/controllers,
and intercepted requests. Its passing result establishes its listed viewport and
interaction checks, not full application, database or provider acceptance.

## Evidence and local files

In Linux Codespaces, each checkout gets an owner-checked private directory at
`/workspaces/masterapp-dev-<uid>-<checkout-hash>`, outside Git checkouts. Other
environments keep a per-checkout directory under the operating system's temporary
directory. .NET build outputs, Playwright downloads, TRX results, bounded redacted
logs and a per-run `summary.json` go there. The summary records the exact HEAD,
local tracked-change status, tool versions, requested scope and stage results.
It always leaves full application/production readiness unverified.

The .NET test process receives `TMPDIR=<run-output>/tmp`, a fresh owner-only
directory with mode `0700`. Fixture key paths derived as `../AgentPortal/...`
then stay inside the same private run output. The build, bootstrap, website and
browser processes retain their existing temporary-directory environments.

The cloud cache and completed evidence persist across normal workspace stops and
starts when `/workspaces` is preserved. This workspace-local state is not an
independent backup; retain accepted evidence through the existing task checkpoint
and preservation process. Cache loss or a rebuild may require bootstrap again.

Normal npm dependency directories are generated under `Legend-Website` and
`tests/website`; the existing website compiler can also generate its normal
ignored `Legend-Website/dist` output. The browser fixture gets a temporary `tests/layout/node_modules`
link to the private cache; only the link created by that invocation is removed.
An existing link/directory is preserved and reported as a conflict. Package
manifests and lockfiles are not changed. The root `.gitignore` has scoped rules
for `/tests/website/node_modules/` and `/scripts/__pycache__/` to exclude generated
test dependencies and Python bytecode. Inspect Git status and commit only the
reviewed source scope; never add generated dependencies.

Provider/application credential variables and CI identity are not passed to
child commands. Runtime paths, proxy/CA settings and the original home directory
are preserved for existing development tools. This is environment hygiene, not a
replacement for network isolation or an assertion that arbitrary repository tests
cannot read local files. Do not run unreviewed code with production credentials
or raw customer data present. Logs are bounded to approximately 1 MiB per step,
with first/last portions retained when truncated; command output is not dumped to
the console. Protected values and common credential patterns are redacted.

## Full localhost application startup remains a separate prerequisite

This helper intentionally has no app-start, migration or deployment command.
Before enabling real application startup, establish these exact prerequisites:

- **Disposable database targets and synthetic fixtures.** AgentPortal and
  ClientApp require SQL Server. Protect Development rejects SQL Server and uses
  SQLite; Parfait supports SQLite. Do not relabel the environment to evade that
  protection. A SQLite pass does not prove SQL Server or cross-app behavior.
- **Migration proof.** ClientApp automatically migrates its Development database.
  Verify the disposable target before starting it. AgentPortal must pass strict
  schema readiness; non-strict startup can tolerate an unreachable database and
  is not a successful database test. Use the existing migration owner and gates.
- **External-effect control.** Real hosted workers exist for voice, notification,
  calendar, directory, billing and other functions. Development plus an empty
  database does not establish that they cannot contact providers. Use verified
  existing controls or an isolated runtime with blocked provider egress. Do not
  import production credentials or customer records.
- **Local identity, certificate and publication setup.** Preserve authentication.
  Use admitted test identities and a localhost development certificate. Protect's
  existing `LocalWebsite` mode requires a matching absolute SQLite path and a
  valid synthetic owner/publication, and binds `https://localhost:1113`. Capture
  mode accepts loopback GET/HEAD only; document bootstrap exits after publication.
  These are useful rendering checks, not proof of form submissions or CRM delivery.
- **Existing fixture availability.** The last-documented later host is
  `/private/tmp/legend-parfait-native-host` at `https://localhost:15432`; its earlier
  predecessor was `/private/tmp/legend-store-browser-host`. The actual host source
  was not recovered in the Codespace after inspection of the uploaded archive,
  so cloud recovery remains **UNRESOLVED**. This does not establish the current
  Mac state or whether other backups exist. Its behavior cannot be claimed here
  until the actual fixture is recovered and admitted through the existing owner.
  Reuse the existing
  `scripts/migration-probe-package.py test-contract` synthetic SQL proof where
  applicable; do not build a competing migration test system.
- **Native and release verification.** iOS/Android device checks, provider tests,
  independent exact-candidate acceptance, required CI/admission and postdeployment
  live verification remain separate. This setup does not grant deployment authority.

## Return to the Mac

Preserve and push only through the existing reviewed session/finish path, then
use the existing safe synchronization procedure on the Mac. Do not force-pull,
reset, or overwrite unpublished Mac changes. To inspect the published setup in a
separate worktree while leaving the current Mac checkout untouched:

```sh
git -C /Users/zacowen/MASTERAPP fetch origin refs/heads/isolation/cloud-workspace-readiness-20261109:refs/remotes/origin/isolation/cloud-workspace-readiness-20261109
git -C /Users/zacowen/MASTERAPP worktree add --detach /Users/zacowen/MASTERAPP-cloud-review refs/remotes/origin/isolation/cloud-workspace-readiness-20261109
git -C /Users/zacowen/MASTERAPP-cloud-review rev-parse HEAD
```

Run these only after the exact accepted cloud head is verified on the remote.
Compare the final command with that independently recorded head. The commands
have no force/reset option and stop if the destination already exists. A fetch
can update remote-tracking references; the comparison with the independently
accepted head is the guard against an unexpected remote revision. Stop and
reconcile any mismatch before using the review worktree.
The new worktree is detached for review, not a competing implementation writer.
Admit any later Mac editing through the existing handoff and branch procedure.
This preserves the original Mac worktree, including its unpublished files. The
procedure was checked with an isolated Git fixture; the actual Mac checkout,
credentials and unpublished state have not been verified from the Codespace.

`doctor` and selected `check` scopes
can run on macOS with their native prerequisites; the Linux bootstrap cannot.
Browser scope also requires the exact helper-managed Playwright cache and should
be run in the prepared Codespace unless that dependency has been separately set up.
