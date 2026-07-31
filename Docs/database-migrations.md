# Database migrations

Run every database workflow from the repository root. The command owns the EF
project, startup project, `MasterAppDbContext`, test project, and migration
folder, so no developer needs to remember EF Core paths or options.

```bash
./scripts/db.sh check
./scripts/db.sh sync AddPrivateProfiles
./scripts/db.sh validate
./scripts/db.sh bundle
```

`check` is read-only: it builds, tests, checks for pending model changes, checks
the migration artifact set, and runs `git diff --check`.

`sync` generates a migration only when the EF model has pending changes. A
descriptive PascalCase name is required. Safe additive migrations are applied to
the local development database automatically. Destructive or data-moving changes
stop with `MANUAL REVIEW REQUIRED` and are not applied.

`apply` applies existing validated migrations only to the repository development
database. To use another *local* database, set the clearly named variable below;
the command refuses known Azure production hosts and never displays its value.

```bash
MASTERAPP_LOCAL_DB_CONNECTION='Data Source=/absolute/path/masterapp.db' ./scripts/db.sh apply
```

`validate` is the pre-commit gate. It does not stage, commit, push, generate, or
apply a migration. It reports the exact migration artifacts that must be included
in the commit.

`bundle` first runs `validate`, then creates
`artifacts/migrations/masterapp-migrate`. It never executes the bundle. During a
controlled production deployment, run that bundle exactly once before the
application rollout, using the deployment environment's approved connection
string and change controls.

The repository includes a locked baseline of historical hand-authored migrations
in `scripts/db-legacy-manual-migrations.txt`. It exists only to preserve valid
history predating generated designer artifacts. New migrations must be generated
through `sync`; they require the migration source, matching `.Designer.cs`, and
`MasterAppDbContextModelSnapshot.cs` together.
