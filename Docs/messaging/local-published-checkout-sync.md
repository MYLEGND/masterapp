# One Mac checkout and persistent local testing

Zac's active Mac checkout is `/Users/zacowen/MASTERAPP`. Its host-local Git setting
`legend.canonicalCheckout` pins that path. The existing
`scripts/sync-published-checkout.py` owner refuses alternate linked worktrees when
that pin is present. Other machines retain their own authorized continuity flow.
Do not create a fresh checkout or database to resume existing work.

## Bring source into the Mac

Finish and preserve the current task before importing source. From the canonical
checkout, stop app/build processes and use:

```sh
python3 -B scripts/sync-published-checkout.py --import-source Legend/fast-track-release --expected-source FULL_REVIEWED_SHA
```

For another authorized GitHub branch, pass its exact branch name and reviewed full
SHA to the same command. Codespaces must first preserve original commits on the
canonical GitHub repository, then use this intake. Offline recovery retains the
existing verified bundle session modes; a temporary bundle is not durable recovery.
Never copy a Codespace database, user secrets or environment file into the Mac.

Intake checks canonical origin, clean source, expected remote head, ancestry and
active processes. It fast-forwards the current `isolation/` branch without switching
branches, resetting, stashing, deploying or touching the database. Divergence stops
with the existing work retained; reconcile reviewed commits explicitly. Normal
session preflight/finish, independent review and protected fast-track integration
still apply. Launching an app never pulls Git changes.

The former every-minute `com.mylegnd.native-checkout-sync` LaunchAgent and automatic
Azure SQL firewall updater were retired on this Mac on 2026-11-10. Their verified
backups are under `~/.codex/legend-mac/retired-automatic-jobs-20261110/`. Do not
reinstall them as a workaround for a dirty checkout or a denied production read.

## Persistent local test database

All four managed app launchers use the existing database owner:

```sh
bash scripts/db.sh local status
bash scripts/db.sh local run portal
# Other app names: client, protect, parfait
```

The shared local SQL Server database is `MasterAppLocalTest`, in Docker's named
volume `legend-masterapp-local-test`, exposed only at `127.0.0.1:14339`. Its private
identity and generated local credentials live outside Git under
`~/.local/share/legend/masterapp-local-test/`. Source intake, app launches and app
restarts preserve that database. Startup checks identity and schema readiness;
it never recreates a missing database or silently falls back to SQLite/production.

`local start` and `local stop` retain the volume. `local migrate` explicitly invokes
the existing local migration owner against this database. Review schema changes
and retain recovery before applying destructive migrations. `local init` is a
one-time explicit operation only; it refuses an existing configuration, container
or volume. It generates the current EF model from source, initializes migration
metadata and a unique identity, and imports no customer data. This initial model
bootstrap is **not** proof that the production migration chain has been rehearsed;
the existing isolated migration release gate remains required when applicable.

Normal Development startup rejects remote SQL targets. Local testing launchers
strip inherited provider credentials; local testing host configuration removes
operator user secrets and application background workers. This protects database
separation but does not prove every external integration is sandboxed. Exercise
payment, messaging and provider journeys only with their explicit test configuration;
do not load production credentials or copy customer records into local testing.
Use synthetic test accounts and retain useful synthetic scenarios across releases.

Explicit isolated read-only website capture fixtures remain separate test tools;
they are not the normal app launch path or a replacement persistent database.

## Release continuity

The canonical release state is `LEGEND-REVENUE-LAUNCH-CHECKPOINT.md`, with actual
GitHub heads and provider receipts. Incoming source is tested on the Mac before
an authorized production release. Use the existing admitted release, mutex and
receipt owners; stop affected apps only when upload-ready accepted packages exist,
then deploy, restart and verify. Source-only fast-track integration must not replay
successful production uploads. Archived worktrees and dated release artifacts are
recovery evidence, never active checkout instructions.

Focused checks: `python3 -B scripts/test-sync-published-checkout.py`,
`python3 -B scripts/test-local-test-database.py`, and
`LocalDevelopmentDatabaseTests` in `AgentPortal.Tests`.
