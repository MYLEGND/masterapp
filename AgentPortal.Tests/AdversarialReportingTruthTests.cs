using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Analytics;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Shared.Analytics;

namespace AgentPortal.Tests;

public sealed class AdversarialReportingTruthTests
{
    [Fact]
    public async Task ReplayAndIdentityMutationStormCannotCreateOrRetargetAnAcceptedBrowserFact()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var owner = Guid.NewGuid(); var eventId = Guid.NewGuid();
        AnalyticsEvent Row(Guid business, string session) {
            var row = UnifiedEventMapper.ToAnalytics(new UnifiedEventContext {
                EventId = eventId.ToString("N"), EventName = "page_view", CommerceBusinessId = business,
                SessionId = session, VisitorId = "visitor", Host = "business.example.test", PageKey = "/"
            });
            row.ClientEventId = eventId; return row;
        }
        Assert.Equal(UnifiedAnalyticsWriter.BrowserWriteResult.Accepted,
            await UnifiedAnalyticsWriter.PersistBrowserEventAsync(db, Row(owner,"session")));
        for (var index = 0; index < 500; index++)
        {
            Assert.Equal(UnifiedAnalyticsWriter.BrowserWriteResult.Duplicate,
                await UnifiedAnalyticsWriter.PersistBrowserEventAsync(db, Row(owner,"session")));
            Assert.Equal(UnifiedAnalyticsWriter.BrowserWriteResult.Conflict,
                await UnifiedAnalyticsWriter.PersistBrowserEventAsync(db, Row(Guid.NewGuid(),"session")));
            Assert.Equal(UnifiedAnalyticsWriter.BrowserWriteResult.Conflict,
                await UnifiedAnalyticsWriter.PersistBrowserEventAsync(db, Row(owner,"forged-" + index)));
        }
        var stored = Assert.Single(await db.AnalyticsEvents.ToListAsync());
        Assert.Equal(owner,stored.CommerceBusinessId);
        Assert.Equal(eventId,stored.EventId);
        Assert.Equal("session",stored.SessionId);
    }

    [Fact]
    public void BrowserMetadataCannotPromoteItselfToServerAuthority()
    {
        foreach (var name in new[] {"Lead","Purchase","PolicyPaid","website_lead_submitted","appointment_booked"})
        foreach (var browser in new[] {true,false})
        foreach (var forged in new[] {true,false})
        {
            var context = new UnifiedEventContext {
                EventName = name, IsServerAuthority = false, IsBrowserSignal = browser,
                Metadata = new {isServerAuthority=forged,measurementServerAuthorityEligible=true,metaServerAuthorityEligible=true,
                    client=new {isServerAuthority=true,measurementServerAuthorityEligible=true}}
            };
            if (browser && AnalyticsEventCatalog.RequiresServerAuthority(name))
            {
                Assert.Throws<InvalidOperationException>(()=>UnifiedEventMapper.ToAnalytics(context));
                continue;
            }
            var row = UnifiedEventMapper.ToAnalytics(context);
            row.Id = 42;
            Assert.False(CanonicalAdvertisingEventProjection.CanProjectServer(row));
        }
    }

    [Fact]
    public void TransportAndAcceptanceEvidenceMustAgreeAcrossMalformedAndContradictoryReceipts()
    {
        foreach (var sent in new[] {true,false})
        foreach (var count in new long[] {-10,0,1,20})
        {
            var row = new MetaSignalEvent {MetaServerSent=sent,
                MetadataJson=JsonSerializer.Serialize(new {metaServerEventsReceived=count})};
            Assert.Equal(sent && count>0,MarketingDeliveryEvidencePolicy.MetaProviderAccepted(row));
        }
        foreach (var json in new[] {"{}","null","[]","broken", "{\"metaServerEventsReceived\":true}", "{\"metaServerEventsReceived\":\"not-a-count\"}"})
            Assert.False(MarketingDeliveryEvidencePolicy.MetaProviderAccepted(new MetaSignalEvent {MetaServerSent=true,MetadataJson=json}));
        foreach (var status in new[] {"sent","pending","retryable","permanent_failure","blocked_scope"})
        foreach (var code in new int?[] {null,0,199,200,204,299,300,400,429,500})
            Assert.Equal(status=="sent" && code>=200 && code<300,
                MarketingDeliveryEvidencePolicy.HttpTransportAccepted(new MarketingDestinationDelivery {Status=status,LastHttpStatusCode=code}));
    }
}
