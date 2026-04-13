using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AgentPortal.Controllers.Api;
using AgentPortal.Models;
using AgentPortal.Security;
using AgentPortal.Services.Tracking;
using Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentPortal.Tests;

public class AnalyticsIngestControllerTests
{
    private static AnalyticsIngestController BuildController(MasterAppDbContext db, string secret = "secret")
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Analytics:SharedSecret"] = secret
            })
            .Build();

        var resolver = new AgentTrackingResolver(db, NullLogger<AgentTrackingResolver>.Instance);
        var flags = Options.Create(new AppFeatureFlags { IngestHmacEnabled = false });
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var signatureValidator = new IngestSignatureValidator(memoryCache, config, NullLogger<IngestSignatureValidator>.Instance);
        var controller = new AnalyticsIngestController(
            db,
            config,
            resolver,
            NullLogger<AnalyticsIngestController>.Instance,
            flags,
            signatureValidator);

        var http = new DefaultHttpContext();
        http.Request.Headers["X-Shared-Secret"] = secret;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private static string? ReadStatus(object? value)
        => value?.GetType().GetProperty("status")?.GetValue(value)?.ToString();

    [Fact]
    public async Task Ingest_DuplicateClientEventId_ReturnsDuplicateIgnored()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var controller = BuildController(db);

        var clientEventId = Guid.NewGuid();

        var first = await controller.Ingest(new AnalyticsIngestController.AnalyticsEventRequest
        {
            ClientEventId = clientEventId,
            EventType = "page_view",
            Host = "test",
            Path = "/",
            EventUtc = DateTime.UtcNow
        });

        var second = await controller.Ingest(new AnalyticsIngestController.AnalyticsEventRequest
        {
            ClientEventId = clientEventId,
            EventType = "page_view",
            Host = "test",
            Path = "/",
            EventUtc = DateTime.UtcNow
        });

        var ok1 = Assert.IsType<OkObjectResult>(first);
        var ok2 = Assert.IsType<OkObjectResult>(second);

        Assert.Equal("ok", ReadStatus(ok1.Value));
        Assert.Equal("duplicate_ignored", ReadStatus(ok2.Value));
        Assert.Single(db.AnalyticsEvents);
    }
}
