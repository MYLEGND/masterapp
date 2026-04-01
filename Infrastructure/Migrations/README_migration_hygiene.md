Migration hygiene (AgentPortal)
===============================

Providers
- SQL Server (production) – standard migrations path.
- SQLite (local dev / tests) – some older migrations used provider-conditional SQL; keep SQLite paths in sync.

Known risks mitigated
- 20260330094500_RepairAgentProfilesSqlite ensures AgentProfiles exists on SQLite before later ALTERs.
- MigrationHealthHostedService logs provider, pending migrations, and presence of critical tables (ActionItems, ActionLogs, Blockers, DecisionRecords, Commitments) at startup without mutating schema.

Local discipline
- Use `dotnet ef migrations add` from Infrastructure with the correct provider set in `appsettings.Development.json` to avoid provider-mismatched SQL.
- If switching between SQLite and SQL Server locally, delete only the local dev DB file; do **not** prune migrations.
- Always run `dotnet ef database update` after pulling migrations; check startup logs for missing critical tables.
