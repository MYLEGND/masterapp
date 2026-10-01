using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Shared.Diagnostics;

namespace Infrastructure.Diagnostics;

/// <summary>
/// Public structural page diagnostics for public LEGEND web surfaces. It has no
/// account, repository, configuration, repair, CRM, analytics or mutation access.
/// Browser observations are untrusted and pass through the same server disclosure
/// authority before anything is returned.
/// </summary>
[ApiController]
[AllowAnonymous]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/legend-public-site-tools")]
public sealed class PublicLegendSiteToolsController(
    IHostEnvironment environment,
    IEnumerable<EndpointDataSource> endpointSources) : ControllerBase
{
    [HttpGet("catalog")]
    public IActionResult Catalog() => Ok(new
    {
        schemaVersion = 1,
        authority = nameof(LegendSiteToolDisclosureAuthority),
        authentication = "public_structural_only",
        mutationToolsExposed = false,
        systemToolsExposed = false,
        tools = new[] { LegendSiteToolDisclosureAuthority.CurrentPageTool }
    });

    [HttpPost("execute")]
    [RequestSizeLimit(32 * 1024)]
    public IActionResult Execute([FromBody] PublicLegendSiteToolExecutionRequest request)
    {
        if (request is null ||
            !string.Equals(request.Name, LegendSiteToolDisclosureAuthority.CurrentPageToolName, StringComparison.Ordinal))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "legend_site_tool_not_exposed" });

        var application = LegendSiteToolDisclosureAuthority.ResolvePublicApplication(
            request.Page?.Application, environment.ApplicationName);
        if (application is null)
            return BadRequest(new { error = "legend_public_site_application_invalid" });

        var route = LegendSiteToolDisclosureAuthority.ResolvePublicRouteAuthority(
            application, request.Page?.Path, endpointSources);

        return Ok(LegendSiteToolDisclosureAuthority.SanitizePage(
            request.Page,
            application,
            "public_structural",
            application == "Legend-Website"
                ? LegendSiteToolDisclosureAuthority.SafeBrowserRevision(request.Page?.SourceRevision)
                : LegendSiteToolDisclosureAuthority.EntryAssemblyRevision(),
            route));
    }
}

public sealed record PublicLegendSiteToolExecutionRequest(
    string? Name,
    System.Text.Json.JsonElement Arguments,
    LegendSitePageSnapshot? Page);
