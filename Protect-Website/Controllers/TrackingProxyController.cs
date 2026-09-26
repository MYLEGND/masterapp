using Infrastructure.Analytics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ProtectWebsite.Controllers;

[ApiController]
[AllowAnonymous]
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("public-ingest")]
public sealed class TrackingProxyController : WebsiteTrackingProxyAuthority
{
    public TrackingProxyController(IHttpClientFactory httpClientFactory, IConfiguration config,
        ILogger<TrackingProxyController> logger, AgentTrackingResolver resolver)
        : base(httpClientFactory, config, logger, resolver) { }
}
