# Commerce migration source-to-target validation contract

The isolated `CommerceLegacyTransfer` performs **plan-only by default** and supports an explicitly invoked `CopyNoOverwrite` path for authoritatively approved later data transfer. It:

- Resolves media URLs only from the authenticated business's commerce catalog; uses stable business ID + key
- Includes the correct business's customer-automation JSON and, for Parfait, legacy team JSON
- Checks all source file contents and target conflicts before any mutation; stops on missing or unsafe media
- Rejects different existing target bytes, verifies source stability, copies through a same-directory staging file and verifies each destination hash
- Reuses matching targets, never overwrites originals, and never changes SQL order, payment, product, attribution, or membership records
- Has synthetic-file idempotency and conflict tests. This does NOT authorize a live copy

**What is not proved until an explicitly approved production cutover:** source and target Azure storage reachability, complete real image/JSON inventory, shared durable automation lease, payment/webhook delivery, live media URL parity, buyer session continuity, DNS/Cloudflare binding, screenshot parity, and safe ParfaitApp retirement. All remain fail-closed.

The isolated CI also publishes both affected applications **to runner-local directories only** and compares key Parfait assets byte-for-byte. No Azure identity/deployment upload or protected branch write is used.
