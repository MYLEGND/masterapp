using System.Security.Cryptography;
using System.Text.Json;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;

namespace Infrastructure.Reception;

public sealed partial class ReceptionistOperatorService
{
    private string ProtectOperatorPreview(MarketingOwnerScope owner, string actor, string packageKey,
        string packageHash, string stateHash, string secretHash)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var token = protector.Protect(owner, PreviewPurpose, JsonSerializer.Serialize(new Preview(1, actor,
            owner.Key, packageKey, packageHash, stateHash, secretHash, now, now.Add(PreviewLifetime)), ReceptionistConfigurationCodec.Json))
            ?? throw new InvalidOperationException("The operator preview could not be protected.");
        if (token.Length > 4096) throw new InvalidOperationException("The operator preview exceeds its supported bound.");
        return token;
    }

    private Preview ReadOperatorPreview(MarketingOwnerScope owner, ReceptionistOperatorApplyRequest request, string actor)
    {
        if (string.IsNullOrWhiteSpace(request.ExpectedStateToken) || request.ExpectedStateToken.Length > 4096)
            throw new ArgumentException("A bounded current operator preview is required.");
        var json = protector.Unprotect(owner, PreviewPurpose, request.ExpectedStateToken)
            ?? throw new InvalidOperationException("The operator preview is unavailable.");
        Preview? preview;
        try { preview = JsonSerializer.Deserialize<Preview>(json, ReceptionistConfigurationCodec.Json); }
        catch (JsonException) { throw new InvalidOperationException("The operator preview is invalid."); }
        var now = clock.GetUtcNow().UtcDateTime;
        if (preview is null || preview.Version != 1 || preview.ActorOid != actor || preview.OwnerKey != owner.Key ||
            preview.PackageKey != request.PackageKey || preview.PackageSha256 != request.PackageSha256 ||
            preview.IssuedUtc.Kind != DateTimeKind.Utc || preview.ExpiresUtc.Kind != DateTimeKind.Utc ||
            preview.IssuedUtc > now || preview.ExpiresUtc <= now || preview.ExpiresUtc - preview.IssuedUtc != PreviewLifetime)
            throw new InvalidOperationException("The operator preview is invalid or expired.");
        return preview;
    }

    private async Task<ReceptionistOperatorPlan> PlanAssistantAccountingAsync(string key, string hash,
        ReceptionistOperatorPackage package, string actor, CancellationToken ct)
    {
        try
        {
            var state = await admission.ReadAssistantAccountingStateAsync(package, hash, ct);
            if (state.CompletedReference is null) state.RequireRevisions(package);
            return new(key, hash, package.Label, package.Operation, true, [], state.Facts,
                ProtectOperatorPreview(state.Owner, actor, key, hash, state.StateSha256, string.Empty));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException or CryptographicException)
        {
            return new(key, hash, package.Label, package.Operation, false,
                ["The exact assistant receipt, immutable binding or accounting state is unavailable."], [], null);
        }
    }

    private async Task<ReceptionistOperatorReceipt> ApplyAssistantAccountingAsync(ReceptionistOperatorApplyRequest request,
        ReceptionistOperatorPackage package, string actor, MarketingOwnerScope? owner,
        Func<MarketingOwnerScope, CancellationToken, Task<bool>>? authorize, CancellationToken ct)
    {
        var initial = await admission.ReadAssistantAccountingStateAsync(package, request.PackageSha256, ct);
        var preview = ReadOperatorPreview(initial.Owner, request, actor);
        if (initial.CompletedReference is not null)
            return Receipt(request, package, "already_applied", true, true, initial.CompletedReference, initial.Facts);
        initial.RequireRevisions(package);
        if (initial.StateSha256 != preview.StateSha256 || preview.SecretInputsSha256 != string.Empty)
            throw new DbUpdateConcurrencyException("The reviewed assistant accounting state changed.");
        async Task Guard(MasterAppDbContext write)
        {
            var currentActor = owner is null ? RequireFounder() : await RequireOwnerActorAsync(owner, authorize!, ct);
            var currentPackage = await ReadPackageAsync(request.PackageKey, request.PackageSha256, currentActor, owner, ct, write);
            var current = await admission.ReadAssistantAccountingStateAsync(write, currentPackage, request.PackageSha256, ct);
            if (current.CompletedReference is not null) return;
            current.RequireRevisions(currentPackage);
            if (current.StateSha256 != preview.StateSha256 || currentActor != preview.ActorOid)
                throw new DbUpdateConcurrencyException("The reviewed assistant accounting state changed.");
        }
        try { await admission.ReconcileAssistantCostAsync(package, initial, Guard, ct); }
        catch (DbUpdateConcurrencyException) { throw; }
        catch (Exception error) when (error is DbUpdateException or System.Data.Common.DbException or TimeoutException or OperationCanceledException)
        {
            // Reconcile ambiguous commit once; never rerun the mutation.
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                var observed = await admission.ReadAssistantAccountingStateAsync(package, request.PackageSha256, recovery.Token);
                return Receipt(request, package, observed.CompletedReference is null ? "commit_unknown" : "applied", observed.CompletedReference is not null,
                    observed.CompletedReference is not null, observed.CompletedReference, observed.Facts);
            }
            catch (Exception readError) when (readError is InvalidOperationException or System.Data.Common.DbException or OperationCanceledException)
            { return Receipt(request, package, "commit_unknown", false, false, null, []); }
        }
        var final = await admission.ReadAssistantAccountingStateAsync(package, request.PackageSha256, ct);
        return Receipt(request, package, final.CompletedReference is null ? "commit_unknown" : "applied", final.CompletedReference is not null,
            final.CompletedReference is not null, final.CompletedReference, final.Facts);
    }
}
