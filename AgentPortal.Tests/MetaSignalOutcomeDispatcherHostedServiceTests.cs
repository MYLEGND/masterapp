using Infrastructure.Analytics;
using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Infrastructure.Analytics;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public class MetaSignalOutcomeDispatcherHostedServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DispatchBatchAsync_SendsOnlyDispatchEligibleServerAuthorityRows(bool businessScope)
    {
        var businessId = businessScope ? Guid.NewGuid() : (Guid?)null;
        var databaseName = Guid.NewGuid().ToString();
        var capi = new Mock<IMetaConversionsApiService>(MockBehavior.Strict);
        var pixelResolution = new Mock<IMetaPixelResolutionService>(MockBehavior.Strict);

        capi
            .Setup(x => x.SendEventAsync(
                It.Is<MetaConversionsApiEventRequest>(request =>
                    request.EventName == "Lead" &&
                    request.AuthoritySource == MetaSendAuthoritySources.MetaSignalOutcomeDispatcherHostedService &&
                    request.ClientIpAddress == "1.2.3.4" &&
                    request.ClientUserAgent == "browser-agent"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetaConversionsApiResult
            {
                Attempted = true,
                Sent = true,
                Status = "sent"
            });

        var resolvedPixel = new ResolvedMetaPixelContext
        {
            PixelId = "pixel-123", AccessToken = "token-123",
            PixelOwnerType = businessScope ? MetaPixelOwnerTypes.Business : MetaPixelOwnerTypes.Agency
        };
        if (businessScope)
            pixelResolution.Setup(x => x.ResolveForBusinessAsync(businessId!.Value, It.IsAny<CancellationToken>())).ReturnsAsync(resolvedPixel);
        else
            pixelResolution.Setup(x => x.ResolveForLeadAsync(null, null, false, It.IsAny<CancellationToken>())).ReturnsAsync(resolvedPixel);

        await using var provider = new ServiceCollection()
            .AddDbContext<MasterAppDbContext>(options => options.UseInMemoryDatabase(databaseName))
            .AddSingleton(capi.Object)
            .AddSingleton(pixelResolution.Object)
            .BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
            db.MetaSignalEvents.Add(new MetaSignalEvent
            {
                Id = 1,
                CreatedUtc = DateTime.UtcNow,
                EventId = "eligible-row",
                CommerceBusinessId = businessId,
                EventName = "Lead",
                EventCategory = "conversion",
                SessionId = "session-1",
                VisitorId = "visitor-1",
                QuoteType = "life",
                PageKey = "quote_life_landing",
                EffectivePageKey = "quote_life_landing",
                TrafficType = "PaidAds",
                FunnelStep = 3,
                StepName = "lead_submitted",
                IntentScore = 100,
                EngagementScore = 100,
                QualificationScore = 100,
                FrictionScore = 0,
                TotalSignalScore = 100,
                ScoreTier = "SubmittedLead",
                MetaBrowserSent = false,
                MetaServerSent = false,
                MetaDeduplicationKey = "Lead:anonymous:session-1",
                Host = "example.com",
                MetadataJson = BuildBridgeOwnedServerMetadata(dispatchEligible: true)
            });

            db.MetaSignalEvents.Add(new MetaSignalEvent
            {
                Id = 2,
                CreatedUtc = DateTime.UtcNow,
                EventId = "ineligible-row",
                EventName = "Lead",
                EventCategory = "conversion",
                SessionId = "session-2",
                VisitorId = "visitor-2",
                QuoteType = "life",
                PageKey = "quote_life_landing",
                EffectivePageKey = "quote_life_landing",
                TrafficType = "PaidAds",
                FunnelStep = 3,
                StepName = "lead_submitted",
                IntentScore = 100,
                EngagementScore = 100,
                QualificationScore = 100,
                FrictionScore = 0,
                TotalSignalScore = 100,
                ScoreTier = "SubmittedLead",
                MetaBrowserSent = false,
                MetaServerSent = false,
                MetaDeduplicationKey = "Lead:anonymous:session-2",
                Host = "example.com",
                MetadataJson = BuildBridgeOwnedServerMetadata(dispatchEligible: false)
            });

            await db.SaveChangesAsync();
        }

        var service = new MetaSignalOutcomeDispatcherHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new MetaSignalIntelligenceOptions
            {
                Enabled = true,
                SendServerEvents = true
            }),
            NullLogger<MetaSignalOutcomeDispatcherHostedService>.Instance);

        await InvokeDispatchBatchAsync(service);

        await using (var verificationScope = provider.CreateAsyncScope())
        {
            var db = verificationScope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
            var rows = await db.MetaSignalEvents.OrderBy(x => x.Id).ToListAsync();

            Assert.True(rows[0].MetaServerSent);
            Assert.Equal("sent", MetaSignalSingleTruthPolicy.ReadString(rows[0].MetadataJson, "metaServerStatus"));
            Assert.True(MetaSignalSingleTruthPolicy.ReadBoolean(rows[0].MetadataJson, "metaServerSent"));

            Assert.False(rows[1].MetaServerSent);
            Assert.Null(MetaSignalSingleTruthPolicy.ReadString(rows[1].MetadataJson, "metaServerStatus"));
        }

        capi.VerifyAll();
        pixelResolution.VerifyAll();
    }

    [Fact]
    public async Task BusinessWebsiteLeadIdentityFlowsToTheExistingScopedDispatcher()
    {
        var businessId = Guid.NewGuid();
        var leadId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var capi = new Mock<IMetaConversionsApiService>(MockBehavior.Strict);
        var pixel = new Mock<IMetaPixelResolutionService>(MockBehavior.Strict);

        capi.Setup(x => x.SendEventAsync(
                It.Is<MetaConversionsApiEventRequest>(request =>
                    request.EventName == "Lead" &&
                    request.LeadId == leadId &&
                    request.CommerceBusinessId == businessId &&
                    request.AgentTrackingProfileId == null &&
                    request.Email == "visitor@example.org" &&
                    request.FirstName == "Visitor" &&
                    request.PixelOwnerType == MetaPixelOwnerTypes.Business),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetaConversionsApiResult { Attempted = true, Sent = true, Status = "sent" });
        pixel.Setup(x => x.ResolveForBusinessAsync(businessId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedMetaPixelContext
            {
                PixelId = "business-pixel",
                AccessToken = "business-token",
                PixelOwnerType = MetaPixelOwnerTypes.Business
            });

        var database = Guid.NewGuid().ToString();
        await using var provider = new ServiceCollection()
            .AddDbContext<MasterAppDbContext>(options => options.UseInMemoryDatabase(database))
            .AddSingleton(capi.Object)
            .AddSingleton(pixel.Object)
            .BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
            db.WebsiteLeads.Add(new WebsiteLead
            {
                LeadId = leadId,
                CommerceBusinessId = businessId,
                WebsiteContentVersionId = versionId,
                WebsiteBindingId = "business_contact",
                FirstName = "Visitor",
                Email = "visitor@example.org",
                InterestType = "BusinessInquiry",
                SourcePageKey = "/contact",
                SessionId = "business-session",
                TermsAccepted = true,
                CreatedUtc = DateTime.UtcNow,
                ClientIpAddress = "1.2.3.4",
                ClientUserAgent = "browser-agent"
            });
            db.MetaSignalEvents.Add(new MetaSignalEvent
            {
                CreatedUtc = DateTime.UtcNow,
                EventId = "business:" + businessId.ToString("N") + ":lead-event",
                EventName = "Lead",
                EventCategory = "conversion",
                LeadId = leadId,
                CommerceBusinessId = businessId,
                WebsiteContentVersionId = versionId,
                WebsiteBindingId = "business_contact",
                SessionId = "business-session",
                TrafficType = "Direct",
                MetaDeduplicationKey = "Lead:" + leadId.ToString("N"),
                MetadataJson = BuildBridgeOwnedServerMetadata(true)
            });
            await db.SaveChangesAsync();
        }

        var dispatcher = new MetaSignalOutcomeDispatcherHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new MetaSignalIntelligenceOptions { Enabled = true, SendServerEvents = true }),
            NullLogger<MetaSignalOutcomeDispatcherHostedService>.Instance);

        await InvokeDispatchBatchAsync(dispatcher);

        await using var verification = provider.CreateAsyncScope();
        var row = await verification.ServiceProvider.GetRequiredService<MasterAppDbContext>()
            .MetaSignalEvents.SingleAsync();
        Assert.True(row.MetaServerSent);
        Assert.Equal(businessId, row.CommerceBusinessId);
        Assert.Equal(versionId, row.WebsiteContentVersionId);
        Assert.Equal("business_contact", row.WebsiteBindingId);
        Assert.Null(row.AgentTrackingProfileId);
        capi.VerifyAll();
        pixel.VerifyAll();
    }

    [Fact]
    public async Task DispatchBatchRejectsMixedBusinessAndAgentOwnersBeforeResolvingOrSending()
    {
        var capi = new Mock<IMetaConversionsApiService>(MockBehavior.Strict);
        var pixel = new Mock<IMetaPixelResolutionService>(MockBehavior.Strict);
        var database = Guid.NewGuid().ToString();
        await using var scopedProvider = new ServiceCollection()
            .AddDbContext<MasterAppDbContext>(options => options.UseInMemoryDatabase(database))
            .AddSingleton(capi.Object).AddSingleton(pixel.Object).BuildServiceProvider();
        await using (var scope = scopedProvider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
            db.MetaSignalEvents.Add(new MetaSignalEvent { EventId = "conflicting-owner", EventName = "Lead",
                CommerceBusinessId = Guid.NewGuid(), AgentTrackingProfileId = Guid.NewGuid(),
                TrafficType = "PaidAds", MetadataJson = BuildBridgeOwnedServerMetadata(true) });
            await db.SaveChangesAsync();
        }
        var dispatcher = new MetaSignalOutcomeDispatcherHostedService(scopedProvider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new MetaSignalIntelligenceOptions { Enabled = true, SendServerEvents = true }),
            NullLogger<MetaSignalOutcomeDispatcherHostedService>.Instance);
        await InvokeDispatchBatchAsync(dispatcher);
        await using var verification = scopedProvider.CreateAsyncScope();
        var row = await verification.ServiceProvider.GetRequiredService<MasterAppDbContext>().MetaSignalEvents.SingleAsync();
        Assert.False(row.MetaServerSent);
        Assert.Equal("skipped_owner_conflict", MetaSignalSingleTruthPolicy.ReadString(row.MetadataJson, "metaServerStatus"));
        pixel.VerifyNoOtherCalls();
        capi.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DispatchBatch_RetriesSameEventAfterBackoffAndBlockedRowsDoNotStarveIt()
    {
        var ids = new System.Collections.Generic.List<string>();
        var capi = new Mock<IMetaConversionsApiService>();
        capi.Setup(x => x.SendEventAsync(It.IsAny<MetaConversionsApiEventRequest>(), It.IsAny<CancellationToken>()))
            .Callback<MetaConversionsApiEventRequest, CancellationToken>((request, _) => ids.Add(request.EventId))
            .ReturnsAsync(() => ids.Count == 1
                ? new MetaConversionsApiResult { Attempted = true, Status = "failed", Retryable = true, HttpStatusCode = 503 }
                : new MetaConversionsApiResult { Attempted = true, Sent = true, Status = "sent", EventsReceived = 1 });
        var pixel = new Mock<IMetaPixelResolutionService>();
        pixel.Setup(x => x.ResolveForLeadAsync(null, null, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedMetaPixelContext { PixelId = "test", AccessToken = "test", PixelOwnerType = MetaPixelOwnerTypes.Agency });
        var database = Guid.NewGuid().ToString();
        await using var provider = new ServiceCollection()
            .AddDbContext<MasterAppDbContext>(options => options.UseInMemoryDatabase(database))
            .AddSingleton(capi.Object).AddSingleton(pixel.Object).BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
            for (var i = 0; i < 27; i++)
                db.MetaSignalEvents.Add(new MetaSignalEvent
                {
                    EventId = "retry-test-" + i, EventName = "Lead", TrafficType = "PaidAds",
                    SessionId = "retry-session-" + i, MetaDeduplicationKey = "retry-key-" + i,
                    CreatedUtc = DateTime.UtcNow.AddMinutes(-30).AddSeconds(i),
                    WebDriver = i < 26, MetadataJson = BuildBridgeOwnedServerMetadata(true)
                });
            await db.SaveChangesAsync();
        }
        var service = new MetaSignalOutcomeDispatcherHostedService(provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new MetaSignalIntelligenceOptions { Enabled = true, SendServerEvents = true }),
            NullLogger<MetaSignalOutcomeDispatcherHostedService>.Instance);
        await InvokeDispatchBatchAsync(service);
        Assert.Single(ids);
        await InvokeDispatchBatchAsync(service);
        Assert.Single(ids); // Future retry does not send early.
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
            Assert.Equal(26, await db.MetaSignalEvents.CountAsync(x => x.MetadataJson!.Contains("skipped_traffic_or_producer")));
            var row = await db.MetaSignalEvents.SingleAsync(x => x.EventId == "retry-test-26");
            var metadata = System.Text.Json.Nodes.JsonNode.Parse(row.MetadataJson!)!;
            metadata["metaServerNextAttemptUtc"] = DateTime.UtcNow.AddSeconds(-1);
            row.MetadataJson = metadata.ToJsonString();
            await db.SaveChangesAsync();
        }
        await InvokeDispatchBatchAsync(service);
        Assert.Equal(new[] { "retry-key-26", "retry-key-26" }, ids);
        await InvokeDispatchBatchAsync(service);
        Assert.Equal(2, ids.Count);
    }

    private static string BuildBridgeOwnedServerMetadata(bool dispatchEligible)
    {
        return JsonSerializer.Serialize(new
        {
            bridgeSource = "analytics_events",
            isBrowserSignal = false,
            isServerAuthority = true,
            serverAuthorityWinsConflictResolution = true,
            browserPayloadCanOverrideServer = false,
            metaServerAuthorityEligible = dispatchEligible,
            metaSingleTruthDispatchEligible = dispatchEligible,
            metaDispatchOwner = MetaSignalSingleTruthPolicy.DispatchOwner,
            metaDecisionAuthority = MetaSignalSingleTruthPolicy.DecisionAuthority,
            metaAuthoritativeSendPath = MetaSignalSingleTruthPolicy.AuthoritativeSendPath,
            sourceClientIpAddress = "1.2.3.4",
            sourceClientUserAgent = "browser-agent"
        });
    }

    private static async Task InvokeDispatchBatchAsync(MetaSignalOutcomeDispatcherHostedService service)
    {
        var method = typeof(MetaSignalOutcomeDispatcherHostedService)
            .GetMethod("DispatchBatchAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = method!.Invoke(service, [CancellationToken.None]);
        Assert.NotNull(task);

        await Assert.IsAssignableFrom<Task>(task);
    }
}
