using System.Text.Json;
using Domain.Entities;
using Infrastructure.Businesses;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Microsoft.EntityFrameworkCore;

namespace ParfaitApp.Services;

public sealed record CommerceStoreContext(
    Guid CommerceBusinessId,
    Guid? WebsiteContentVersionId,
    Guid? AgentTrackingProfileId,
    string WebsiteSiteKey,
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
/// Commerce owns products/orders; the linked website owner remains the marketing/analytics authority.
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
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized == ParfaitBusinessScopeService.ParfaitBusinessKey)
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

        return await BuildAsync(
            business,
            publishedOnly: false,
            ct,
            explicitDocument: draft,
            explicitSiteKey: actor.SiteKey,
            explicitState: state);
    }

    private async Task<CommerceStoreContext?> BuildAsync(
        CommerceBusiness business,
        bool publishedOnly,
        CancellationToken ct,
        WebsiteContentDocument? explicitDocument = null,
        string? explicitSiteKey = null,
        WebsiteContentState? explicitState = null)
    {
        var settings = await db.CommerceBusinessStorefrontSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CommerceBusinessId == business.Id, ct);

        var linkedState = explicitState ?? await db.Set<WebsiteContentState>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.CommerceBusinessId == business.Id, ct);

        WebsiteContentDocument? websiteDocument = explicitDocument;
        Guid? publishedVersionId = null;
        var websiteSiteKey = !string.IsNullOrWhiteSpace(explicitSiteKey)
            ? explicitSiteKey.Trim().ToLowerInvariant()
            : linkedState?.SiteKey?.Trim().ToLowerInvariant()
              ?? (IsParfaitKey(business.Key) ? "ParfaitApp" : WebsiteEditorSiteKeys.Business);

        if (websiteDocument is null && linkedState is not null)
        {
            if (publishedOnly)
            {
                if (!linkedState.PublishedVersionId.HasValue) return null;
                var version = await db.Set<WebsiteContentVersion>().AsNoTracking()
                    .SingleOrDefaultAsync(
                        x => x.Id == linkedState.PublishedVersionId.Value && x.StateId == linkedState.Id,
                        ct);
                if (version is null) return null;
                publishedVersionId = version.Id;
                websiteDocument = Deserialize(version.DocumentJson);
                if (websiteDocument.Store?.Enabled != true) return null;
            }
            else
            {
                websiteDocument = Deserialize(linkedState.DraftJson);
            }
        }

        var isParfait = IsParfaitKey(business.Key);
        if (publishedOnly && !isParfait && websiteDocument?.Store?.Enabled != true) return null;

        Guid? agentTrackingProfileId = null;
        if (string.Equals(websiteSiteKey, WebsiteEditorSiteKeys.Protect, StringComparison.OrdinalIgnoreCase) &&
            linkedState is not null &&
            !string.IsNullOrWhiteSpace(linkedState.OwnerKey))
        {
            var owner = linkedState.OwnerKey.Trim().ToLowerInvariant();
            agentTrackingProfileId = await db.AgentTrackingProfiles.AsNoTracking()
                .Where(profile =>
                    profile.AgentUserId.ToLower() == owner &&
                    profile.Status.ToLower() == "active")
                .Select(profile => (Guid?)profile.Id)
                .SingleOrDefaultAsync(ct);
            if (!agentTrackingProfileId.HasValue)
                return null;
        }

        var label = websiteDocument?.Store?.NavigationLabel?.Trim();
        if (string.IsNullOrWhiteSpace(label)) label = isParfait ? "Shop" : "Store";

        var root = isParfait ? "/store" : "/store/s/" + Uri.EscapeDataString(business.Key);
        var theme = websiteDocument?.Theme ?? new WebsiteThemeOverride();

        return new CommerceStoreContext(
            business.Id,
            publishedVersionId,
            agentTrackingProfileId,
            websiteSiteKey,
            business.Key,
            business.DisplayName,
            label,
            string.IsNullOrWhiteSpace(settings?.BrandHeadline) ? business.DisplayName : settings!.BrandHeadline,
            string.IsNullOrWhiteSpace(settings?.BrandSubheadline)
                ? business.DisplayName + " storefront."
                : settings!.BrandSubheadline,
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

    private static bool IsParfaitKey(string? key) =>
        string.Equals(key, ParfaitBusinessScopeService.ParfaitBusinessKey, StringComparison.OrdinalIgnoreCase);

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
