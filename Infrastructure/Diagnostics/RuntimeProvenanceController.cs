using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;

namespace Infrastructure.Diagnostics;

// Loaded process identity, never an App Service setting or a static stamp.
[ApiController]
[AllowAnonymous]
[Route("api/runtime-provenance")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class RuntimeProvenanceController(IHostEnvironment environment) : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var revision = version?.Split('+').LastOrDefault();
        if (revision is null || revision.Length != 40 || !revision.All(Uri.IsHexDigit))
            return StatusCode(503, new { schemaVersion = 1, appIdentifier = environment.ApplicationName, error = "source_identity_unavailable" });
        return Ok(new { schemaVersion = 1, appIdentifier = environment.ApplicationName, sourceRevision = revision.ToLowerInvariant() });
    }
}
