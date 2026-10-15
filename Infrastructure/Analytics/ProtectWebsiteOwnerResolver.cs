using Domain.Entities;
using Microsoft.AspNetCore.Http;

namespace Infrastructure.Analytics;

/// <summary>Protect owner authority, independent of advertising configuration.</summary>
public static class ProtectWebsiteOwnerResolver
{
    public sealed record Owner(AgentTrackingProfile Profile, string? Slug, bool IsFounder);

    public static async Task<Owner?> ResolveAsync(HttpContext? context, AgentTrackingResolver profiles,
        string? founderUpn, string? sourcePath = null, CancellationToken ct = default)
    {
        if (context is null) return null;
        // A route or same-origin referring page outranks middleware's root-route
        // context on API POSTs. Invalid explicit scope never falls back to Founder.
        var path = context.Request.Path.Value;
        var referrer = SameOriginReferrerPath(context.Request);
        var scopedPath = IsScopedPath(path) ? path : IsScopedPath(referrer) ? referrer
            : IsScopedPath(sourcePath) ? sourcePath : null;
        if (scopedPath is not null)
        {
            var slug = ExtractSlug(scopedPath);
            if (slug is null) return null;
            return From(await profiles.ResolveBySlugAsync(slug, ct), founderUpn);
        }
        if (HttpMethods.IsPost(context.Request.Method) && context.Request.HasFormContentType)
        {
            // Legacy quote forms declare their scoped owner explicitly. Resolve it
            // strictly rather than falling through a root-route middleware marker.
            var form = await context.Request.ReadFormAsync(ct);
            var formSlug = form["AgentSlug"].ToString().Trim();
            var formId = form["AgentTrackingProfileId"].ToString().Trim();
            Guid? profileId = null;
            if (!string.IsNullOrEmpty(formId))
            {
                if (!Guid.TryParse(formId, out var parsedId)) return null;
                profileId = parsedId;
            }
            if (profileId.HasValue || !string.IsNullOrEmpty(formSlug))
                return From(await profiles.ResolveAsync(formSlug, profileId, ct), founderUpn);
        }
        if (context.Items["TrackingProfile"] is AgentTrackingProfile profile &&
            context.Items["IsFounderPath"] as bool? != true)
            return From(await profiles.ResolveByIdAsync(profile.Id, ct), founderUpn);
        if (string.IsNullOrWhiteSpace(founderUpn)) return null;
        return From(await profiles.ResolveByUpnAsync(founderUpn, ct), founderUpn);
    }

    public static async Task<Owner?> ResolveLeadAsync(AgentTrackingResolver profiles, string? founderUpn,
        Guid? profileId, string? slug, bool isFounderPath, CancellationToken ct = default)
    {
        if (profileId.HasValue || !string.IsNullOrWhiteSpace(slug))
        {
            var owner = From(await profiles.ResolveAsync(slug?.Trim(), profileId, ct), founderUpn);
            // A server Founder marker cannot turn another explicit owner into Founder.
            return isFounderPath && owner?.IsFounder != true ? null : owner;
        }
        return isFounderPath && !string.IsNullOrWhiteSpace(founderUpn)
            ? From(await profiles.ResolveByUpnAsync(founderUpn, ct), founderUpn) : null;
    }

    public static string? SameOriginReferrerPath(HttpRequest request)
    {
        var value = request.Headers.Referer.ToString();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(uri.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase) ? uri.AbsolutePath : null;
        return value.StartsWith('/') && !value.StartsWith("//") ? value : null;
    }

    private static Owner? From(ResolveResult result, string? founderUpn) => result.Found && result.Profile is not null
        ? new(result.Profile, result.CanonicalSlug ?? result.Profile.Slug,
            !string.IsNullOrWhiteSpace(founderUpn) && string.Equals(result.Profile.AgentUpn, founderUpn, StringComparison.OrdinalIgnoreCase))
        : null;

    private static bool IsScopedPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)) value = uri.AbsolutePath;
        return value.Equals("/a", StringComparison.OrdinalIgnoreCase) || value.StartsWith("/a/", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractSlug(string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri)) path = uri.AbsolutePath;
        var segments = path.Split('?', '#')[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length < 2 ? null : Uri.UnescapeDataString(segments[1]).Trim();
    }
}
