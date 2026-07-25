using ClientApp.Services;
using Domain.JourneyCircles;
using Domain.Messaging;
using Infrastructure.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClientApp.Controllers;

[Authorize]
[Route("JourneyCircles")]
public sealed class JourneyCirclesController : Controller
{
    private readonly EffectiveClientContextService _context;
    private readonly IJourneyCirclesService _journeys;
    private readonly IMessagingProfileImageResolver _images;
    private readonly global::Infrastructure.Data.MasterAppDbContext _db;

    public JourneyCirclesController(EffectiveClientContextService context, IJourneyCirclesService journeys, IMessagingProfileImageResolver images, global::Infrastructure.Data.MasterAppDbContext db)
    {
        _context = context; _journeys = journeys; _images = images; _db = db;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var client = await CurrentAsync(); if (client is null) return Forbid();
        return RedirectToAction("Index", "Home", new { openMessages = "1", journeyCircles = "open" });
    }

    [HttpGet("Modal")]
    public async Task<IActionResult> Modal()
    {
        var client = await CurrentAsync();
        if (client is null) return Forbid();
        return Ok(await _journeys.GetDashboardAsync(client.ClientUserId, HttpContext.RequestAborted));
    }

    [HttpPost("Profile")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveProfile(JourneyCircleProfileInput input)
    {
        var client = await CurrentAsync(); if (client is null) return Forbid();
        return await FinishAsync(client, await _journeys.SaveProfileAsync(client.ClientUserId, input, HttpContext.RequestAborted));
    }

    [HttpPost("Connections")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestConnection(Guid targetClientProfileId, string? reason, string? introduction)
    {
        var client = await CurrentAsync(); if (client is null) return Forbid();
        return await FinishAsync(client, await _journeys.RequestConnectionAsync(client.ClientUserId, targetClientProfileId, reason, introduction, HttpContext.RequestAborted));
    }

    [HttpPost("Connections/{connectionId:guid}/Response")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Respond(Guid connectionId, bool accept)
    {
        var client = await CurrentAsync(); if (client is null) return Forbid();
        return await FinishAsync(client, await _journeys.RespondToConnectionAsync(client.ClientUserId, connectionId, accept, HttpContext.RequestAborted));
    }

    [HttpPost("Connections/{connectionId:guid}/Disconnect")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Disconnect(Guid connectionId)
    {
        var client = await CurrentAsync(); if (client is null) return Forbid();
        return await FinishAsync(client, await _journeys.DisconnectAsync(client.ClientUserId, connectionId, HttpContext.RequestAborted));
    }

    [HttpPost("Profiles/{targetClientProfileId:guid}/Block")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Block(Guid targetClientProfileId)
    {
        var client = await CurrentAsync(); if (client is null) return Forbid();
        return await FinishAsync(client, await _journeys.BlockAsync(client.ClientUserId, targetClientProfileId, HttpContext.RequestAborted));
    }

    [HttpPost("Profiles/{targetClientProfileId:guid}/Report")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Report(Guid targetClientProfileId, string category, string? detail)
    {
        var client = await CurrentAsync(); if (client is null) return Forbid();
        return await FinishAsync(client, await _journeys.ReportAsync(client.ClientUserId, targetClientProfileId, category, detail, HttpContext.RequestAborted));
    }

    [HttpGet("Profiles/{targetClientProfileId:guid}/Avatar")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> Avatar(Guid targetClientProfileId)
    {
        var client = await CurrentAsync(); if (client is null) return Forbid();
        var dashboard = await _journeys.GetDashboardAsync(client.ClientUserId, HttpContext.RequestAborted);
        var visible = dashboard.Recommendations.Select(x => x.Profile).Concat(dashboard.Connections.Select(x => x.Profile)).Concat(dashboard.Requests.Select(x => x.Profile)).Any(x => x.ClientProfileId == targetClientProfileId);
        if (!visible) return Forbid();
        var target = await _db.ClientProfiles.FindAsync([targetClientProfileId], HttpContext.RequestAborted); if (target is null) return NotFound();
        var image = await _images.ResolveClientProfileImageAsync(target.Id, HttpContext.RequestAborted);
        if (image is null)
            return NotFound();

        if (image.Content is { Length: > 0 })
            return File(image.Content, image.ContentType);

        return string.IsNullOrWhiteSpace(image.PhysicalPath)
            ? NotFound()
            : PhysicalFile(image.PhysicalPath, image.ContentType);
    }

    private async Task<EffectiveClientContext?> CurrentAsync()
    {
        var client = await _context.ResolveAsync(User, Request.Cookies, allowRelink: false);
        return client is { IsAgentView: false } ? client : null;
    }

    private void SetResult(JourneyCircleOperationResult result)
    {
        TempData[result.Succeeded ? "JourneySuccess" : "JourneyError"] = result.Succeeded ? "Journey Circles updated." : result.ErrorMessage;
    }

    private async Task<IActionResult> FinishAsync(EffectiveClientContext client, JourneyCircleOperationResult result)
    {
        if (Request.Headers.TryGetValue("X-Journey-Circles-Modal", out var modal) && modal == "1")
        {
            if (!result.Succeeded)
                return BadRequest(new { result.ErrorCode, result.ErrorMessage });

            return Ok(await _journeys.GetDashboardAsync(client.ClientUserId, HttpContext.RequestAborted));
        }

        SetResult(result);
        return RedirectToAction("Index", "Home", new { openMessages = "1", journeyCircles = "open" });
    }
}
