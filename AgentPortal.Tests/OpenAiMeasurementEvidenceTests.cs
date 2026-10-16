using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Analytics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class OpenAiMeasurementEvidenceTests
{
    [Theory]
    [InlineData("sent", 200, false, 1)]
    [InlineData("sent", 204, false, 1)]
    [InlineData("sent", 500, false, 0)]
    [InlineData("retryable", 200, false, 0)]
    [InlineData("sent", 200, true, 0)]
    public async Task HttpReceiptsOnlyProveTransportForTheirCurrentDestination(string status, int httpStatus, bool foreignPixel, int expected)
    {
        using var db = ControllerTestHelpers.BuildDb();
        using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
        var owner = MarketingOwnerScope.Business(Guid.NewGuid());
        db.CommerceBusinesses.Add(new CommerceBusiness { Id = owner.CommerceBusinessId!.Value, Key = "synthetic", DisplayName = "Synthetic", IsActive = true, Status = "Active" });
        var source = new AnalyticsEvent { EventId = Guid.NewGuid(), EventType = "Lead", CommerceBusinessId = owner.CommerceBusinessId,
            EventUtc = DateTime.UtcNow.AddMinutes(-1), ReceivedUtc = DateTime.UtcNow, Host = "synthetic.example", Environment = "Production" };
        db.AnalyticsEvents.Add(source);
        await db.SaveChangesAsync();
        var connections = new OpenAiAdsAccountConnectionAuthority(db, protector);
        await connections.BindVerifiedAsync(owner, new("synthetic-account", "Synthetic", "admin", "approved", "api_key",
            PixelId: "synthetic-pixel", ConversionDataSourceId: "synthetic-source"), new("synthetic-key", "synthetic-capi"));
        var receipt = new MarketingDestinationDelivery { OwnerKey = owner.Key, OwnerType = owner.OwnerType,
            CommerceBusinessId = owner.CommerceBusinessId, AnalyticsEventId = source.Id, Provider = "openai", Channel = "server",
            CanonicalSource = nameof(AnalyticsEvent), CanonicalEventId = source.EventId.ToString("N"), CanonicalEventName = "Lead", ProviderEventName = "lead_created",
            PixelId = foreignPixel ? "foreign-pixel" : "synthetic-pixel", AdvertiserAccountId = "synthetic-account", ConversionDataSourceId = "synthetic-source",
            Status = status, LastHttpStatusCode = httpStatus, AttemptCount = 1, SentUtc = DateTime.UtcNow, CreatedUtc = DateTime.UtcNow };
        db.MarketingDestinationDeliveries.Add(receipt);
        await db.SaveChangesAsync();
        var meta = new MarketingConnectionStore(db, protector);
        await meta.ImportProfileAsync(owner, "synthetic-meta-pixel", "synthetic-meta-key", null);
        db.MetaSignalEvents.Add(new MetaSignalEvent { EventId = "synthetic-meta-event", EventName = "Lead",
            CommerceBusinessId = owner.CommerceBusinessId, CreatedUtc = DateTime.UtcNow, MetaServerSent = true,
            MetadataJson = System.Text.Json.JsonSerializer.Serialize(new { sourceAnalyticsEventId = source.Id,
                metaServerAttempted = true, metaServerEventsReceived = 1, metaServerPixelId = "synthetic-meta-pixel" }) });
        await db.SaveChangesAsync();
        var configuration = new ConfigurationBuilder().Build();
        var snapshot = await new MarketingMeasurementEvidenceService(db, configuration, meta, connections).GetAsync(owner);
        Assert.Equal(1, snapshot.Meta.Accepted);
        Assert.Equal("events_received", snapshot.Meta.AcceptanceEvidence);
        Assert.True(snapshot.ReceivingEvents);
        Assert.Equal(0, snapshot.OpenAi.Accepted);
        Assert.Equal(expected, snapshot.OpenAi.TransportAcknowledged);
        Assert.Equal(expected > 0 ? "http_accepted" : "not_observed", snapshot.OpenAi.AcceptanceEvidence);
        Assert.False(snapshot.OpenAi.AttributionObserved);
        Assert.Equal(status, receipt.Status);
        Assert.Equal(1, receipt.AttemptCount);
        using var http = new HttpClient(new UnavailableHandler());
        var health = await new OpenAiMeasurementHealthService(db, connections, http).GetAsync(owner);
        Assert.NotEqual("provider_accepted", health.Status);
        if (expected > 0) Assert.Equal("transport_acknowledged", health.Status);
    }

    private sealed class UnavailableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
