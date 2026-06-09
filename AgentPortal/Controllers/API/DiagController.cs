using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Hosting;

namespace AgentPortal.Controllers.API
{
    [ApiController]
    [Route("api/diag")]
    [Authorize]
    public class DiagController : ControllerBase
    {
        [HttpGet("whoami")]
        public IActionResult WhoAmI()
        {
            if (!HttpContext.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment())
                return Forbid();
            var asm = typeof(DiagController).Assembly.GetName();
            return Ok(new
            {
                app = asm.Name,
                version = asm.Version?.ToString(),
                env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            });
        }

        [HttpGet("routes")]
        public IActionResult Routes([FromServices] IEnumerable<EndpointDataSource> sources)
        {
            if (!HttpContext.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment())
                return Forbid();
            var routes = sources
                .SelectMany(s => s.Endpoints)
                .OfType<RouteEndpoint>()
                .Select(e => e.RoutePattern.RawText)
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            return Ok(routes);
        }
    }
}
