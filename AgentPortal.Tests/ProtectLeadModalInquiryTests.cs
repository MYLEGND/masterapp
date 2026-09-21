using System.Net;
using System.Text;
using System.Text.Json;
using AgentPortal.Controllers.Api;
using AgentPortal.Models;
using AgentPortal.Security;
using AgentPortal.Services;
using Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using PortalTrackingResolver = AgentPortal.Services.Tracking.AgentTrackingResolver;
using ProtectTrackingResolver = ProtectWebsite.Services.Tracking.AgentTrackingResolver;
using ProtectWebsite.Controllers;

namespace AgentPortal.Tests;

public sealed class ProtectLeadModalInquiryTests
{
    [Fact]
    public async Task Proxy_UsesScopedReferrerAsServerVerifiedAgentAttribution()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var profile = new AgentTrackingProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = "agent-oid",
            AgentUpn = "agent@example.com",
            Slug = "agent-one",
            DisplayName = "Agent One"
        };
        db.AgentTrackingProfiles.Add(profile);
        db.AgentTrackingAliases.Add(new AgentTrackingAlias
        {
            AgentTrackingProfileId = profile.Id,
            Slug = "agent-one",
            IsCanonical = true,
            Profile = profile
        });
        await db.SaveChangesAsync();

        var resolver = new ProtectTrackingResolver(db, NullLogger<ProtectTrackingResolver>.Instance);
        var handler = new CaptureHandler();
        var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        factory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(client);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tracking:ApiBase"] = "https://portal.example.test",
                ["Tracking:SharedSecret"] = "secret",
                ["Founder:Upn"] = "founder@example.com",
                ["ASPNETCORE_ENVIRONMENT"] = "Production"
            })
            .Build();

        var controller = new TrackingProxyController(
            factory.Object,
            config,
            NullLogger<TrackingProxyController>.Instance,
            resolver)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Headers.Referer = "https://protect.mylegnd.com/a/agent-one/";

        var result = await controller.SubmitLead(new TrackingProxyController.LeadSubmitRequest
        {
            FirstName = "Jamie",
            Email = "jamie@example.com",
            InterestType = "assessment",
            SourcePageKey = "home",
            SourceCtaKey = "soft_scroll_home",
            TermsAccepted = true,
            MarketingEmailConsent = true
        }, CancellationToken.None);

        Assert.IsType<ContentResult>(result);
        Assert.NotNull(handler.Body);

        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal("agent-one", json.RootElement.GetProperty("AgentSlug").GetString());
        Assert.Equal(profile.Id, json.RootElement.GetProperty("AgentTrackingProfileId").GetGuid());
    }

    [Fact]
    public async Task CentralLeadSubmit_PersistsAndEmailsOnlyTheScopedAgent()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var profile = SeedAgent(db);
        var sender = new Mock<IEmailSender>(MockBehavior.Strict);
        sender.Setup(x => x.TrySendAsync(
                profile.AgentUpn,
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>()))
            .ReturnsAsync(true);

        var controller = BuildCentralController(db, sender.Object);
        var result = await controller.Submit(Request(profile));

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(StatusCodes.Status200OK, ok.StatusCode ?? StatusCodes.Status200OK);

        var lead = Assert.Single(db.WebsiteLeads);
        Assert.Equal(profile.Id, lead.AgentTrackingProfileId);
        Assert.Equal("agent-one", lead.AgentSlug);
        Assert.Equal("New", lead.Status);

        sender.Verify(x => x.TrySendAsync(
            profile.AgentUpn,
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>()), Times.Once);
        sender.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CentralLeadSubmit_DoesNotReportSuccessWhenAgentNotificationFails()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var profile = SeedAgent(db);
        var sender = new Mock<IEmailSender>(MockBehavior.Strict);
        sender.Setup(x => x.TrySendAsync(
                profile.AgentUpn,
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>()))
            .ReturnsAsync(false);

        var controller = BuildCentralController(db, sender.Object);
        var result = await controller.Submit(Request(profile));

        var unavailable = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);

        var lead = Assert.Single(db.WebsiteLeads);
        Assert.Equal("NotificationFailed", lead.Status);
        Assert.Equal(profile.Id, lead.AgentTrackingProfileId);

        var captured = unavailable.Value?.GetType().GetProperty("captured")?.GetValue(unavailable.Value);
        var notificationSent = unavailable.Value?.GetType().GetProperty("notificationSent")?.GetValue(unavailable.Value);
        Assert.Equal(true, captured);
        Assert.Equal(false, notificationSent);
    }

    private static AgentTrackingProfile SeedAgent(Infrastructure.Data.MasterAppDbContext db)
    {
        var profile = new AgentTrackingProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = "agent-oid",
            AgentUpn = "agent@example.com",
            Slug = "agent-one",
            DisplayName = "Agent One"
        };
        db.AgentTrackingProfiles.Add(profile);
        db.AgentTrackingAliases.Add(new AgentTrackingAlias
        {
            AgentTrackingProfileId = profile.Id,
            Slug = "agent-one",
            IsCanonical = true,
            Profile = profile
        });
        db.SaveChanges();
        return profile;
    }

    private static LeadSubmitController BuildCentralController(
        Infrastructure.Data.MasterAppDbContext db,
        IEmailSender sender)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Founder:Upn"] = "founder@example.com",
                ["Analytics:SharedSecret"] = "secret"
            })
            .Build();

        var resolver = new PortalTrackingResolver(db, NullLogger<PortalTrackingResolver>.Instance);
        var flags = Options.Create(new AppFeatureFlags { IngestHmacEnabled = false });
        var memory = new MemoryCache(new MemoryCacheOptions());
        var signature = new IngestSignatureValidator(memory, config, NullLogger<IngestSignatureValidator>.Instance);
        var controller = new LeadSubmitController(
            db,
            config,
            sender,
            resolver,
            NullLogger<LeadSubmitController>.Instance,
            flags,
            signature)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        controller.Request.Headers["X-Shared-Secret"] = "secret";
        controller.Request.Host = new HostString("protect.mylegnd.com");
        return controller;
    }

    private static LeadSubmitController.LeadSubmitRequest Request(AgentTrackingProfile profile) => new()
    {
        FirstName = "Jamie",
        Email = "jamie@example.com",
        InterestType = "assessment",
        SourcePageKey = "home",
        SourceCtaKey = "soft_scroll_home",
        SourcePath = "/a/agent-one/",
        TermsAccepted = true,
        MarketingEmailConsent = true,
        AgentTrackingProfileId = profile.Id,
        AgentSlug = profile.Slug
    };

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Body = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"status\":\"ok\",\"captured\":true,\"notificationSent\":true,\"emailSent\":true}",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
