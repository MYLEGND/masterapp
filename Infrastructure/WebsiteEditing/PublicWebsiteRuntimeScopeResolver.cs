using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

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
    private static readonly string ProtectHost =
        new Uri(Shared.Analytics.ProtectRouteCatalog.CanonicalOrigin).IdnHost;

    public static bool HasValidPublicOrigin(HttpContext context) =>
        TryOrigin(context.Request.Headers.Origin.ToString(), out _);

    public Task<PublicWebsiteRuntimeScope?> ResolveInquiryAsync(
        HttpContext context,
        CancellationToken cancellationToken) =>
        ResolveInquiryAsync(context, sourcePath: null, cancellationToken);

    public async Task<PublicWebsiteRuntimeScope?> ResolveInquiryAsync(
        HttpContext context,
        string? sourcePath = null,
        CancellationToken cancellationToken = default)
    {
        // The verified browser Origin + public page path selects the owner. The form
        // never supplies a permanent owner ID or tracking-profile ID.
        var legend = await ResolveAsync(context, WebsiteEditorSiteKeys.Legend, sourcePath, cancellationToken);
        if (legend is not null) return legend;
        var protect = await ResolveAsync(context, WebsiteEditorSiteKeys.Protect, sourcePath, cancellationToken);
        if (protect is not null) return protect;
        return await ResolveAsync(context, WebsiteEditorSiteKeys.Business, sourcePath, cancellationToken);
    }

    public Task<PublicWebsiteRuntimeScope?> ResolveAsync(
        HttpContext context,
        string? requestedSiteKey,
        CancellationToken cancellationToken) =>
        ResolveAsync(context, requestedSiteKey, sourcePath: null, cancellationToken);

    public async Task<PublicWebsiteRuntimeScope?> ResolveAsync(
        HttpContext context,
        string? requestedSiteKey,
        string? sourcePath = null,
        CancellationToken cancellationToken = default)
    {
        var siteKey = NormalizeSiteKey(requestedSiteKey);
        if (!TryOrigin(context.Request.Headers.Origin.ToString(), out var origin))
        {
            // Published business pages can call the Protect authority through the
            // verified custom host itself. GET requests do not always carry Origin.
            var publicHost = WebsiteRequestHostResolver.Resolve(context, configuration);
            if (siteKey != WebsiteEditorSiteKeys.Business || !context.Request.IsHttps ||
                string.IsNullOrWhiteSpace(publicHost))
                return null;
            origin = new Uri("https://" + publicHost);
        }
        if (siteKey == WebsiteEditorSiteKeys.Legend)
        {
            if (!LegendHosts.Contains(origin.IdnHost))
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

        if (siteKey == WebsiteEditorSiteKeys.Protect)
        {
            if (!origin.IdnHost.Equals(ProtectHost, StringComparison.OrdinalIgnoreCase))
                return null;

            var pagePath = NormalizePath(sourcePath) ?? RefererPath(context, origin) ?? "/";
            var profile = await ResolveProtectProfileAsync(pagePath, cancellationToken);
            if (profile is null)
                return null;

            var ownerKey = NormalizeOwner(profile.AgentUserId);
            var version = await PublishedAsync(ownerKey, WebsiteEditorSiteKeys.Protect, cancellationToken);
            if (version is null)
                return null;

            return new PublicWebsiteRuntimeScope(
                WebsiteEditorSiteKeys.Protect,
                ownerKey,
                null,
                version,
                origin.IdnHost,
                profile.Id,
                profile.Slug);
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
        {
            var canonical = Shared.Analytics.ProtectRouteCatalog.CanonicalPath(normalized);
            if (canonical.StartsWith("/a/", StringComparison.OrdinalIgnoreCase))
            {
                var end = canonical.IndexOf('/', 3);
                if (end < 0) return false;
                var scopedSlug = canonical[3..end];
                if (!string.Equals(scopedSlug, scope.AgentSlug, StringComparison.OrdinalIgnoreCase))
                    return false;
                canonical = canonical[end..];
            }
            return Shared.Analytics.ProtectRouteCatalog.Routes.Any(route =>
                route.Path.Equals(canonical, StringComparison.OrdinalIgnoreCase));
        }

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

    private async Task<AgentTrackingProfile?> ResolveProtectProfileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var slug = AgentSlugFromPath(path);
        if (!string.IsNullOrWhiteSpace(slug))
        {
            var normalized = slug.Trim().ToLowerInvariant();
            return await db.AgentTrackingProfiles.AsNoTracking()
                .SingleOrDefaultAsync(profile =>
                    profile.Status == "active" &&
                    profile.Slug.ToLower() == normalized,
                    cancellationToken);
        }

        var founderUpn = (configuration["Founder:Upn"] ?? string.Empty).Trim().ToLowerInvariant();
        if (founderUpn.Length == 0)
            return null;

        return await db.AgentTrackingProfiles.AsNoTracking()
            .SingleOrDefaultAsync(profile =>
                profile.Status == "active" &&
                profile.AgentUpn.ToLower() == founderUpn,
                cancellationToken);
    }

    private static string? AgentSlugFromPath(string? path)
    {
        var normalized = NormalizePath(path);
        if (normalized is null || !normalized.StartsWith("/a/", StringComparison.OrdinalIgnoreCase))
            return null;
        var remainder = normalized[3..];
        var slash = remainder.IndexOf('/');
        var slug = (slash < 0 ? remainder : remainder[..slash]).Trim();
        return slug.Length is > 0 and <= 160 &&
               slug.All(character => char.IsLetterOrDigit(character) || character is '-' or '_')
            ? slug
            : null;
    }

    private static string? RefererPath(HttpContext context, Uri origin)
    {
        var raw = context.Request.Headers.Referer.ToString();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var referer) ||
            referer.Scheme != Uri.UriSchemeHttps ||
            !referer.IdnHost.Equals(origin.IdnHost, StringComparison.OrdinalIgnoreCase))
            return null;
        return NormalizePath(referer.AbsolutePath);
    }

    private static string NormalizeOwner(string? value)
        => (value ?? string.Empty).Trim().ToLowerInvariant();

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
    string? AgentSlug = null);
