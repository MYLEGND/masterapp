using Microsoft.AspNetCore.Mvc;
using ParfaitApp.Models;
using ParfaitApp.Services;

namespace ParfaitApp.Controllers;

[Route("parfait-analytics")]
public sealed class ParfaitAnalyticsController : Controller
{
    private readonly ParfaitAnalyticsService _analytics;

    public ParfaitAnalyticsController(ParfaitAnalyticsService analytics)
    {
        _analytics = analytics;
    }

    [HttpPost("track")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Track([FromBody] ParfaitAnalyticsEventRequest request, CancellationToken ct)
    {
        await _analytics.TrackAsync(request, HttpContext, ct);
        return Ok(new { success = true });
    }
}
