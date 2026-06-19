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
using ProtectWebsite.Services.Meta;
using ProtectWebsite.Services.MetaSignal;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public class MetaSignalOutcomeDispatcherHostedServiceTests
{
    [Fact]
    public async Task DispatchBatchAsync_SendsOnlyDispatchEligibleServerAuthorityRows()
    {
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

        pixelResolution
            .Setup(x => x.ResolveForLeadAsync(null, null, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedMetaPixelContext
            {
                PixelId = "pixel-123",
                PixelOwnerType = MetaPixelOwnerTypes.Agency,
                AccessToken = "token-123"
            });

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
