using Domain.Billing;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using ParfaitApp.Services;

namespace Infrastructure.WebsiteEditing;

// Both app shells create one-use handoffs. Protect alone redeems them into business editor tickets.
public static class WebsiteEditorHandoffService
{
    public static async Task<(string OpaqueState, DateTime ExpiresUtc)> CreateAsync(
        MasterAppDbContext db, Guid clientProfileId, Guid commerceBusinessId,
        string actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        if (clientProfileId == Guid.Empty || commerceBusinessId == Guid.Empty || string.IsNullOrWhiteSpace(actorUserId))
            throw new ArgumentException("A complete website editor handoff authority is required.");

        var nowUtc = DateTime.UtcNow;
        var expiresUtc = nowUtc.AddMinutes(2);
        var token = WebsiteEditorHandoffToken.Create();

        db.ClientIdentityContinuations.Add(new ClientIdentityContinuation
        {
            ClientProfileId = clientProfileId,
            CommerceBusinessId = commerceBusinessId,
            ActorUserId = actorUserId.Trim(),
            ActorEmail = (actorEmail ?? "").Trim().ToLowerInvariant(),
            Purpose = ClientIdentityContinuationPurpose.WebsiteEditor,
            TokenHash = WebsiteEditorHandoffToken.Hash(token),
            IntendedNormalizedEmail = (actorEmail ?? "").Trim().ToLowerInvariant(),
            ReturnUrl = "/",
            ExpiresUtc = expiresUtc,
            CreatedUtc = nowUtc
        });

        await db.SaveChangesAsync(cancellationToken);
        return (token, expiresUtc);
    }

    public static async Task<(string OpaqueState, DateTime ExpiresUtc)?> CreateStoreAsync(
        MasterAppDbContext db, CommerceStoreContextService stores, WebsiteEditorTicketProtector protector,
        string protectedRequest, string authenticatedActor, string capability,
        Guid? selectedProfileId = null, CancellationToken ct = default)
    {
        var request = protector.TryUnprotectStoreLoginRequest(protectedRequest);
        if (request is null) return null;
        var scope = await stores.ResolveManagementScopeAsync(request, ct);
        if (scope is null) return null;
        var actor = await stores.ResolveManagementActorAsync(scope, authenticatedActor, capability, selectedProfileId, ct);
        if (actor is null) return null;
        var token = WebsiteEditorHandoffToken.Create();
        var expires = DateTime.UtcNow.AddMinutes(2);
        if (expires > request.ExpiresUtc) expires = request.ExpiresUtc;
        db.ClientIdentityContinuations.Add(new ClientIdentityContinuation
        {
            Purpose = ClientIdentityContinuationPurpose.StoreManagement,
            ClientProfileId = actor.ClientProfileId, CommerceBusinessId = scope.CommerceBusinessId,
            ActorUserId = actor.ActorUserId, DestinationOrigin = scope.DestinationOrigin,
            WebsiteSiteKey = scope.WebsiteSiteKey, WebsiteOwnerKey = scope.WebsiteOwnerKey,
            BrowserCorrelationHash = request.BrowserCorrelationHash,
            TokenHash = WebsiteEditorHandoffToken.Hash(token), ReturnUrl = request.ReturnPath,
            ExpiresUtc = expires, CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
        return (token, expires);
    }

    public static async Task<(CommerceManagementActor Actor, string ReturnPath)?> RedeemStoreAsync(
        MasterAppDbContext db, CommerceStoreContextService stores, CommerceManagementScope scope,
        string opaqueState, string browserNonce, Func<string, string?> capabilityForPath,
        CancellationToken ct = default, TimeProvider? timeProvider = null)
    {
        var clock = timeProvider ?? TimeProvider.System;
        if (string.IsNullOrWhiteSpace(browserNonce) || string.IsNullOrWhiteSpace(opaqueState)) return null;
        var tokenHash = WebsiteEditorHandoffToken.Hash(opaqueState);
        var nonceHash = WebsiteEditorHandoffToken.Hash(browserNonce);
        var now = clock.GetUtcNow().UtcDateTime;
        var row = await db.ClientIdentityContinuations.AsNoTracking().SingleOrDefaultAsync(r =>
            r.TokenHash == tokenHash && r.Purpose == ClientIdentityContinuationPurpose.StoreManagement &&
            r.ConsumedUtc == null && r.ExpiresUtc > now && r.CommerceBusinessId == scope.CommerceBusinessId &&
            r.DestinationOrigin == scope.DestinationOrigin && r.WebsiteSiteKey == scope.WebsiteSiteKey &&
            r.WebsiteOwnerKey == scope.WebsiteOwnerKey && r.BrowserCorrelationHash == nonceHash, ct);
        if (row is null || string.IsNullOrWhiteSpace(row.ActorUserId)) return null;
        var capability = capabilityForPath(row.ReturnUrl);
        if (capability is null) return null;
        var actor = await stores.ResolveManagementActorAsync(scope, row.ActorUserId, capability, row.ClientProfileId, ct);
        if (actor is null) return null;
        now = clock.GetUtcNow().UtcDateTime;
        var consumed = await db.ClientIdentityContinuations.Where(r => r.Id == row.Id &&
            r.Purpose == ClientIdentityContinuationPurpose.StoreManagement && r.ConsumedUtc == null && r.ExpiresUtc > now &&
            r.TokenHash == tokenHash && r.CommerceBusinessId == scope.CommerceBusinessId &&
            r.DestinationOrigin == scope.DestinationOrigin && r.WebsiteSiteKey == scope.WebsiteSiteKey &&
            r.WebsiteOwnerKey == scope.WebsiteOwnerKey && r.BrowserCorrelationHash == nonceHash)
            .ExecuteUpdateAsync(updates => updates.SetProperty(r => r.ConsumedUtc, now), ct);
        return consumed == 1 ? (actor, row.ReturnUrl) : null;
    }

}
