using AgentPortal.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;

namespace AgentPortal.Controllers;

public sealed partial class FounderEngineeringController
{
    [HttpGet("receptionist/guide")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> ReceptionistGuide(CancellationToken cancellationToken = default)
    {
        FounderGuard.EnsureFounderOrThrow(User);
        try
        {
            var guide = await ReceptionistOperator.SetupGuideForFounderAsync(cancellationToken);
            return Json(guide with { PublishUrl = Url.Action(nameof(PublishReceptionistGuide)) });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.Text.Json.JsonException or
            TimeoutException or System.Data.Common.DbException)
        { return StatusCode(503, new { message = "Current setup instructions are unavailable. Verify the current guide migration and database before publishing." }); }
    }

    [HttpPost("receptionist/guide/publish")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(262144)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> PublishReceptionistGuide([FromBody] ReceptionistSetupGuidePublishInput input,
        CancellationToken cancellationToken = default)
    {
        FounderGuard.EnsureFounderOrThrow(User);
        if (!ModelState.IsValid) return BadRequest(new { message = "Complete both guides within the displayed field limits before publishing." });
        try
        {
            var guide = await ReceptionistOperator.PublishSetupGuideAsync(input, cancellationToken);
            return Json(guide with { PublishUrl = Url.Action(nameof(PublishReceptionistGuide)) });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (DbUpdateConcurrencyException)
        { return Conflict(new { message = "A newer guide has been published. Your edits are retained. Load the current instructions and review the changes before publishing again." }); }
        catch (ArgumentException)
        { return BadRequest(new { message = "Complete both guides: a title, introduction and one to sixteen steps. Each step needs a title and instructions within the displayed limits." }); }
        catch (Exception error) when (error is InvalidOperationException or TimeoutException or System.Data.Common.DbException or DbUpdateException)
        { return StatusCode(503, new { message = "Publication could not be verified. Keep your edits and reload the current instructions to check the published version before trying again." }); }
    }
}
