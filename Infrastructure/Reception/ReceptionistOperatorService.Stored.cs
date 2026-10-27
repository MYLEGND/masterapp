using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared.Analytics;
using Shared.Auth;

namespace Infrastructure.Reception;

public sealed partial class ReceptionistOperatorService
{
    private const string StoredPrefix = "saved-";
    private DbContextOptions<MasterAppDbContext> DatabaseOptions => services?.GetRequiredService<DbContextOptions<MasterAppDbContext>>()
        ?? throw new InvalidOperationException("Receptionist management storage is unavailable.");

    public async Task<ReceptionistOperatorPlan> PlanForOwnerAsync(MarketingOwnerScope owner, string key, string hash,
        Func<MarketingOwnerScope, CancellationToken, Task<bool>> authorize, CancellationToken ct = default) =>
        TenantPlan(await PlanCoreAsync(key, hash, await RequireOwnerActorAsync(owner, authorize, ct), owner, authorize, ct));

    public async Task<ReceptionistOperatorReceipt> ApplyForOwnerAsync(MarketingOwnerScope owner, ReceptionistOperatorApplyRequest request,
        Func<MarketingOwnerScope, CancellationToken, Task<bool>> authorize, CancellationToken ct = default) =>
        TenantReceipt(await ApplyCoreAsync(request, await RequireOwnerActorAsync(owner, authorize, ct), owner, authorize, ct));

    private static IReadOnlyList<ReceptionistOperatorFact> TenantFacts(IReadOnlyList<ReceptionistOperatorFact> facts) =>
        facts.Where(x => x.Label is "Owner" or "Provider project" or "Configuration" or "Current owner admission" or
            "Accepted backend" or "Provider acceptance scope" or "Configured call duration maximum" or "Requested admission" or
            "After binding acceptance" or "Test window" or "Test window starts" or "Test window expires" or "Test window action" or
            "Budget contract" or "Provider duration guarantee" or "Owner cash / consumption limits" or
            "Observed provider budget / observed UTC" or "Provider observation period" or "Owner risk consent / policy expiry" or
            "Accepted configuration identity" or "Budget side effects" or "Cash estimate: fixed / per second / per call" or
            "Consumption estimate: fixed / per second / per call").ToArray();
    private static ReceptionistOperatorPlan TenantPlan(ReceptionistOperatorPlan plan) => plan with
        { Label = plan.Operation.Contains("acceptance_window", StringComparison.Ordinal) ? "Restricted test window" : plan.Operation == "accept_binding" ? "Receptionist activation" : "Call permission", Facts = TenantFacts(plan.Facts) };
    private static ReceptionistOperatorReceipt TenantReceipt(ReceptionistOperatorReceipt receipt) => receipt with { Facts = TenantFacts(receipt.Facts) };

    private static void RequireSavedBudgetConsentCreator(ReceptionistOperatorPackage package, ReceptionistOperationPackage row)
    {
        if (package.Binding?.BudgetPolicy is { } policy &&
            (policy.OwnerAcceptedByActorId != row.CreatedByActorId || policy.OwnerAcceptedUtc != row.CreatedUtc))
            throw new InvalidOperationException("The owner risk consent differs from its authenticated saved proposal.");
    }

    private static async Task GuardStoredCredentialsAsync(MasterAppDbContext write, ReceptionistOperatorPackage package, CancellationToken ct)
    {
        if (package.Operation != "accept_binding") return;
        foreach (var input in new[] { (package.Binding!.CredentialReference, "voice-api", package.ApiCredentialSha256),
            (package.Binding.WebhookSecretReference, "voice-webhook", package.WebhookSecretSha256) })
        {
            if (!input.Item1.StartsWith(StoredPrefix, StringComparison.Ordinal)) continue;
            if (!Guid.TryParseExact(input.Item1[StoredPrefix.Length..], "N", out var id)) throw new InvalidOperationException("Invalid credential version.");
            var row = await write.Set<ReceptionistCredentialVersion>().SingleOrDefaultAsync(x => x.Id == id && x.MarketingConnectionId == package.MarketingConnectionId, ct);
            if (row is null || row.RevokedUtc is not null || row.Provider != package.Provider || row.ProviderProjectKey != package.ProviderProjectKey ||
                row.Purpose != input.Item2 || row.ValueSha256 != input.Item3) throw new DbUpdateConcurrencyException("The reviewed credential changed.");
            row.Revision = Guid.NewGuid();
        }
    }

    private async Task<string> RequireOwnerActorAsync(MarketingOwnerScope owner,
        Func<MarketingOwnerScope, CancellationToken, Task<bool>> authorize, CancellationToken ct)
    {
        var user = httpContext.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true || !await authorize(owner, ct))
            throw new UnauthorizedAccessException("Workspace settings permission is required.");
        var actor = user.GetCanonicalUserId();
        if (string.IsNullOrWhiteSpace(actor)) throw new UnauthorizedAccessException("A canonical actor is required.");
        return actor;
    }

    private async Task<ReceptionistOperatorPackage> ReadPackageAsync(string key, string hash, string actor,
        MarketingOwnerScope? owner, CancellationToken ct, MasterAppDbContext? write = null)
    {
        if (!key.StartsWith(StoredPrefix, StringComparison.Ordinal))
        {
            if (owner is not null) throw new UnauthorizedAccessException("Workspace operations require a scoped saved review.");
            return ReadPackage(key, hash, actor);
        }
        if (!Guid.TryParseExact(key[StoredPrefix.Length..], "N", out var id) || !HashValue(hash))
            throw new ArgumentException("A saved operation identity is required.");
        async Task<ReceptionistOperatorPackage> Read(MasterAppDbContext db)
        {
            var row = await db.Set<ReceptionistOperationPackage>().SingleOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new InvalidOperationException("The saved operation is unavailable.");
            var connection = await db.MarketingConnections.AsNoTracking().SingleAsync(x => x.Id == row.MarketingConnectionId, ct);
            if (owner is not null && connection.OwnerKey != owner.Key) throw new UnauthorizedAccessException("The operation belongs to another workspace.");
            var now = clock.GetUtcNow().UtcDateTime;
            if (row.ReviewState != "accepted" || row.RevokedUtc is not null || row.ExpiresUtc <= now ||
                row.ReviewedByActorId is null || row.ReviewedUtc is null ||
                row.ReviewedUtc > now || row.ReviewedPayloadSha256 != hash || row.PayloadSha256 != hash ||
                row.PayloadJson.Length is < 2 or > 262144 || ReceptionistConfigurationCodec.Hash(row.PayloadJson) != hash)
                throw new InvalidOperationException("The exact operation needs a current independent review.");
            var package = JsonSerializer.Deserialize<ReceptionistOperatorPackage>(row.PayloadJson, ReceptionistConfigurationCodec.Json)
                ?? throw new InvalidOperationException("The saved operation is invalid.");
            if (package.MarketingConnectionId != row.MarketingConnectionId || package.ConfigurationId != row.ConfigurationVersionId ||
                package.ExpectedOwnerKey != connection.OwnerKey || package.Operation != row.Operation ||
                package.ExpiresUtc != row.ExpiresUtc || package.ReviewedByFounderOid.Length != 0 || package.ReviewedUtc != default)
                throw new InvalidOperationException("The saved operation no longer matches its review.");
            RequireSavedBudgetConsentCreator(package, row);
            if (owner is not null && package.Operation is not ("accept_binding" or "set_owner_admission" or "create_acceptance_window" or "revoke_acceptance_window"))
                throw new UnauthorizedAccessException("This operation requires platform accounting authority.");
            if (package.Operation == "create_acceptance_window" && package.Window?.CreatedByActorId != row.CreatedByActorId)
                throw new InvalidOperationException("The proposed window creator differs from its saved operation.");
            if (package.Operation == "accept_binding" && (package.Binding?.CredentialReference != StoredPrefix + row.ApiCredentialVersionId?.ToString("N") ||
                (package.Binding?.Transport == "webrtc" ? row.WebhookCredentialVersionId is not null || !string.IsNullOrEmpty(package.Binding.WebhookSecretReference) :
                    package.Binding?.WebhookSecretReference != StoredPrefix + row.WebhookCredentialVersionId?.ToString("N"))))
                throw new InvalidOperationException("The reviewed credential versions do not match.");
            package = package with { ReviewedByFounderOid = row.ReviewedByActorId, ReviewedUtc = DateTime.SpecifyKind(row.ReviewedUtc.Value, DateTimeKind.Utc) };
            package.Validate(row.ReviewedByActorId, now);
            // Touch under the caller's apply transaction: concurrent revocation/review
            // cannot race the final effect without a concurrency failure.
            if (write is not null) row.Revision = Guid.NewGuid();
            return package;
        }
        if (write is not null) return await Read(write);
        await using var read = new MasterAppDbContext(DatabaseOptions);
        return await Read(read);
    }

    private async Task<string> BootstrapSecretAsync(ReceptionistOperatorPackage package, string reference, string kind,
        string expectedHash, CancellationToken ct)
    {
        if (!reference.StartsWith(StoredPrefix, StringComparison.Ordinal)) return BootstrapSecret(package, reference, kind, expectedHash);
        if (!Guid.TryParseExact(reference[StoredPrefix.Length..], "N", out var id)) throw new InvalidOperationException("A saved credential version is required.");
        await using var db = new MasterAppDbContext(DatabaseOptions);
        var row = await db.Set<ReceptionistCredentialVersion>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id &&
            x.MarketingConnectionId == package.MarketingConnectionId, ct);
        if (row is null || row.RevokedUtc is not null || row.Provider != package.Provider || row.ProviderProjectKey != package.ProviderProjectKey ||
            row.Purpose != kind || row.ValueSha256 != expectedHash) throw new InvalidOperationException("The reviewed credential is unavailable or revoked.");
        var owner = await db.MarketingConnections.AsNoTracking().Where(x => x.Id == package.MarketingConnectionId)
            .Select(x => new { x.OwnerKey, x.OwnerType, x.AgentTrackingProfileId, x.CommerceBusinessId }).SingleAsync(ct);
        if (owner.OwnerKey != package.ExpectedOwnerKey) throw new UnauthorizedAccessException("Credential ownership changed.");
        var scope = owner.OwnerType switch
        {
            "founder" => MarketingOwnerScope.Founder,
            "agent" => MarketingOwnerScope.Agent(owner.AgentTrackingProfileId!.Value),
            "business" => MarketingOwnerScope.Business(owner.CommerceBusinessId!.Value),
            _ => throw new InvalidOperationException("Unknown workspace owner.")
        };
        var secret = ReceptionistCredentialEnvelope.Unprotect(protector, scope, package.Provider, package.ProviderProjectKey, kind, row.ProtectedValue);
        if (ReceptionistConfigurationCodec.Hash(secret) != expectedHash) throw new InvalidOperationException("Credential integrity failed.");
        return secret;
    }
}
