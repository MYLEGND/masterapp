# MASTERAPP Platform Security Authority Map

**Source of truth for the consolidated security architecture (Steps 1–2, Phases 3–6).**

> One source of *implementation* truth does not mean one universal policy.
> Applications consume shared authorities through **configuration, profiles, and
> explicit trust-boundary adapters** — never by copying the implementation.

Applications: **AgentPortal** (agent/internal + mobile API host), **ClientApp**
(client portal), **ParfaitApp** (ecommerce + internal), **Protect-Website**
(public marketing/lead + integrations), **Legend-iOS** (native bearer client).
Shared projects: **SHARED** (`Shared.*`), **Infrastructure**, **Domain**.

For each authority: implementation → adopters → adapters/profiles → intentional
exceptions → trust-boundary rationale → how to change safely → protecting tests →
deployment/config dependencies.

---

## 1. Authentication boundaries
- **Implementation:** per-app ASP.NET Core auth in each `Program.cs` (distinct schemes).
- **Adopters/adapters:** AgentPortal cookie + Entra OIDC + `LegendMobileBearer` (JWT); ClientApp cookie + OIDC; ParfaitApp internal cookie + OIDC; Protect-Website anonymous (public); Legend-iOS bearer client.
- **Intentional exceptions:** these are **REQUIRED SECURITY BOUNDARIES** and are deliberately NOT merged. Cookie and bearer trust models stay separate.
- **Rationale:** different trust models (browser session vs mobile bearer vs public).
- **Safe change:** modify the specific app scheme; never merge schemes or share cookies across apps.
- **Tests:** `MobileIntegrationTests`, anti-forgery suites.
- **Config:** Entra `AzureAd:*` / `GraphProvisioning:*` per app.

## 2. Canonical identity  → `SHARED/Auth/UserIdExtensions.GetCanonicalUserId` (+ `ClaimsExtensions.GetOid`)
- **OID-only**, never falls back to email/UPN/NameIdentifier/sub. `GetEmailCandidate` is the separate, non-authoritative email helper; `GetUserIdCandidates` is migration-only.
- **Adopters:** AgentPortal (`EffectiveAgentContext`, `AccountController`), ClientApp (`ClientIdentityAccessService`), founder guards, ownership consumers. Mobile `MobileActorResolver` is OID-only.
- **Exceptions:** oid-only inline reads left in non-divergent controllers (no security fallback); `UnderwritingController` NameIdentifier key retained (legacy data compatibility — see checklist).
- **Safe change:** edit `GetCanonicalUserId`. Never add a non-OID fallback in authoritative reads.
- **Tests:** `Phase3IdentityAuthorityTests`, `Phase6ArchitectureInvariantTests.CanonicalIdentity_HasNoNonOidFallback`.

## 3. Founder identity  → `SHARED/Auth/FounderAuthority.Evaluate`
- Canonical-OID match against a valid `FOUNDER_OID`; fail-closed on missing/malformed OID in production; email only as a dev fallback when no OID configured.
- **Adapters:** `AgentPortal.Security.FounderGuard` (OWNER_EMAIL) and `ParfaitApp.Security.ParfaitFounderGuard` (OWNER_EMAILS list) — app-specific owner config, both delegate to `FounderAuthority`.
- **Safe change:** edit `FounderAuthority.Evaluate`. Both guards inherit it.
- **Tests:** `Phase3IdentityAuthorityTests` (founder region), `Phase6ArchitectureInvariantTests.FounderGuards_Delegate_And_FailClosed`.
- **Config:** `FOUNDER_OID` (valid GUID) required in production for AgentPortal **and** ParfaitApp (startup guards enforce).

## 4. Agent context  → `AgentPortal/Services/EffectiveAgentContext`
- Real vs impersonated (`EffectiveAgentOid`) agent; founder-gated impersonation only. **REQUIRED ADAPTER** (AgentPortal-specific), consumes canonical identity + `FounderGuard`.
- **Safe change:** AgentPortal-local; preserve "impersonation never mutates the authenticated principal."

## 5. Client ownership  → `Infrastructure/Data/OwnershipQueries.AgentOwnsClientAsync`
- One `AgentClients` predicate (OID + legacy candidates + explicit UPN compat param).
- **Adopters:** AgentPortal (`ClientsController`, `FinanceController`, etc.), ClientApp (`EffectiveClientContextService`, `SupportController`).
- **Safe change:** edit `AgentOwnsClientAsync`. Consumers pass identity/candidates.
- **Tests:** `OwnershipTests`, `Phase3IdentityAuthorityTests` (ownership region).

## 6. Appointment ownership  → `AgentPortal/Controllers/CalendarController.LoadAppointmentMutationContextAsync`
- Appointment mutations require ownership of **that appointment** (AND, not OR). Stricter than #5 by design.
- **Exception:** appointment-specific rule (not the generic ownership query) — a legitimate stricter boundary.
- **Tests:** `CalendarControllerTests` (IDOR regression).

## 7. Mobile actor resolution  → `Infrastructure/Mobile/MobileActorResolver`
- OID-only, fail-closed, ambiguity-rejecting. Server is authoritative; the iOS `X-Legend-Participant-Type` header is advisory only.
- **Exception:** REQUIRED SECURITY BOUNDARY — never trust device/client role claims.
- **Tests:** `MobileIntegrationTests`.

## 8. Billing & entitlement  → `Infrastructure/Billing/MasterAppBillingOrchestrator`, `BillingEntitlementService`
- Single charge/entitlement authority; Square adapter is provider-specific. **Unchanged by this program** (verified sound).
- **Safe change:** do not refactor without a proven defect + tests.

## 9. Anti-forgery  → AgentPortal global `AutoValidateAntiforgeryTokenAttribute` (header `RequestVerificationToken`); ClientApp per-action `[ValidateAntiForgeryToken]`
- Browser cookie mutations require a token; bearer APIs, signed webhooks, and OAuth callbacks are explicitly exempt.
- **Exceptions (explicit):** Mobile bearer controllers, Square/Graph webhooks, signed ingest, OAuth callbacks retain `[IgnoreAntiforgeryToken]`; AgentPortal `ProductionController` GETs retain it (read-only + class-level `[ValidateAntiForgeryToken]`).
- **Tests:** `AntiforgeryPolicyTests`, `ClientAppProductionAntiforgeryTests`.

## 10. Data Protection  → `Infrastructure/Security/PlatformDataProtection.AddPlatformDataProtection`
- Azure Blob + Key Vault (prod) / filesystem (dev); application-name isolation.
- **Adopters:** AgentPortal (`"AgentPortal"`), Protect-Website (`"AgentPortal"`, shared ring for Meta CAPI cross-decrypt), ClientApp (`"MasterApp.ClientApp"`).
- **Exception (migration-gated):** ParfaitApp retains its existing key ring (changing it invalidates live cookies + Meta CAPI ciphertext) — see checklist.
- **Safe change:** edit the helper; never change an app's application name or rotate/delete keys casually.
- **Tests:** `Phase4PlatformSecurityTests` (DP round-trip + app-name isolation + cross-decrypt).
- **Config:** `DataProtection:BlobUri` + `DataProtection:KeyVaultKeyId` (both or neither) per adopting app.

## 11. Security headers  → `SHARED/Security/PlatformSecurityHeaders.UsePlatformSecurityHeaders`
- Baseline non-CSP headers (nosniff, XFO SAMEORIGIN, Referrer-Policy, Permissions-Policy); non-destructive.
- **Adopters:** ClientApp, Protect-Website. **Exception:** AgentPortal keeps its own header block **incl. an enforced CSP** (app-specific); ParfaitApp keeps its existing header handling.
- **Tests:** `Phase4PlatformSecurityTests` (present + non-destructive).

## 12. Forwarded headers  → `SHARED/Security/PlatformSecurityHeaders.AddPlatformForwardedHeaders`
- `X-Forwarded-For | X-Forwarded-Proto`. **Adopters:** ClientApp, Protect-Website. AgentPortal/ParfaitApp already configure their own.
- **Rationale:** correct scheme/redirect/client-IP behind Azure. Arbitrary `X-Forwarded-For` is not trusted directly.

## 13. Startup validation  → `SHARED/Security/PlatformConfigValidation.ValidateDataProtection` (+ `RequireInProduction`) and per-app production guards
- Fail-fast in production on partial DP config / missing `FOUNDER_OID` / `OWNER_EMAIL` / SQLite-on-Azure. Dev is a no-op.
- **Adopters:** all four web apps (DP validation); AgentPortal + ParfaitApp (FOUNDER_OID); AgentPortal (OWNER_EMAIL).
- **Tests:** `Phase4PlatformSecurityTests`, founder tests.

## 14. Upload validation  → `Infrastructure/Security/UploadValidation/UploadValidator` (+ `UploadValidationPolicy`)
- Magic-byte detection, extension allow-list, dangerous-extension rejection, filename sanitization/traversal prevention, size/empty checks; declarative per-profile.
- **Adopters:** AgentPortal + ClientApp avatars (`ValidateImageContent`); AgentPortal `AgentDocuments` (`ValidateMetadata`, non-buffering).
- **Exceptions (explicit):** streaming paths (`SocialMediaStorage`, `MessagingAttachmentStorage`) and remaining controller uploads enforce extension/size/randomized-name/containment inline; **in-stream signature validation deferred** (would require buffering the header — a pipeline redesign). `ValidateMetadata` is available for non-buffering adoption.
- **Tests:** `Phase5CrossPlatformSecurityTests` (upload region).

## 15. Rate limiting  → `Infrastructure/Security/PlatformRateLimiting` (`ConfigurePolicies`, `AddFixedWindowPolicy`, partition helpers)
- One fixed-window + partition-key implementation; app policy names/limits/global-limiter are profiles/data.
- **Adopters/profiles:** AgentPortal (`ingest` 300, `anon-public` 30, app global limiter 120 with SignalR exemptions); ClientApp (`clientapp-login` 8, `clientapp-public` 20); Protect-Website + ParfaitApp (`public-ingest` on analytics/tracking).
- **Exceptions (explicit):** webhooks, OAuth callbacks, mobile refresh, health checks, background jobs, internal services are **not** throttled; Protect-Website LifeQuote lead endpoints deferred (mixed rapid preview — per-endpoint tuning).
- **Safe change:** edit `AddFixedWindowPolicy`/partition helpers; apps keep names/limits as data.
- **Tests:** `Phase5CrossPlatformSecurityTests` (rate-limit region).

## 16. Logging redaction  → `SHARED/Security/LogRedactor`
- Sensitive-header masking; bearer/JWT/`key=value`/connection-string secret redaction.
- **Adoption:** available platform-wide; audit found **no** current log site emits secret values, so no forced migration (avoids reducing operational diagnostics). Adopt for any future header/exception-body logging.
- **Tests:** `Phase5CrossPlatformSecurityTests` (logging region).

## 17. Security audit events  → `SHARED/Security/SecurityAuditEvent` (+ `SecurityAuditEventTypes`/`Results`, `ILogger.LogSecurityAudit`)
- Stable audit envelope; metadata values redacted on emit. A **logging contract** (not immutable storage).
- **Tests:** `Phase5CrossPlatformSecurityTests` (audit contract stable + metadata redacted).

## 18. Webhook validation  → `Infrastructure/Billing/Square/SquareBillingWebhookSignatureValidator`, `AgentPortal/Security/IngestSignatureValidator`, Graph `clientState`
- Provider-specific signature/replay validation; **PROVIDER-SPECIFIC** adapters, not merged. **Unchanged** by this program (verified sound; F18/F24 low items noted in the risk register).

## 19. OAuth/OIDC callbacks  → per-app `Program.cs` (`/signin-oidc`, `/signout-callback-oidc`, Parfait canonical-host rewrite)
- Correlation/nonce cookies (`SameSite=None; Secure`) required; **exempt** from anti-forgery and rate limiting by design.

---

### Confirmations
- **Future common security changes are made once** in the relevant shared authority above; adopting apps inherit them.
- **App-specific policies remain profiles/adapters** (policy names, limits, allowed types, owner-email config, CSP, app schemes) — not copied implementations.
- Architecture-invariant tests (`Phase6ArchitectureInvariantTests`) + the `security-ci.yml` workflow guard against regression and re-introduced duplication.
