using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Analytics;
using Infrastructure.WebsiteEditing;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class OpenAiMeasurementDeliveryTests
{
    [Fact]
    public async Task ConversionsApi_UsesDocumentedEndpointBearerKeyAndCanonicalEventId()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{\"received\":1}");
        using var client = new HttpClient(handler);
        var service = new OpenAiConversionsApiService(client);
        var conversion = new OpenAiConversionEvent(
            "canonical-event-123",
            OpenAiMeasurementEventNames.LeadCreated,
            1773892800000,
            "https://example.com/quote",
            "web",
            new("customer_action"),
            "opaque-oppref");

        var result = await service.SendAsync("pixel-123", "secret-capi-key", conversion);

        Assert.True(result.Sent);
        Assert.False(result.Retryable);
        Assert.Equal("sent", result.Status);
        Assert.Equal("https://bzr.openai.com/v1/events?pid=pixel-123", handler.Uri!.ToString());
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("secret-capi-key", handler.AuthorizationParameter);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.GetProperty("validate_only").GetBoolean());
        Assert.Equal("legend_growth_os", body.RootElement.GetProperty("integration_source").GetString());
        var evt = body.RootElement.GetProperty("events")[0];
        Assert.Equal("canonical-event-123", evt.GetProperty("id").GetString());
        Assert.Equal("lead_created", evt.GetProperty("type").GetString());
        Assert.Equal("opaque-oppref", evt.GetProperty("oppref").GetString());
        Assert.Equal("customer_action", evt.GetProperty("data").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    public async Task ConversionsApi_ClassifiesRetryableAndPermanentFailures(int statusCode, bool retryable)
    {
        using var client = new HttpClient(new RecordingHandler((HttpStatusCode)statusCode, "{\"error\":\"failure\"}"));
        var service = new OpenAiConversionsApiService(client);
        var result = await service.SendAsync(
            "pixel",
            "key",
            new("evt", OpenAiMeasurementEventNames.LeadCreated, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                "https://example.com/", "web", new("customer_action")));

        Assert.False(result.Sent);
        Assert.Equal(retryable, result.Retryable);
        Assert.Equal(statusCode, result.HttpStatusCode);
    }

    [Theory]
    [InlineData("Lead", "lead_created", "customer_action")]
    [InlineData("QualifiedLead", "QualifiedLead", "customer_action")]
    [InlineData("AppointmentBooked", "appointment_scheduled", "customer_action")]
    [InlineData("AppointmentCompleted", "AppointmentCompleted", "customer_action")]
    [InlineData("ApplicationSubmitted", "ApplicationSubmitted", "customer_action")]
    [InlineData("PolicyIssued", "PolicyIssued", "customer_action")]
    [InlineData("PolicyPaid", "order_created", "contents")]
    [InlineData("AddToCart", "items_added", "contents")]
    [InlineData("InitiateCheckout", "checkout_started", "contents")]
    [InlineData("Purchase", "order_created", "contents")]
    public void Mapper_ProjectsCanonicalServerEventsThroughDestinationCatalog(
        string canonical,
        string expectedProviderEvent,
        string expectedDataType)
    {
        var row = new MetaSignalEvent
        {
            EventId = "canonical-123",
            EventName = canonical,
            CreatedUtc = DateTime.UtcNow,
            Host = "shop.example.com",
            MetadataJson = "{\"sourcePath\":\"/checkout\",\"valueCents\":8900,\"currency\":\"USD\",\"productId\":\"sku-1\",\"productName\":\"Item\",\"quantity\":1}"
        };

        Assert.True(OpenAiMeasurementEventMapper.TryMap(row, out var mapped));
        Assert.Equal("canonical-123", mapped.Id);
        Assert.Equal(expectedProviderEvent, mapped.Type);
        Assert.Equal(expectedDataType, mapped.Data.Type);
        Assert.Equal("https://shop.example.com/checkout", mapped.SourceUrl);

        if (expectedDataType == "contents")
        {
            Assert.Equal(8900, mapped.Data.Amount);
            Assert.Equal("USD", mapped.Data.Currency);
        }
    }

    [Fact]
    public void DestinationCatalog_CoversEveryCanonicalServerConversionExactlyOnce()
    {
        var canonicalConversions = MetaSignalEventCatalog.Definitions
            .Where(definition => string.Equals(definition.Category, "conversion", StringComparison.OrdinalIgnoreCase))
            .Select(definition => definition.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var mappedConversions = MarketingConversionDestinationCatalog.Definitions
            .Select(definition => definition.CanonicalEventName)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(canonicalConversions, mappedConversions);
        Assert.Equal(mappedConversions.Length, mappedConversions.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(mappedConversions, OpenAiMeasurementEventMapper.SupportedCanonicalServerEvents.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Lead", "lead_created", MarketingConversionEventKind.Standard, true)]
    [InlineData("QualifiedLead", "QualifiedLead", MarketingConversionEventKind.Custom, false)]
    [InlineData("AppointmentBooked", "appointment_scheduled", MarketingConversionEventKind.Standard, true)]
    [InlineData("AppointmentCompleted", "AppointmentCompleted", MarketingConversionEventKind.Custom, false)]
    [InlineData("ApplicationSubmitted", "ApplicationSubmitted", MarketingConversionEventKind.Custom, false)]
    [InlineData("PolicyIssued", "PolicyIssued", MarketingConversionEventKind.Custom, false)]
    [InlineData("PolicyPaid", "order_created", MarketingConversionEventKind.Standard, true)]
    [InlineData("AddToCart", "items_added", MarketingConversionEventKind.Standard, true)]
    [InlineData("InitiateCheckout", "checkout_started", MarketingConversionEventKind.Standard, true)]
    [InlineData("Purchase", "order_created", MarketingConversionEventKind.Standard, true)]
    public void DestinationCatalog_ExplicitlyClassifiesOpenAiStandardVsCustomOptimizationEligibility(
        string canonical,
        string providerEvent,
        MarketingConversionEventKind expectedKind,
        bool optimizationEligible)
    {
        var destination = MarketingConversionDestinationCatalog.ResolveOpenAi(canonical);

        Assert.NotNull(destination);
        Assert.Equal(providerEvent, destination!.EventName);
        Assert.Equal(expectedKind, destination.Kind);
        Assert.Equal(optimizationEligible, destination.OptimizationEligible);
    }

    [Fact]
    public void DestinationCatalog_PreservesExistingMetaPolicyPaidTranslation()
    {
        var destination = MarketingConversionDestinationCatalog.ResolveMeta("PolicyPaid");

        Assert.NotNull(destination);
        Assert.Equal("Purchase", destination!.EventName);
        Assert.Equal(MarketingConversionEventKind.Standard, destination.Kind);
    }

    [Fact]
    public void Mapper_DoesNotGuessUnknownEvents()
    {
        var row = new MetaSignalEvent
        {
            EventId = "canonical-unsupported",
            EventName = "UnknownCanonicalEvent",
            CreatedUtc = DateTime.UtcNow,
            Host = "example.com"
        };

        Assert.False(OpenAiMeasurementEventMapper.TryMap(row, out _));
    }

    [Fact]
    public void OwnerResolution_IsStrictAcrossFounderAgentAndBusiness()
    {
        var business = Guid.NewGuid();
        var agent = Guid.NewGuid();

        Assert.Equal(
            MarketingOwnerScope.Business(business).Key,
            OpenAiMeasurementEventMapper.ResolveOwner(new MetaSignalEvent { CommerceBusinessId = business })!.Key);

        Assert.Equal(
            MarketingOwnerScope.Agent(agent).Key,
            OpenAiMeasurementEventMapper.ResolveOwner(new MetaSignalEvent { AgentTrackingProfileId = agent })!.Key);

        Assert.Equal(
            MarketingOwnerScope.Founder.Key,
            OpenAiMeasurementEventMapper.ResolveOwner(new MetaSignalEvent
            {
                MetadataJson = "{\"siteKey\":\"" + WebsiteEditorSiteKeys.Legend + "\"}"
            })!.Key);

        Assert.Null(OpenAiMeasurementEventMapper.ResolveOwner(new MetaSignalEvent()));
    }

    [Fact]
    public void DeliveryLedger_HasUniqueCanonicalDestinationIdempotencyKey()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var entity = db.Model.FindEntityType(typeof(MarketingDestinationDelivery));
        Assert.NotNull(entity);
        Assert.Contains(entity!.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual(new[]
            {
                nameof(MarketingDestinationDelivery.OwnerKey),
                nameof(MarketingDestinationDelivery.Provider),
                nameof(MarketingDestinationDelivery.Channel),
                nameof(MarketingDestinationDelivery.CanonicalEventId),
                nameof(MarketingDestinationDelivery.ProviderEventName)
            }));
    }

    [Fact]
    public void SharedBrowserRuntime_UsesOpenAiSdkMeasureSingleAndCanonicalPageEventId()
    {
        var root = FindRoot();
        var measurement = File.ReadAllText(Path.Combine(root, "SHARED", "WebsitePlatform", "openai-measurement.js"));
        var tracking = File.ReadAllText(Path.Combine(root, "SHARED", "WebsitePlatform", "tracking.js"));
        var cms = File.ReadAllText(Path.Combine(root, "SHARED", "WebsitePlatform", "legend-public-cms.js"));

        Assert.Contains("https://bzrcdn.openai.com/sdk/oaiq.min.js", measurement, StringComparison.Ordinal);
        Assert.Contains("measureSingle", measurement, StringComparison.Ordinal);
        Assert.Contains("event_id", measurement, StringComparison.Ordinal);
        Assert.Contains("ClientEventId", measurement, StringComparison.Ordinal);
        Assert.Contains("page_viewed", measurement, StringComparison.Ordinal);
        Assert.Contains("globalPrivacyControl", measurement, StringComparison.Ordinal);
        Assert.DoesNotContain("ConversionsApiKey", measurement, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LegendOpenAiMeasurement?.trackCanonical", tracking, StringComparison.Ordinal);
        var sendEventIndex = tracking.IndexOf("async function sendEvent(payload)", StringComparison.Ordinal);
        var postIndex = tracking.IndexOf("const result = await postBody(body);", sendEventIndex, StringComparison.Ordinal);
        var successIndex = tracking.IndexOf("if (result.ok)", postIndex, StringComparison.Ordinal);
        var projectionIndex = tracking.IndexOf("LegendOpenAiMeasurement?.trackCanonical?.(body)", successIndex, StringComparison.Ordinal);
        Assert.True(sendEventIndex >= 0 && postIndex > sendEventIndex && successIndex > postIndex && projectionIndex > successIndex);
        Assert.DoesNotContain(
            "LegendOpenAiMeasurement?.trackCanonical?.(body)",
            tracking[sendEventIndex..postIndex],
            StringComparison.Ordinal);
        Assert.Contains("openAiMeasurementAsset", cms, StringComparison.Ordinal);
        Assert.Contains("LegendOpenAiMeasurement?.configure", cms, StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        var github = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(github) && File.Exists(Path.Combine(github, "MASTERAPP.sln"))) return github;
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MASTERAPP.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException();
    }

    private sealed class RecordingHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(responseBody) };
        }
    }
}
