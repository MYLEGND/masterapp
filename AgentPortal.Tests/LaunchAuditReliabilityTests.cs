using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProtectWebsite.Services.Meta;
using Infrastructure.Analytics;
using Protect_Website.Models;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LaunchAuditReliabilityTests
{
    [Theory]
    [InlineData(200, "{\"events_received\":1,\"fbtrace_id\":\"test-trace\"}", true, false)]
    [InlineData(200, "{\"events_received\":0}", false, false)]
    [InlineData(200, "{}", false, false)]
    [InlineData(200, "invalid", false, false)]
    [InlineData(400, "{}", false, false)]
    [InlineData(503, "{}", false, true)]
    [InlineData(429, "{}", false, true)]
    public async Task MetaAcceptanceRequiresAcknowledgementAndClassifiesRetry(int status, string body, bool accepted, bool retryable)
    {
        var authority = new Mock<IMetaSendAuthority>();
        authority.Setup(x => x.TrySendAsync(It.IsAny<MetaSendAuthorityRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetaSendAuthorityDecision(true, "Lead", null,
                MetaSendAuthoritySources.MetaSignalOutcomeDispatcherHostedService, "test-key", "test-token", "allowed", null));
        var service = new MetaConversionsApiService(new HttpClient(new ResponseHandler(status, body)),
            Options.Create(new MetaOptions { PixelId = "test-pixel", AccessToken = "test-token" }),
            authority.Object, NullLogger<MetaConversionsApiService>.Instance);
        var result = await service.SendEventAsync(new MetaConversionsApiEventRequest
        {
            EventName = "Lead", EventId = Guid.NewGuid().ToString("N"), EventUtc = DateTime.UtcNow,
            AuthoritySource = MetaSendAuthoritySources.MetaSignalOutcomeDispatcherHostedService
        });
        Assert.Equal(accepted, result.Sent);
        Assert.Equal(retryable, result.Retryable);
        authority.Verify(x => x.Complete(It.IsAny<MetaSendAuthorityDecision>(), accepted), Times.Once);
    }

    [Fact]
    public async Task UnavailableDedupeAuthorityDoesNotGrantPermission()
    {
        var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await db.DisposeAsync();
        var service = new MetaSendAuthority(db, NullLogger<MetaSendAuthority>.Instance);
        var decision = await service.TrySendAsync(new MetaSendAuthorityRequest
        {
            EventType = "Lead", EventId = Guid.NewGuid().ToString("N"), EventUtc = DateTime.UtcNow,
            Source = MetaSendAuthoritySources.MetaSignalOutcomeDispatcherHostedService
        });
        Assert.False(decision.Allowed);
        Assert.Equal("authority_unavailable", decision.Status);
    }

    [Fact]
    public async Task BrowserIngestReplayPersistsOnceAndNeverPromotesServerAuthority()
    {
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var controller = new Protect_Website.Controllers.AnalyticsController(db,
            NullLogger<Protect_Website.Controllers.AnalyticsController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var request = new MetaSignalIngestRequest
        {
            EventId = Guid.NewGuid().ToString("N"), EventName = "ViewContent",
            SessionId = "qa-session", VisitorId = "qa-visitor"
        };
        Assert.IsType<JsonResult>(await controller.MetaSignal(request, CancellationToken.None));
        var duplicate = Assert.IsType<JsonResult>(await controller.MetaSignal(request, CancellationToken.None));
        var result = Assert.IsType<MetaSignalProcessResult>(duplicate.Value);
        Assert.True(result.Accepted);
        Assert.True(result.Skipped);
        Assert.False(result.MetaServerSent);
        Assert.Single(await db.AnalyticsEvents.ToListAsync());
        request.SessionId = "different-session";
        Assert.IsType<ConflictObjectResult>(await controller.MetaSignal(request, CancellationToken.None));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void RiskAssessmentRequiresAffirmativeConsent(bool consent, bool valid)
    {
        var model = new RiskAssessmentModel { AcknowledgedDisclaimer = consent };
        var context = new ValidationContext(model) { MemberName = nameof(model.AcknowledgedDisclaimer) };
        Assert.Equal(valid, Validator.TryValidateProperty(consent, context, new List<ValidationResult>()));
    }

    private sealed class ResponseHandler(int status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) });
    }
}
