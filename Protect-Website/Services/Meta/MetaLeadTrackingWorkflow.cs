using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Shared.Meta;

namespace ProtectWebsite.Services.Meta;

public sealed class ThankYouMetaBrowserPixelAckRequest
{
    public Guid LeadId { get; set; }
    public string? EventId { get; set; }
    public string? Status { get; set; }
    public string? Note { get; set; }
}

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

        return string.IsNullOrWhiteSpace(cookieValue) ? null : cookieValue.Trim();
    }

    public static string NormalizeBrowserPixelStatus(string? status)
    {
        var normalized = status?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "sent" => "sent",
            "unavailable" => "unavailable",
            "error" => "error",
            _ => "unknown"
        };
    }

    public static string? NormalizeBrowserPixelNote(string? note)
    {
        var normalized = note?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "fbq_unavailable" => "fbq_unavailable",
            "fbq_exception" => "fbq_exception",
            "session_storage_dedup" => "session_storage_dedup",
            _ => string.IsNullOrWhiteSpace(normalized) ? null : "custom"
        };
    }
}
