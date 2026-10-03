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
using Infrastructure.Analytics;
using Domain.Entities;
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
        var owner = new AgentTrackingProfile { Id = Guid.NewGuid(), AgentUserId = "replay-owner",
            AgentUpn = "replay@example.test", Slug = "replay-owner" };
        db.AgentTrackingProfiles.Add(owner);
        await db.SaveChangesAsync();
        var controller = WebsiteTrackingIngestTests.BuildController(db);
        controller.HttpContext.Items["TrackingProfile"] = owner;
        var request = new MetaSignalIngestRequest
        {
            EventId = Guid.NewGuid().ToString("N"), EventName = "ViewContent",
            SessionId = "qa-session", VisitorId = "qa-visitor"
        };
        var envelope = new WebsiteTrackingProxyAuthority.AnalyticsEventRequest {
            ClientEventId = Guid.Parse(request.EventId), EventType = request.EventName,
            SessionId = request.SessionId, VisitorId = request.VisitorId, MetaSignal = request
        };
        var accepted = Assert.IsType<OkObjectResult>(await controller.Ingest(envelope, default));
        var duplicate = Assert.IsType<OkObjectResult>(await controller.Ingest(envelope, default));
        var acceptedJson = System.Text.Json.JsonSerializer.SerializeToElement(accepted.Value);
        var duplicateJson = System.Text.Json.JsonSerializer.SerializeToElement(duplicate.Value);
        Assert.Equal(acceptedJson.GetProperty("eventId").GetGuid(), duplicateJson.GetProperty("eventId").GetGuid());
        Assert.Equal("duplicate_ignored", duplicateJson.GetProperty("status").GetString());
        Assert.Single(await db.AnalyticsEvents.ToListAsync());
        envelope.SessionId = "different-session";
        Assert.IsType<ConflictObjectResult>(await controller.Ingest(envelope, default));

    }

    [Fact]
    public async Task ProtectBrowserMetaIngestIgnoresSpoofedAgentOwnerAndUsesServerScope()
    {
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var serverAgent = new AgentTrackingProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = "server-agent",
            AgentUpn = "server@example.test",
            Slug = "server-scope",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        db.AgentTrackingProfiles.Add(serverAgent);
        await db.SaveChangesAsync();
        var http = new DefaultHttpContext();
        http.Items["TrackingProfile"] = serverAgent;
        http.Items["TrackingSlug"] = serverAgent.Slug;

        var controller = WebsiteTrackingIngestTests.BuildController(db);
        controller.HttpContext.Items["TrackingProfile"] = serverAgent;
        controller.HttpContext.Items["TrackingSlug"] = serverAgent.Slug;

        var request = new MetaSignalIngestRequest
        {
            EventId = Guid.NewGuid().ToString("N"),
            EventName = "ViewContent",
            SessionId = "server-owned-session",
            VisitorId = "server-owned-visitor",
            AgentTrackingProfileId = Guid.NewGuid(),
            AgentSlug = "spoofed-agent"
        };

        Assert.IsType<OkObjectResult>(await controller.Ingest(new WebsiteTrackingProxyAuthority.AnalyticsEventRequest {
            ClientEventId = Guid.Parse(request.EventId), EventType = request.EventName,
            SessionId = request.SessionId, VisitorId = request.VisitorId, MetaSignal = request
        }, default));

        var row = await db.AnalyticsEvents.SingleAsync();
        Assert.Equal(serverAgent.Id, row.AgentTrackingProfileId);
        Assert.Equal(serverAgent.Slug, row.AgentSlug);
        Assert.NotEqual(request.AgentTrackingProfileId, row.AgentTrackingProfileId);
        Assert.NotEqual(request.AgentSlug, row.AgentSlug);
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
    [Fact]
    public async Task BrowserPersistenceRejectsCrossOwnerAndSessionReplayThroughOneAuthority()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var eventId = Guid.NewGuid();
        var owner = Guid.NewGuid();
        AnalyticsEvent Row(Guid profile, string session)
        {
            var row = UnifiedEventMapper.ToAnalytics(new UnifiedEventContext
            {
                EventId = eventId.ToString("N"), EventName = "page_view", AgentTrackingProfileId = profile,
                SessionId = session, VisitorId = "visitor", Host = "protect.example.test", PageKey = "home"
            });
            row.ClientEventId = eventId;
            return row;
        }
        var original = Row(owner, "session");
        original.EventId = Guid.NewGuid();
        Assert.Equal(UnifiedAnalyticsWriter.BrowserWriteResult.Accepted,
            await UnifiedAnalyticsWriter.PersistBrowserEventAsync(db, original));
        Assert.Equal(original.ClientEventId, original.EventId);
        var retry = Row(owner, "session");
        Assert.Equal(UnifiedAnalyticsWriter.BrowserWriteResult.Duplicate,
            await UnifiedAnalyticsWriter.PersistBrowserEventAsync(db, retry));
        Assert.Equal(original.EventId, retry.EventId);
        Assert.Equal(UnifiedAnalyticsWriter.BrowserWriteResult.Conflict,
            await UnifiedAnalyticsWriter.PersistBrowserEventAsync(db, Row(Guid.NewGuid(), "session")));
        Assert.Equal(UnifiedAnalyticsWriter.BrowserWriteResult.Conflict,
            await UnifiedAnalyticsWriter.PersistBrowserEventAsync(db, Row(owner, "other-session")));
        Assert.Single(await db.AnalyticsEvents.ToListAsync());
    }

}
