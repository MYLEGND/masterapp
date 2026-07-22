using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Shared.Meta;

namespace ProtectWebsite.Services.Meta;

public static class MetaLeadTrackingWorkflow
{
    public static async Task TryPersistAsync(
        WebsiteLead lead,
        MasterAppDbContext db,
        Guid correlationId,
        string stage,
        ILogger logger,
        CancellationToken cancellationToken,
        Action<MetaLeadTrackingState> mutate)
    {
        try
        {
            lead.MetadataJson = MetaLeadTrackingJson.Upsert(lead.MetadataJson, mutate);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception metaPersistEx)
        {
            logger.LogError(
                metaPersistEx,
                "Meta tracking persistence failed correlationId={CorrelationId} stage={Stage} lead={LeadId}",
                correlationId, stage, lead.LeadId);
        }
    }

    public static string? ResolveEventSourceUrl(string? landingPageUrl, HttpRequest? request)
    {
        if (!string.IsNullOrWhiteSpace(landingPageUrl) &&
            Uri.TryCreate(landingPageUrl.Trim(), UriKind.Absolute, out var landingUri))
        {
            return landingUri.ToString();
        }

        var referer = request?.Headers.Referer.ToString();
        if (!string.IsNullOrWhiteSpace(referer) &&
            Uri.TryCreate(referer.Trim(), UriKind.Absolute, out var refererUri))
        {
            return refererUri.ToString();
        }

        if (request == null || !request.Host.HasValue)
            return null;

        return $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}{request.QueryString}";
    }

    public static string? ResolveClientIpAddress(HttpRequest? request)
    {
        static string? FirstHeaderValue(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
        }

        if (request == null)
            return null;

        return FirstHeaderValue(request.Headers["X-Forwarded-For"].ToString())
            ?? FirstHeaderValue(request.Headers["X-Real-IP"].ToString())
            ?? FirstHeaderValue(request.Headers["CF-Connecting-IP"].ToString())
            ?? request.HttpContext.Connection.RemoteIpAddress?.ToString();
    }

    public static string? ResolveCookieValue(HttpRequest? request, string cookieName)
    {
        if (string.IsNullOrWhiteSpace(cookieName))
            return null;

        if (request?.Cookies.TryGetValue(cookieName, out var cookieValue) != true)
            return null;

        if (string.IsNullOrEmpty(cookieValue))
            return null;

        // Preserve Meta _fbc/_fbp exactly. Do not trim/lowercase/decode/re-encode.
        return cookieValue;
    }

}
