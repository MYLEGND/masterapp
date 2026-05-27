# Migration Freeze Notice

Migration lineage is temporarily frozen until a controlled SQL Server baseline consolidation is completed.

Do not create, remove, rename, or manually edit EF migrations without review.

Current findings:
- Production has applied migrations not fully represented in active local EF discovery.
- Historical regen/sync/repair migrations fragmented lineage.
- SQL Server is now the authoritative provider for EF tooling.
- SQLite is local-runtime only and must not drive production migration design.

Allowed:
- Normal code changes that do not require schema changes.
- Emergency SQL Server-targeted migration only after review.

Blocked:
- Sync migrations
- Regen migrations
- Manual snapshot edits
- SQLite production repair migrations
- Broad schema rewrite migrations
