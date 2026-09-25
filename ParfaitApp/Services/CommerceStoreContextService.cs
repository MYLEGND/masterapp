using System.Text.Json;
using Domain.Entities;
using Infrastructure.Businesses;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

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
/// Normal public storefronts resolve from the authenticated public hostname and therefore live
/// at /store on that hostname. The scoped /store/s/{businessKey} route is retained only for
/// Protect's shared agent host and legacy compatibility.
/// </summary>
public sealed class CommerceStoreContextService(
    MasterAppDbContext db,
    CommerceBusinessScopeResolver businesses,
    ParfaitBusinessScopeService parfaitScope,
    WebsiteDomainService domains,
    IConfiguration configuration)
{
    private static readonly HashSet<string> LegendHosts =
        new(StringComparer.OrdinalIgnoreCase) { "mylegnd.com", "www.mylegnd.com" };
    private const string ProtectHost = "protect.mylegnd.com";
    private const string ParfaitAzureHost = "masterapp-parfait.azurewebsites.net";

    public async Task<CommerceStoreContext?> ResolvePublicAsync(string? businessKey, CancellationToken ct = default)
    {
        var normalized = NormalizeKey(businessKey);
        CommerceBusiness? business;
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized == ParfaitBusinessScopeService.ParfaitBusinessKey)
        {
            business = await parfaitScope.GetParfaitAsync(ct);
            return await BuildAsync(business, publishedOnly: false, ct, useScopedPath: false);
        }

        business = await businesses.ResolveActiveByKeyAsync(normalized, ct);
        if (business is null) return null;
        return await BuildAsync(business, publishedOnly: true, ct, useScopedPath: true);
    }

    public async Task<CommerceStoreContext?> ResolvePublicAsync(
        HttpContext context,
        string? businessKey,
        CancellationToken ct = default)
    {
        var normalized = NormalizeKey(businessKey);
        if (!string.IsNullOrWhiteSpace(normalized))
            return await ResolvePublicAsync(normalized, ct);

        var host = WebsiteRequestHostResolver.Resolve(context, configuration, allowLegendCommerceHost: true);
        if (IsParfaitHost(host))
        {
            var parfait = await parfaitScope.GetParfaitAsync(ct);
            return await BuildAsync(parfait, publishedOnly: false, ct, useScopedPath: false);
        }

        if (LegendHosts.Contains(host))
        {
            var state = await db.Set<WebsiteContentState>().AsNoTracking()
                .SingleOrDefaultAsync(
                    x => x.OwnerKey == WebsiteEditorSiteKeys.GlobalOwnerKey &&
                         x.SiteKey == WebsiteEditorSiteKeys.Legend,
                    ct);
            if (state?.CommerceBusinessId is not Guid legendBusinessId || legendBusinessId == Guid.Empty)
                return null;

            var business = await businesses.ResolveActiveByIdAsync(legendBusinessId, ct);
            if (business is null) return null;

            return await BuildAsync(
                business,
                publishedOnly: true,
                ct,
                explicitSiteKey: WebsiteEditorSiteKeys.Legend,
                explicitState: state,
                useScopedPath: false);
        }

        // Protect is the one shared-host exception. It must retain an explicit scoped
        // business key so one agent cannot select another agent's store by hostname alone.
        if (string.Equals(host, ProtectHost, StringComparison.OrdinalIgnoreCase))
            return null;

        var businessId = await domains.ResolveAsync(host, ct);
        if (!businessId.HasValue || businessId == Guid.Empty)
            return null;

        var scopedBusiness = await businesses.ResolveActiveByIdAsync(businessId.Value, ct);
        if (scopedBusiness is null) return null;

        return await BuildAsync(scopedBusiness, publishedOnly: true, ct, useScopedPath: false);
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
            explicitState: state,
            useScopedPath: true);
    }

    public bool IsCentralCommerceHost(HttpContext context)
    {
        var host = WebsiteRequestHostResolver.Resolve(context, configuration, allowLegendCommerceHost: true);
        return IsParfaitHost(host);
    }

    public async Task<string?> ResolveCanonicalPublicRootAsync(
        CommerceStoreContext store,
        CancellationToken ct = default)
    {
        if (store.IsParfait)
            return PublicBase("Commerce:PublicBaseUrl", "https://shopparfait.com") + "/store";

        if (string.Equals(store.WebsiteSiteKey, WebsiteEditorSiteKeys.Legend, StringComparison.OrdinalIgnoreCase))
            return PublicBase("Commerce:LegendPublicBaseUrl", "https://mylegnd.com") + "/store";

        if (string.Equals(store.WebsiteSiteKey, WebsiteEditorSiteKeys.Protect, StringComparison.OrdinalIgnoreCase))
            return PublicBase("Commerce:ProtectPublicBaseUrl", "https://protect.mylegnd.com") +
                   "/store/s/" + Uri.EscapeDataString(store.BusinessKey);

        if (!string.Equals(store.WebsiteSiteKey, WebsiteEditorSiteKeys.Business, StringComparison.OrdinalIgnoreCase))
            return null;

        var cutoff = DateTime.UtcNow.AddHours(-24);
        var hostname = await db.Set<WebsiteDomainBinding>().AsNoTracking()
            .Where(x =>
                x.CommerceBusinessId == store.CommerceBusinessId &&
                x.Status == "active" &&
                x.CertificateStatus == "active" &&
                x.LastCheckedUtc >= cutoff)
            .OrderBy(x => x.CreatedUtc)
            .Select(x => x.Hostname)
            .FirstOrDefaultAsync(ct);

        return string.IsNullOrWhiteSpace(hostname) ? null : "https://" + hostname + "/store";
    }

    private async Task<CommerceStoreContext?> BuildAsync(
        CommerceBusiness business,
        bool publishedOnly,
        CancellationToken ct,
        WebsiteContentDocument? explicitDocument = null,
        string? explicitSiteKey = null,
        WebsiteContentState? explicitState = null,
        bool useScopedPath = false)
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

        var root = !isParfait && useScopedPath
            ? "/store/s/" + Uri.EscapeDataString(business.Key)
            : "/store";
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

    private string PublicBase(string key, string fallback)
    {
        var configured = configuration[key]?.Trim();
        if (!string.IsNullOrWhiteSpace(configured) &&
            Uri.TryCreate(configured, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps &&
            string.IsNullOrWhiteSpace(uri.UserInfo))
            return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        return fallback;
    }

    private bool IsParfaitHost(string host)
    {
        if (string.Equals(host, ParfaitAzureHost, StringComparison.OrdinalIgnoreCase))
            return true;

        var configured = PublicBase("Commerce:PublicBaseUrl", "https://shopparfait.com");
        return Uri.TryCreate(configured, UriKind.Absolute, out var uri) &&
               (string.Equals(host, uri.IdnHost, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(host, "www." + uri.IdnHost, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeKey(string? value) => (value ?? "").Trim().ToLowerInvariant();

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
