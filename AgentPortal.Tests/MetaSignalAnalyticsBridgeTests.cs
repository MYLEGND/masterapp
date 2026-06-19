using System;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public class MetaSignalAnalyticsBridgeTests
{
    [Fact]
    public void BridgeMetadata_BuildsBrowserSignalFlagsAndEventKey()
    {
        var source = BuildSourceAnalyticsEvent("LeadFormStart", "session-tagged");
        var leadId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        var metadataJson = InvokeBridgeMetadataBuild(
            source,
            mappedEventName: "LeadFormStart",
            deduplicationKey: "LeadFormStart:11111111111111111111111111111111:session-tagged",
            trafficType: "PaidAds",
            resolvedLeadId: leadId);

        Assert.True(ReadBoolean(metadataJson, "isBrowserSignal"));
        Assert.False(ReadBoolean(metadataJson, "isServerAuthority"));
        Assert.False(ReadBoolean(metadataJson, "metaServerAuthorityEligible"));
        Assert.False(ReadBoolean(metadataJson, "metaSingleTruthDispatchEligible"));
        Assert.True(ReadBoolean(metadataJson, "serverAuthorityWinsConflictResolution"));
        Assert.False(ReadBoolean(metadataJson, "browserPayloadCanOverrideServer"));
        Assert.Equal(
            "LeadFormStart:11111111111111111111111111111111:session-tagged",
            ReadString(metadataJson, "eventKey"));
    }

    [Fact]
    public void BridgeMetadata_BuildsServerAuthorityFlagsAndEventKey()
    {
        var leadId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var source = BuildSourceAnalyticsEvent(
            "lead_persisted",
            "session-tagged",
            MetaSignalSingleTruthPolicy.BuildMetadataJson(
                eventName: "lead_persisted",
                leadId,
                sessionId: "session-tagged",
                payload: new { LeadId = leadId },
                isBrowserSignal: false,
                isServerAuthority: false,
                metaServerAuthorityEligible: true,
                metaSingleTruthDispatchEligible: false,
                metaPipelineOrigin: "unit_test"));

        var metadataJson = InvokeBridgeMetadataBuild(
            source,
            mappedEventName: "Lead",
            deduplicationKey: "Lead:11111111111111111111111111111111:session-tagged",
            trafficType: "crm",
            resolvedLeadId: leadId);

        Assert.False(ReadBoolean(metadataJson, "isBrowserSignal"));
        Assert.True(ReadBoolean(metadataJson, "isServerAuthority"));
        Assert.True(ReadBoolean(metadataJson, "metaServerAuthorityEligible"));
        Assert.True(ReadBoolean(metadataJson, "metaSingleTruthDispatchEligible"));
        Assert.True(ReadBoolean(metadataJson, "serverAuthorityWinsConflictResolution"));
        Assert.False(ReadBoolean(metadataJson, "browserPayloadCanOverrideServer"));
        Assert.Equal(
            "Lead:11111111111111111111111111111111:session-tagged",
            ReadString(metadataJson, "eventKey"));
    }

    [Fact]
    public void BridgeMetadata_RefusesDispatchEligibilityWithoutServerTruthMarker()
    {
        var source = BuildSourceAnalyticsEvent("appointment_booked", "session-tagged");

        var metadataJson = InvokeBridgeMetadataBuild(
            source,
            mappedEventName: "AppointmentBooked",
            deduplicationKey: "AppointmentBooked:11111111111111111111111111111111:session-tagged",
            trafficType: "crm",
            resolvedLeadId: Guid.Parse("11111111-1111-1111-1111-111111111111"));

        Assert.True(ReadBoolean(metadataJson, "isServerAuthority"));
        Assert.False(ReadBoolean(metadataJson, "metaServerAuthorityEligible"));
        Assert.False(ReadBoolean(metadataJson, "metaSingleTruthDispatchEligible"));
    }

    [Fact]
    public async Task TryBuildBridgeRowAsync_DoesNotPromoteBrowserConfirmedLeadEventToServerTruth()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var source = BuildSourceAnalyticsEvent(
            "lead_form_submit_success",
            "session-tagged",
            MetaSignalSingleTruthPolicy.BuildMetadataJson(
                eventName: "lead_form_submit_success",
                leadId: null,
                sessionId: "session-tagged",
                payload: new { browser = true },
                isBrowserSignal: true,
                isServerAuthority: false,
                metaServerAuthorityEligible: false,
                metaSingleTruthDispatchEligible: false,
                metaPipelineOrigin: "unit_test"));

        var bridge = new ProtectWebsite.Services.MetaSignal.MetaSignalAnalyticsBridge(
            Mock.Of<IServiceScopeFactory>(),
            Options.Create(new ProtectWebsite.Services.MetaSignal.MetaSignalIntelligenceOptions()),
            NullLogger<ProtectWebsite.Services.MetaSignal.MetaSignalAnalyticsBridge>.Instance);

        var bridgeRow = await InvokeTryBuildBridgeRowAsync(bridge, db, source);

        Assert.Null(bridgeRow);
    }

    private static AnalyticsEvent BuildSourceAnalyticsEvent(string eventType, string sessionId, string? metadataJson = null) =>
        new()
        {
            Id = 1,
            EventId = Guid.NewGuid(),
            EventType = eventType,
            SessionId = sessionId,
            VisitorId = "visitor-tagged",
            EventUtc = DateTime.UtcNow,
            ReceivedUtc = DateTime.UtcNow,
            PageKey = "quote_life_landing",
            QuoteType = "life",
            Environment = "development",
            Host = "localhost:1111",
            MetadataJson = metadataJson
        };

    private static string InvokeBridgeMetadataBuild(
        AnalyticsEvent source,
        string mappedEventName,
        string deduplicationKey,
        string trafficType,
        Guid? resolvedLeadId)
    {
        var helperType = typeof(ProtectWebsite.Services.MetaSignal.MetaSignalAnalyticsBridge)
            .Assembly
            .GetType("ProtectWebsite.Services.MetaSignal.MetaSignalAnalyticsBridgeMetadata");
        Assert.NotNull(helperType);

        var method = helperType!.GetMethod(
            "Build",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = method!.Invoke(null, [
            source,
            mappedEventName,
            deduplicationKey,
            trafficType,
            resolvedLeadId,
            "landing",
            "paid_landing",
            null,
            null,
            null
        ]);

        return Assert.IsType<string>(result);
    }

    private static bool ReadBoolean(string metadataJson, string propertyName)
    {
        using var doc = JsonDocument.Parse(metadataJson);
        return doc.RootElement.TryGetProperty(propertyName, out var value) && value.GetBoolean();
    }

    private static string? ReadString(string metadataJson, string propertyName)
    {
        using var doc = JsonDocument.Parse(metadataJson);
        return doc.RootElement.TryGetProperty(propertyName, out var value)
            ? value.GetString()
            : null;
    }

    private static async Task<MetaSignalEvent?> InvokeTryBuildBridgeRowAsync(
        ProtectWebsite.Services.MetaSignal.MetaSignalAnalyticsBridge bridge,
        Infrastructure.Data.MasterAppDbContext db,
        AnalyticsEvent source)
    {
        var method = typeof(ProtectWebsite.Services.MetaSignal.MetaSignalAnalyticsBridge)
            .GetMethod("TryBuildBridgeRowAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = method!.Invoke(bridge, [db, source, CancellationToken.None]);
        Assert.NotNull(task);

        var typedTask = Assert.IsAssignableFrom<Task>(task);
        await typedTask;

        var resultProperty = typedTask.GetType().GetProperty("Result");
        Assert.NotNull(resultProperty);

        return resultProperty!.GetValue(typedTask) as MetaSignalEvent;
    }
}
