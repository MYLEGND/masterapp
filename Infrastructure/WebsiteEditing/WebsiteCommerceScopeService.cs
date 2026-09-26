using Domain.Entities;
using Infrastructure.Businesses;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.WebsiteEditing;

public sealed record WebsiteCommerceScope(
    Guid CommerceBusinessId,
    string BusinessKey,
    string DisplayName);

/// <summary>
/// Binds one website content state to one existing CommerceBusiness. Business websites
/// reuse their business tenant. Founder/Protect websites provision a commerce tenant
/// through the existing business provisioning authority only when store enablement is explicit.
/// </summary>
public sealed class WebsiteCommerceScopeService(
    MasterAppDbContext db,
    ICommerceBusinessProvisioningService provisioning)
{
    public async Task<WebsiteCommerceScope?> ResolveAsync(
        WebsiteEditorTicket actor,
        WebsiteContentState state,
        bool createIfMissing,
        CancellationToken ct = default)
    {
        if (state.CommerceBusinessId.HasValue)
        {
            var bound = await ActiveBusinessAsync(state.CommerceBusinessId.Value, ct)
                ?? throw new InvalidOperationException("The website commerce scope is no longer available.");

            if (actor.SiteKey == WebsiteEditorSiteKeys.Business &&
                actor.CommerceBusinessId != bound.Id)
                throw new UnauthorizedAccessException("The website commerce scope does not match the authorized business.");

            return Map(bound);
        }

        if (actor.SiteKey == WebsiteEditorSiteKeys.Business)
        {
            if (!actor.CommerceBusinessId.HasValue)
                throw new UnauthorizedAccessException("A business website requires a business scope.");

            var business = await ActiveBusinessAsync(actor.CommerceBusinessId.Value, ct)
                ?? throw new UnauthorizedAccessException("The business scope is unavailable.");

            state.CommerceBusinessId = business.Id;
            await db.SaveChangesAsync(ct);
            return Map(business);
        }

        if (!createIfMissing)
            return null;

        if (actor.SiteKey is not (WebsiteEditorSiteKeys.Legend or WebsiteEditorSiteKeys.Protect))
            throw new UnauthorizedAccessException("This website cannot create a commerce scope.");

        var key = "website-store-" + state.Id.ToString("N");
        var existing = await db.CommerceBusinesses
            .SingleOrDefaultAsync(x => x.Key == key, ct);

        if (existing is null)
        {
            var displayName = actor.SiteKey == WebsiteEditorSiteKeys.Legend
                ? "LEGEND Store"
                : "Protect Store";
            var ownerEmail = NormalizeEmail(actor.ActorEmail) ?? "website-store@mylegnd.com";

            existing = await provisioning.CreateAsync(
                new CommerceBusinessProvisioningRequest(
                    DisplayName: displayName,
                    LegalName: displayName,
                    BusinessType: "Ecommerce",
                    OwnerEmail: ownerEmail,
                    Key: key,
                    OwnerDisplayName: ownerEmail,
                    OwnerRoleKey: "platform-owner",
                    CanManageStorefront: true,
                    CanManageCatalog: true,
                    CanManageOrders: true,
                    CanManageAnalytics: true,
                    CanManageTeam: false,
                    Storefront: new CommerceBusinessStorefrontProvisioning(
                        displayName,
                        displayName + " storefront.",
                        "Active")),
                ct);
        }

        if (!existing.IsActive || !string.Equals(existing.Status, "Active", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The website commerce scope is inactive.");

        state.CommerceBusinessId = existing.Id;
        await db.SaveChangesAsync(ct);
        return Map(existing);
    }

    private Task<CommerceBusiness?> ActiveBusinessAsync(Guid id, CancellationToken ct) =>
        db.CommerceBusinesses.SingleOrDefaultAsync(
            x => x.Id == id && x.IsActive && x.Status.ToLower() == "active", ct);

    private static WebsiteCommerceScope Map(CommerceBusiness business) =>
        new(business.Id, business.Key, business.DisplayName);

    private static string? NormalizeEmail(string? value)
    {
        var email = (value ?? "").Trim().ToLowerInvariant();
        return email.Length > 3 && email.Length <= 320 && email.Contains('@') ? email : null;
    }
}
