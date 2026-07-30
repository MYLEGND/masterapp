# MASTERAPP Security Launch & Deployment Checklist

Companion to `MASTERAPP-SECURITY-AUTHORITY.md`. Complete the CONFIGURATION and
MIGRATION-GATED items **before** deploying; run SMOKE TESTS after each app is up;
keep ROLLBACK notes handy. No secret values appear in this document.

> The platform is **not deployment-ready until every required external
> configuration item below is verified in the target environment.**

## CONFIGURATION (verify in each production environment)
- [ ] **`FOUNDER_OID`** set to a valid GUID in **AgentPortal** production (startup fails closed otherwise).
- [ ] **`FOUNDER_OID`** set to a valid GUID in **ParfaitApp** production (startup fails closed otherwise).
- [ ] **`OWNER_EMAIL`** present in AgentPortal production (existing guard); `OWNER_EMAILS`/`OWNER_EMAIL` for ParfaitApp team access.
- [ ] **AgentPortal** `DataProtection:BlobUri` **and** `DataProtection:KeyVaultKeyId` both set (partial config now fails startup).
- [ ] **AgentPortal** `DataProtection:KeyVaultKeyId` — same key ring as today (do not change the `"AgentPortal"` application name).
- [ ] **Protect-Website** shares the AgentPortal Data Protection config (same `DataProtection:BlobUri`/`KeyVaultKeyId`) so agent-scoped Meta CAPI credentials decrypt.
- [ ] **ClientApp** `DataProtection:BlobUri` **and** `DataProtection:KeyVaultKeyId` set for a cross-instance key ring (otherwise durable per-instance file-system keys; partial config fails startup).
- [ ] **ClientApp** `DataProtection:KeyVaultKeyId` — application name stays `"MasterApp.ClientApp"` (isolated from AgentPortal).
- [ ] Trusted proxy / forwarded-header path confirmed (Azure ingress) so client IP + scheme resolve correctly (rate-limit partitions & HTTPS depend on it).
- [ ] Rate-limit settings: defaults are code-defined (no config needed); confirm no reverse-proxy also imposes conflicting limits.
- [ ] Logging: default provider; confirm no verbose request/response-body logging is enabled that bypasses `LogRedactor`.
- [ ] **No secrets committed** to source (CI `security-ci.yml` enforces; `.env`/`publish-macos/**` remain gitignored).
- [ ] **Entra client secret rotation** (Phase 1 SEC-2): the client secret for app registration `bb7234ae-…` was present in plaintext local `publish-macos` artifacts — **rotate it** and store only in App Service settings / Key Vault; delete the artifacts.
- [ ] Confirm committed `Analytics:SharedSecret` / `Tracking:SharedSecret` defaults are **overridden** in production and rotated (Phase 1 SEC-1).

## MIGRATION-GATED ITEMS (schedule + validate in staging first)
- [ ] **ParfaitApp Data Protection migration** — moving Parfait to the platform Blob+Key Vault ring will (a) log out existing `ParfaitApp.InternalAuth` sessions once and (b) make existing Meta CAPI ciphertext unreadable until re-connected. Schedule a maintenance window; re-connect Meta after.
- [ ] **Existing cookies requiring reauthentication** — ClientApp: first deploy after the ephemeral→persistent DP change issues fresh keys once (identical to today's per-restart behavior); no worse than current restart.
- [ ] **Meta CAPI ciphertext / reconnection** — only relevant to the ParfaitApp DP migration above; AgentPortal↔Protect-Website ring is unchanged.
- [ ] **Legacy UPN-only ownership links** — `AgentOwnsClientAsync` still honors the explicit UPN compat parameter; tightening to OID-only (F20) is deferred until links are backfilled.
- [ ] **`UnderwritingController` NameIdentifier-keyed records** — key intentionally left unchanged to avoid hiding existing records; migrate to canonical OID only with a data backfill.
- [ ] **Streaming upload signature validation** — social/messaging streaming paths validate extension/size/name/containment but not in-stream magic bytes; adopt a bounded header-peek later (no full buffering).
- [ ] **Deferred rate-limit tuning** — Protect-Website `LifeQuoteController` lead endpoints not yet throttled (mixed rapid preview); AgentPortal/ClientApp keep their existing (now shared-builder) limiters.

## SMOKE TESTS (post-deploy, per app)
- [ ] Founder access granted (configured `FOUNDER_OID`); **non-founder denied**.
- [ ] Agent scoping: agent sees only owned clients; **cross-client access denied**.
- [ ] View-as-client works for the owning agent; blocked otherwise.
- [ ] Calendar: cannot mutate another agent's appointment (IDOR closed).
- [ ] Browser mutation without anti-forgery token → rejected; with token → works (AgentPortal + ClientApp `ProductionController` add/edit/delete).
- [ ] Zoom link add/delete works (token now sent).
- [ ] Cookies survive an app restart (ClientApp persistent DP); cross-instance where multi-instance.
- [ ] Mobile: login + bearer APIs; social/story upload; messaging attachments.
- [ ] Story/social + messaging uploads succeed; **spoofed non-image avatar rejected** (magic-byte gate).
- [ ] Webhook processing (Square/Graph) still validates signatures and succeeds for valid events.
- [ ] OAuth/OIDC login + callback works (all apps).
- [ ] Public analytics/tracking ingest throttles at the cap (Protect-Website/ParfaitApp); login throttling (ClientApp).
- [ ] Log spot-check: no Authorization/Cookie/token/secret/connection-string values in logs.

## ROLLBACK
- **Application rollback boundaries:** every code change is per-app and independently revertible in reverse deploy order (Legend-iOS unaffected — no server contract changed).
- **Data Protection:** do **not** delete or rotate keys to roll back. Reverting ClientApp to ephemeral DP would invalidate cookies; prefer keeping the persistent ring. AgentPortal/Protect-Website ring and app names are unchanged — safe.
- **Configuration rollback:** the new startup guards require `FOUNDER_OID` (both apps) and consistent DP config; if rolling back the guards, ensure the app still starts.
- **Why keys must not be casually rotated/deleted:** rotation invalidates all existing cookies and makes existing IDataProtector-encrypted values (PII, Meta CAPI, impersonation tokens) unreadable.
- **Revertible without schema changes:** all Phase 1–6 changes are **code + config only** — no database migrations were added; nothing requires a schema rollback.
- **Code-only changes:** identity/founder/ownership consolidation, anti-forgery, shared middleware/authorities, upload/rate-limit/redaction authorities, MailKit/MimeKit upgrade, CI workflow.
- **External settings that must remain compatible:** `FOUNDER_OID`, `OWNER_EMAIL(S)`, `DataProtection:*`, Entra `AzureAd:*`, Square/Meta secrets — keep names stable across rollback.

## DEPLOYMENT SEQUENCE (suggested)
1. Backups (DB + Data Protection key ring storage).
2. Verify all CONFIGURATION items in the target environment.
3. Deploy shared build (Infrastructure/SHARED/Domain via app packages) — no DB migration.
4. AgentPortal → ClientApp → ParfaitApp → Protect-Website.
5. Legend-iOS: no server-contract change; ship on its own cadence.
6. Run SMOKE TESTS after each app.
