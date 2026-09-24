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
    string OriginHost);
