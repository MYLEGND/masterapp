using Domain.Moderation;
using Domain.Social;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Controllers.Admin;

/// <summary>
/// Founder-controlled review surface for the one community report queue. It
/// records a staff disposition only; content removal and account sanctions
/// remain deliberate policy actions rather than an unreviewed side effect of a
/// member report.
/// </summary>
[ApiController]
[Authorize(Policy = "FounderOnly")]
[Route("admin/community-moderation")]
public sealed class CommunityModerationController : ControllerBase
{
    private readonly ICommunitySafetyService _communitySafety;
    private readonly ISocialFeedService _social;

    public CommunityModerationController(
        ICommunitySafetyService communitySafety,
        ISocialFeedService social)
    {
        _communitySafety = communitySafety;
        _social = social;
    }

    [HttpGet("reports")]
    public async Task<ActionResult<IReadOnlyList<CommunitySafetyReportView>>> GetOpenReports(
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default) =>
        Ok(await _communitySafety.GetOpenReportsAsync(take, cancellationToken));

    [HttpPost("reports/{reportId:guid}/resolution")]
    public async Task<IActionResult> ResolveReport(
        Guid reportId,
        [FromBody] CommunityModerationResolutionRequest? request,
        CancellationToken cancellationToken)
    {
        var moderatorUserId = User.FindFirst("oid")?.Value;
        if (request is null || string.IsNullOrWhiteSpace(moderatorUserId))
            return BadRequest(new { error = "community_report_resolution_invalid" });

        var report = await _communitySafety.GetOpenReportAsync(reportId, cancellationToken);
        if (report is null)
            return NotFound(new { error = "community_report_not_found" });
        if (string.Equals(request.Resolution, CommunitySafetyReviewResolutions.Actioned, StringComparison.Ordinal) &&
            report.TargetKind == CommunitySafetyTargetKinds.SocialPost)
        {
            var removal = await _social.RemoveReportedPostAsync(report.TargetEntityId ?? Guid.Empty, cancellationToken);
            if (!removal.Succeeded)
            {
                return Conflict(new
                {
                    error = removal.ErrorCode,
                    message = removal.ErrorMessage
                });
            }
        }

        var result = await _communitySafety.ResolveReportAsync(
            reportId,
            moderatorUserId,
            request.Resolution ?? string.Empty,
            cancellationToken);
        return result.Succeeded
            ? NoContent()
            : BadRequest(new { error = result.ErrorCode, message = result.ErrorMessage });
    }
}

public sealed record CommunityModerationResolutionRequest(string? Resolution);
