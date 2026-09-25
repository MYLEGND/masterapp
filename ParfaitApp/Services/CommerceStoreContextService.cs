using System.Text.Json;
using Domain.Entities;
using Infrastructure.Businesses;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Microsoft.EntityFrameworkCore;

namespace ParfaitApp.Services;

public sealed record CommerceStoreContext(
    Guid CommerceBusinessId,
    string BusinessKey,
    string StoreName,
    string NavigationLabel,
    string Headline,
    string Subheadline,
    string StoreRootPath,
    string CartPath,
    string CheckoutPath,
    string SuccessPath,
    string CartStorageKey,
    bool IsParfait,
    string AccentColor,
    string? LogoUrl,
    string? GlobalCheckoutUrl,
    WebsiteThemeOverride Theme);

/// <summary>
/// One public storefront scope resolver for Parfait and every website-linked commerce tenant.
/// Public non-Parfait stores are available only when the linked published website has Store.Enabled.
/// </summary>
public sealed class CommerceStoreContextService(
    MasterAppDbContext db,
    CommerceBusinessScopeResolver businesses,
    ParfaitBusinessScopeService parfaitScope)
{
    public async Task<CommerceStoreContext?> ResolvePublicAsync(string? businessKey, CancellationToken ct = default)
    {
        var normalized = (businessKey ?? "").Trim().ToLowerInvariant();
        CommerceBusiness? business;
        if (string.IsNullOrWhiteSpace(normalized) || normalized == ParfaitBusinessScopeService.ParfaitBusinessKey)
        {
            business = await parfaitScope.GetParfaitAsync(ct);
            return await BuildAsync(business, publishedOnly: false, ct);
        }

        business = await businesses.ResolveActiveByKeyAsync(normalized, ct);
        if (business is null) return null;
        return await BuildAsync(business, publishedOnly: true, ct);
    }

    public async Task<CommerceStoreContext?> ResolveForWebsiteTicketAsync(
        string token,
        WebsiteEditorTicketProtector tickets,
        IConfiguration configuration,
        CancellationToken ct = default)
    {
        var actor = await WebsiteTicketAuthorization.ResolveAsync(db, tickets, configuration, token, ct);
        if (actor is null) return null;

        var state = await db.Set<WebsiteContentState>()
            .SingleOrDefaultAsync(x => x.OwnerKey == actor.OwnerUserId && x.SiteKey == actor.SiteKey, ct);
        if (state?.CommerceBusinessId is not Guid businessId || businessId == Guid.Empty) return null;

        var business = await businesses.ResolveActiveByIdAsync(businessId, ct);
        if (business is null) return null;

        var draft = Deserialize(state.DraftJson);
        if (draft.Store?.Enabled != true) return null;

        return await BuildAsync(business, publishedOnly: false, ct, draft);
    }

    private async Task<CommerceStoreContext?> BuildAsync(
        CommerceBusiness business,
        bool publishedOnly,
        CancellationToken ct,
        WebsiteContentDocument? explicitDocument = null)
    {
        var settings = await db.CommerceBusinessStorefrontSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CommerceBusinessId == business.Id, ct);

        WebsiteContentDocument? websiteDocument = explicitDocument;
        if (websiteDocument is null)
        {
            var state = await db.Set<WebsiteContentState>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.CommerceBusinessId == business.Id, ct);
            if (state is not null)
            {
                if (publishedOnly)
                {
                    if (!state.PublishedVersionId.HasValue) return null;
                    var version = await db.Set<WebsiteContentVersion>().AsNoTracking()
                        .SingleOrDefaultAsync(x => x.Id == state.PublishedVersionId.Value && x.StateId == state.Id, ct);
                    if (version is null) return null;
                    websiteDocument = Deserialize(version.DocumentJson);
                    if (websiteDocument.Store?.Enabled != true) return null;
                }
                else
                {
                    websiteDocument = Deserialize(state.DraftJson);
                }
            }
        }

        var isParfait = string.Equals(business.Key, ParfaitBusinessScopeService.ParfaitBusinessKey, StringComparison.OrdinalIgnoreCase);
        if (publishedOnly && !isParfait && websiteDocument?.Store?.Enabled != true) return null;

        var label = websiteDocument?.Store?.NavigationLabel?.Trim();
        if (string.IsNullOrWhiteSpace(label)) label = isParfait ? "Shop" : "Store";

        var root = isParfait ? "/store" : "/store/s/" + Uri.EscapeDataString(business.Key);
        var theme = websiteDocument?.Theme ?? new WebsiteThemeOverride();

        return new CommerceStoreContext(
            business.Id,
            business.Key,
            business.DisplayName,
            label,
            string.IsNullOrWhiteSpace(settings?.BrandHeadline) ? business.DisplayName : settings!.BrandHeadline,
            string.IsNullOrWhiteSpace(settings?.BrandSubheadline) ? business.DisplayName + " storefront." : settings!.BrandSubheadline,
            root,
            root + "/cart",
            root + "/checkout",
            root + "/success",
            isParfait ? "parfaitCart" : "legendCommerceCart:" + business.Key.ToLowerInvariant(),
            isParfait,
            settings?.AccentColor?.Trim() ?? "",
            string.IsNullOrWhiteSpace(settings?.LogoUrl) ? null : settings!.LogoUrl.Trim(),
            string.IsNullOrWhiteSpace(settings?.GlobalStoreCheckoutUrl) ? null : settings!.GlobalStoreCheckoutUrl.Trim(),
            theme);
    }

    private static WebsiteContentDocument Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new WebsiteContentDocument();
        try
        {
            return WebsiteContentSanitizer.Sanitize(
                JsonSerializer.Deserialize<WebsiteContentDocument>(
                    json,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new());
        }
        catch
        {
            return new WebsiteContentDocument();
        }
    }
}
