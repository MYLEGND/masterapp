using Infrastructure.Analytics;
using Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;

namespace Protect_Website.Controllers;

[Route("analytics")]
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("public-ingest")]
public sealed class AnalyticsController : WebsiteAnalyticsIngestAuthority
{
    public AnalyticsController(MasterAppDbContext db, ILogger<AnalyticsController> logger,
        IConfiguration? configuration = null) : base(db, logger, configuration) { }
}
