using AgentPortal.Models.Analytics;
using AgentPortal.Services.Analytics;
using AgentPortal.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Controllers.API;

[Authorize]
[ApiController]
[Route("WebsiteAnalytics")]
public sealed class VisitorConcentrationController : ControllerBase
{
    private readonly IVisitorConcentrationService _visitorConcentrationService;
    private readonly EffectiveAgentContext _effectiveContext;

    public VisitorConcentrationController(
        IVisitorConcentrationService visitorConcentrationService,
        EffectiveAgentContext effectiveContext)
    {
        _visitorConcentrationService = visitorConcentrationService;
        _effectiveContext = effectiveContext;
    }

    [HttpGet("VisitorConcentration")]
    public async Task<IActionResult> Get(
        [FromQuery] string preset = "today",
        [FromQuery] DateTime? fromUtc = null,
        [FromQuery] DateTime? toUtc = null,
        [FromQuery] string? timezoneId = null,
        [FromQuery] int? timezoneOffsetMinutes = null,
        [FromQuery] Guid? agentProfileId = null,
        [FromQuery] TrafficType trafficType = TrafficType.All,
        CancellationToken ct = default)
    {
        var range = ResolveRange(
            preset,
            fromUtc,
            toUtc,
            timezoneId,
            timezoneOffsetMinutes);

        var scope = await _effectiveContext.ResolveScopeAsync(agentProfileId);

        var payload =
            await _visitorConcentrationService.GetVisitorConcentrationPayloadAsync(
                new TimeRangeRequest
                {
                    FromUtc = range.FromUtc,
                    ToUtc = range.ToUtc,
                    ViewerTimeZone = range.ViewerTimeZone
                },
                scope,
                trafficType,
                ct);

        return Ok(payload);
    }

    private static VisitorRange ResolveRange(
        string preset,
        DateTime? fromUtc,
        DateTime? toUtc,
        string? timezoneId,
        int? timezoneOffsetMinutes)
    {
        var tz = ResolveTimeZone(timezoneId, timezoneOffsetMinutes);
        var nowUtc = DateTime.UtcNow;

        if (fromUtc.HasValue && toUtc.HasValue)
        {
            return new VisitorRange(
                DateTime.SpecifyKind(fromUtc.Value, DateTimeKind.Utc),
                DateTime.SpecifyKind(toUtc.Value, DateTimeKind.Utc),
                tz);
        }

        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        var todayLocal = nowLocal.Date;

        DateTime startLocal;
        DateTime endLocal;

        switch ((preset ?? "today").Trim().ToLowerInvariant())
        {
            case "7d":
            case "last7":
                startLocal = todayLocal.AddDays(-6);
                endLocal = todayLocal.AddDays(1);
                break;

            case "30d":
            case "last30":
                startLocal = todayLocal.AddDays(-29);
                endLocal = todayLocal.AddDays(1);
                break;

            case "yesterday":
                startLocal = todayLocal.AddDays(-1);
                endLocal = todayLocal;
                break;

            default:
                startLocal = todayLocal;
                endLocal = todayLocal.AddDays(1);
                break;
        }

        return new VisitorRange(
            TimeZoneInfo.ConvertTimeToUtc(startLocal, tz),
            TimeZoneInfo.ConvertTimeToUtc(endLocal, tz),
            tz);
    }

    private static TimeZoneInfo ResolveTimeZone(
        string? timezoneId,
        int? timezoneOffsetMinutes)
    {
        if (!string.IsNullOrWhiteSpace(timezoneId))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timezoneId.Trim());
            }
            catch
            {
            }
        }

        if (timezoneOffsetMinutes.HasValue)
        {
            var offset = TimeSpan.FromMinutes(-timezoneOffsetMinutes.Value);

            return TimeZoneInfo.CreateCustomTimeZone(
                "viewer-offset",
                offset,
                "viewer-offset",
                "viewer-offset");
        }

        return TimeZoneInfo.Utc;
    }

    private sealed record VisitorRange(
        DateTime FromUtc,
        DateTime ToUtc,
        TimeZoneInfo ViewerTimeZone);
}
