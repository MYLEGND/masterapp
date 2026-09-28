using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Infrastructure.Analytics;

namespace Infrastructure.WebsiteEditing;

/// <summary>
/// Resolves a public LEGEND or business website from the browser origin to the
/// immutable published website owner/version. Browser-supplied owner IDs are
/// never accepted as authority.
/// </summary>
public sealed class PublicWebsiteRuntimeScopeResolver(MasterAppDbContext db, WebsiteDomainService domains, IConfiguration configuration)
{
    private static readonly HashSet<string> LegendHosts =
        new(StringComparer.OrdinalIgnoreCase) { "mylegnd.com", "www.mylegnd.com" };
    private const string ProtectHost = "protect.mylegnd.com";

    public static bool IsLegendHost(string? host) => !string.IsNullOrWhiteSpace(host) && LegendHosts.Contains(host);

    public static bool HasValidPublicOrigin(HttpContext context) =>
        TryOrigin(context.Request.Headers.Origin.ToString(), out _);

    public async Task<PublicWebsiteRuntimeScope?> ResolveInquiryAsync(
        HttpContext context,
        string? sourcePath = null,
        CancellationToken cancellationToken = default)
    {
        // The verified browser Origin selects the public owner. The form never sends
        // an owner ID or site key that could redirect an inquiry to another scope.
        var legend = await ResolveAsync(context, WebsiteEditorSiteKeys.Legend, cancellationToken);
        if (legend is not null) return legend;

        var business = await ResolveAsync(context, WebsiteEditorSiteKeys.Business, cancellationToken);
        if (business is not null) return business;

        if (!TryOrigin(context.Request.Headers.Origin.ToString(), out var origin))
            return null;

        if (string.Equals(origin.IdnHost, ProtectHost, StringComparison.OrdinalIgnoreCase))
        {
            var profiles = context.RequestServices.GetService<AgentTrackingResolver>();
            if (profiles is null) return null;
            var owner = await ProtectWebsiteOwnerResolver.ResolveAsync(
                context,
                profiles,
                configuration["Founder:Upn"],
                sourcePath,
                cancellationToken);
            if (owner is null || owner.Profile.Id == Guid.Empty || string.IsNullOrWhiteSpace(owner.Profile.AgentUserId))
                return null;

            var ownerKey = owner.Profile.AgentUserId.Trim().ToLowerInvariant();
            var version = await PublishedAsync(ownerKey, WebsiteEditorSiteKeys.Protect, cancellationToken);
            return new PublicWebsiteRuntimeScope(
                WebsiteEditorSiteKeys.Protect,
                ownerKey,
                null,
                version,
                origin.IdnHost,
                owner.Profile.Id,
                owner.Slug,
                owner.IsFounder,
                false);
        }

        var stores = context.RequestServices.GetService<ParfaitApp.Services.CommerceStoreContextService>();
        if (stores is not null)
        {
            var store = await stores.ResolveAnalyticsAsync(context, sourcePath, cancellationToken);
            if (store?.IsParfait == true && store.CommerceBusinessId != Guid.Empty)
            {
                WebsiteContentVersion? version = null;
                if (store.WebsiteContentVersionId is Guid versionId && versionId != Guid.Empty)
                    version = await db.Set<WebsiteContentVersion>().AsNoTracking()
                        .SingleOrDefaultAsync(x => x.Id == versionId, cancellationToken);

                return new PublicWebsiteRuntimeScope(
                    store.WebsiteSiteKey,
                    store.BusinessKey,
                    store.CommerceBusinessId,
                    version,
                    origin.IdnHost,
                    null,
                    null,
                    false,
                    true);
            }
        }

        return null;
    }

    public async Task<PublicWebsiteRuntimeScope?> ResolveAsync(
        HttpContext context,
        string? requestedSiteKey,
        CancellationToken cancellationToken = default)
    {
        var siteKey = NormalizeSiteKey(requestedSiteKey);
        if (!TryOrigin(context.Request.Headers.Origin.ToString(), out var origin))
        {
            // Published business pages can call the Protect authority through the
            // verified custom host itself. GET requests do not always carry Origin.
            var publicHost = WebsiteRequestHostResolver.Resolve(context, configuration);
            if (siteKey != WebsiteEditorSiteKeys.Business || !context.Request.IsHttps ||
                !(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)) ||
                context.Request.Headers.ContainsKey("Origin") || string.IsNullOrWhiteSpace(publicHost))
                return null;
            origin = new Uri("https://" + publicHost);
        }
        if (siteKey == WebsiteEditorSiteKeys.Legend)
        {
            if (!IsLegendHost(origin.IdnHost))
                return null;

            var version = await PublishedAsync(
                WebsiteEditorSiteKeys.GlobalOwnerKey,
                WebsiteEditorSiteKeys.Legend,
                cancellationToken);

            return new PublicWebsiteRuntimeScope(
                WebsiteEditorSiteKeys.Legend,
                WebsiteEditorSiteKeys.GlobalOwnerKey,
                null,
                version,
                origin.IdnHost);
        }

        if (siteKey == WebsiteEditorSiteKeys.Business)
        {
            var businessId = await domains.ResolveAsync(origin.IdnHost, cancellationToken);
            if (!businessId.HasValue || businessId == Guid.Empty)
                return null;

            var version = await WebsiteContentStore.PublishedBusinessAsync(db, businessId.Value, cancellationToken);
            if (version is null)
                return null;

            return new PublicWebsiteRuntimeScope(
                WebsiteEditorSiteKeys.Business,
                WebsiteEditorSiteKeys.BusinessOwnerKey(businessId.Value),
                businessId.Value,
                version,
                origin.IdnHost);
        }

        return null;
    }

    public static bool IsPublishedPath(PublicWebsiteRuntimeScope scope, string? path)
    {
        var normalized = NormalizePath(path);
        if (normalized is null)
            return false;

        if (scope.SiteKey == WebsiteEditorSiteKeys.Legend)
            return true;

        if (scope.SiteKey == WebsiteEditorSiteKeys.Protect)
            return IsContactPath(normalized, allowAgentPrefix: true);

        if (scope.IsCommerceApp)
            return IsContactPath(normalized, allowAgentPrefix: false);

        if (scope.SiteKey != WebsiteEditorSiteKeys.Business ||
            string.IsNullOrWhiteSpace(scope.PublishedVersion?.CompiledPagesJson))
            return false;

        try
        {
            using var document = JsonDocument.Parse(scope.PublishedVersion.CompiledPagesJson);
            return document.RootElement.TryGetProperty("pages", out var pages) &&
                   pages.ValueKind == JsonValueKind.Object &&
                   pages.TryGetProperty(normalized, out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsContactPath(string normalizedPath, bool allowAgentPrefix)
    {
        var path = normalizedPath;
        if (allowAgentPrefix && path.StartsWith("/a/", StringComparison.OrdinalIgnoreCase))
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length != 3 || !string.Equals(segments[2], "contact", StringComparison.OrdinalIgnoreCase))
                return false;
            return !string.IsNullOrWhiteSpace(segments[1]);
        }

        return string.Equals(path, "/contact", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<WebsiteContentVersion?> PublishedAsync(
        string ownerKey,
        string siteKey,
        CancellationToken cancellationToken)
    {
        return await (
            from state in db.Set<WebsiteContentState>().AsNoTracking()
            join version in db.Set<WebsiteContentVersion>().AsNoTracking()
                on state.PublishedVersionId equals version.Id
            where state.OwnerKey == ownerKey &&
                  state.SiteKey == siteKey &&
                  version.StateId == state.Id
            select version)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static string NormalizeSiteKey(string? value)
        => (value ?? string.Empty).Trim().ToLowerInvariant();

    private static string? NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "/";

        var path = value.Trim();
        if (path.Length > 160 || !path.StartsWith('/') || path.StartsWith("//") ||
            path.Contains('?') || path.Contains('#') || path.Contains('\\') ||
            path.Any(char.IsControl))
            return null;

        path = path.TrimEnd('/');
        return path.Length == 0 ? "/" : path;
    }

    private static bool TryOrigin(string? raw, out Uri origin)
    {
        origin = null!;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps ||
            !parsed.IsDefaultPort ||
            parsed.AbsolutePath != "/" ||
            parsed.Query.Length != 0 ||
            parsed.Fragment.Length != 0 ||
            parsed.UserInfo.Length != 0)
            return false;

        origin = parsed;
        return true;
    }
}

public sealed record PublicWebsiteRuntimeScope(
    string SiteKey,
    string OwnerKey,
    Guid? CommerceBusinessId,
    WebsiteContentVersion? PublishedVersion,
    string OriginHost,
    Guid? AgentTrackingProfileId = null,
    string? AgentSlug = null,
    bool IsFounder = false,
    bool IsCommerceApp = false);
