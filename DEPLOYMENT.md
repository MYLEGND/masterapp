# Deployment Checklist

## Pre-Deploy

### Database
- [ ] Create or verify a fresh Azure SQL backup / point-in-time restore point before any publish.
- [ ] Do **not** run ad-hoc SQL files against production unless they have been reviewed for destructive commands like `DROP TABLE`, `TRUNCATE`, or broad `DELETE`.
- [ ] All pending EF Core migrations applied to Azure SQL:
  ```
  dotnet ef database update --project Infrastructure --startup-project AgentPortal \
    --connection "<azure-sql-connection-string>"
  ```
  *(Remember: EF tooling uses the startup project's provider. For SQL Server migrations, generate DDL manually and apply via `sqlcmd` if the provider cannot be switched.)*
- [ ] Verify `__EFMigrationsHistory` contains all expected migration IDs.

### App Service Settings (Azure Portal → Configuration)
| Setting | Type | Notes |
|---|---|---|
| `ConnectionStrings:MasterAppDb` | SQLServer | Azure SQL connection string |
| `AzureAd:TenantId` | App setting | AAD tenant ID |
| `AzureAd:ClientId` | App setting | App registration client ID |
| `AzureAd:ClientSecret` | App setting | Key Vault reference preferred |
| `AzureAd:Domain` | App setting | e.g. `mylegnd.com` |
| `Founder__Upn` | App setting | Founder's UPN (e.g. `zac.owen@mylegnd.com`) |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | App setting | App Insights connection string |
| `SignalR__RedisConnectionString` | App setting | Redis connection string (for multi-instance) |
| `DataProtection__BlobUri` | App setting | Azure Blob URI for Data Protection keys |
| `DataProtection__KeyVaultKeyId` | App setting | Key Vault key URI for key encryption |
| `OWNER_EMAIL` | App setting | Required in Production (startup guard) |

### Managed Identity Requirements
- [ ] App Service Managed Identity has **Storage Blob Data Contributor** on the DP keys storage container.
- [ ] App Service Managed Identity has **Key Vault Crypto User** on the DP Key Vault key.
- [ ] App Service Managed Identity has **Azure Cache for Redis Data Contributor** (if using Redis).

### Redis (if enabling multi-instance)
- [ ] Azure Cache for Redis provisioned (`Basic C0` minimum).
- [ ] `SignalR__RedisConnectionString` set on App Service.
- [ ] Verify Redis is reachable: `curl -f https://<site>/readyz` → `Healthy` for `redis` check.

---

## Deploy

1. Push to `main` / trigger deployment pipeline.
2. Monitor **Deployment Center → Logs** in Azure Portal.
3. Watch **Log Stream** for any startup exceptions.

---

## Post-Deploy Health Checks

```bash
# Liveness (process alive)
curl -f https://<site>/healthz

# Readiness (DB + Redis)
curl -f https://<site>/readyz
```

Both must return HTTP 200. If `/readyz` returns Unhealthy, check:
- DB connectivity (firewall rules, connection string)
- Redis connectivity (firewall rules, connection string)

---

## Rollback

1. In Azure Portal → App Service → **Deployment slots** → swap back to previous slot, OR
2. In **Deployment Center** → redeploy previous successful build.
3. If migration must be rolled back: apply the `Down()` migration:
   ```
   dotnet ef database update <previous-migration-id> ...
   ```
4. If Redis state is causing issues: remove `SignalR__RedisConnectionString` from App Settings → restart → app falls back to in-memory `LeadBridgeStateService`.
5. If Data Protection keys are unreachable (decryption failures): restore previous key XML blob from Azure Blob Storage versioning.

---

## Smoke Test After Deploy

- [ ] Sign in via Azure AD — confirm redirect and landing page.
- [ ] Navigate to Leads → confirm list loads or shows empty state.
- [ ] Navigate to Clients → confirm list loads or shows empty state.
- [ ] Open browser DevTools → Network → confirm no 500 errors.
- [ ] Check Application Insights → **Live Metrics** — requests flowing, no exception spikes.
- [ ] Verify SignalR: open two browser tabs, update a lead call count — confirm both tabs reflect the change in real time.
