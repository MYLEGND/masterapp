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

    [Fact]
    public async Task ConversionsApi_SerializesCanonicalUserMatchingWithoutRawIdentifiers()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{\"received\":1}");
        using var client = new HttpClient(handler);
        var service = new OpenAiConversionsApiService(client);
        var user = new OpenAiConversionUser(
            Obref: "browser-ref-123",
            EmailsSha256: ["b4c9a289323b21a01c3e940f150eb9b8c542587f1abfd8f0e1cc1ffc5e475514"],
            PhoneNumbersSha256: ["758fbf68945f21c416814c539ab578876c8d98fb69e6da692def92cd52417fe0"],
            ExternalIdsSha256: ["93be0d83f7a2d56e79d48c5da382f218ce96adb31cdcb242ce4cfa1f2fcf0685"],
            Regions: ["wa"],
            PostalCodes: ["98264"],
            Cities: ["lynden"],
            Countries: ["US"],
            IpAddress: "203.0.113.10",
            UserAgent: "Mozilla/5.0");

        var conversion = new OpenAiConversionEvent(
            "same-canonical-event",
            OpenAiMeasurementEventNames.LeadCreated,
            1773892800000,
            "https://example.com/quote",
            "web",
            new("customer_action"),
            "oppref-1",
            User: user);

        Assert.True((await service.SendAsync("pixel-123", "key", conversion)).Sent);

        using var body = JsonDocument.Parse(handler.Body!);
        var evt = body.RootElement.GetProperty("events")[0];
        Assert.Equal("same-canonical-event", evt.GetProperty("id").GetString());
        var serializedUser = evt.GetProperty("user");
        Assert.Equal("browser-ref-123", serializedUser.GetProperty("obref").GetString());
        Assert.Equal(user.EmailsSha256![0], serializedUser.GetProperty("emails_sha256")[0].GetString());
        Assert.Equal(user.PhoneNumbersSha256![0], serializedUser.GetProperty("phone_numbers_sha256")[0].GetString());
        Assert.Equal("203.0.113.10", serializedUser.GetProperty("ip_address").GetString());
        Assert.DoesNotContain("user@example.com", handler.Body!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("+1 (415) 555-2671", handler.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenAiUserMapper_FollowsDocumentedIdentifierNormalization()
    {
        Assert.Equal("user@example.com", OpenAiConversionUserMapper.NormalizeEmail(" USER@example.com "));
        Assert.Equal("14155552671", OpenAiConversionUserMapper.NormalizePhone("+1 (415) 555-2671"));
        Assert.Equal("Customer-ABC", OpenAiConversionUserMapper.NormalizeExternalId(" Customer-ABC "));
        Assert.Equal("maryjane", OpenAiConversionUserMapper.NormalizeName("Mary Jane"));
        Assert.Equal("oconnor", OpenAiConversionUserMapper.NormalizeName("O'Connor"));
        Assert.Equal("josé", OpenAiConversionUserMapper.NormalizeName("José"));

        Assert.Equal("b4c9a289323b21a01c3e940f150eb9b8c542587f1abfd8f0e1cc1ffc5e475514",
            OpenAiConversionUserMapper.Hash("user@example.com"));
        Assert.Equal("758fbf68945f21c416814c539ab578876c8d98fb69e6da692def92cd52417fe0",
            OpenAiConversionUserMapper.Hash("14155552671"));
        Assert.Equal("93be0d83f7a2d56e79d48c5da382f218ce96adb31cdcb242ce4cfa1f2fcf0685",
            OpenAiConversionUserMapper.Hash("Customer-ABC"));
        Assert.Null(OpenAiConversionUserMapper.NormalizePhone("555-CALL-NOW"));
    }

    [Fact]
    public void ServerDestinationsShareOneCanonicalRawIdentityAuthority()
    {
        var root = FindRoot();
        var openAi = File.ReadAllText(Path.Combine(root, "Infrastructure", "Analytics", "OpenAiMeasurementDelivery.cs"));
        var meta = File.ReadAllText(Path.Combine(root, "Infrastructure", "Analytics", "MetaSignalOutcomeDispatcherHostedService.cs"));

        Assert.Contains("CanonicalMarketingIdentityResolver.ResolveAsync", openAi, StringComparison.Ordinal);
        Assert.Contains("CanonicalMarketingIdentityResolver.ResolveAsync", meta, StringComparison.Ordinal);
        var canonicalBranch = meta.IndexOf("if (canonicalIdentity is not null)", StringComparison.Ordinal);
        var historicalAdapter = meta.IndexOf("Historical queue adapter only", canonicalBranch, StringComparison.Ordinal);
        var legacyResolver = meta.IndexOf("ResolveCrmContactAsync(db, row", historicalAdapter, StringComparison.Ordinal);
        Assert.True(canonicalBranch >= 0 && historicalAdapter > canonicalBranch && legacyResolver > historicalAdapter,
            "Legacy CRM identity resolution must remain behind the explicit historical queue adapter.");
    }

    [Fact]
    public async Task CanonicalIdentityResolver_UnifiesWebsiteLeadCrmAndBrowserMatchingForBothProviders()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var businessId = Guid.NewGuid();
        var websiteLeadId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.WebsiteLeads.Add(new WebsiteLead
        {
            LeadId = websiteLeadId,
            CommerceBusinessId = businessId,
            FirstName = "Jamie",
            LastName = "Smith",
            Email = "Jamie@Example.com",
            Phone = "+1 (360) 555-0100",
            ClientIpAddress = "203.0.113.44",
            ClientUserAgent = "canonical-browser",
            Fbp = "fb-browser",
            Fbc = "fb-click",
            MetadataJson = "{\"Obref\":\"browser-ref-shared\"}",
            CreatedUtc = now
        });
        db.WorkstationLeadProfiles.Add(new WorkstationLeadProfile
        {
            LeadId = "workstation-1",
            AgentUserId = "business-owner",
            CommerceBusinessId = businessId,
            FirstName = "Jamie",
            LastName = "Smith",
            Email = "Jamie@Example.com",
            Phone = "+1 (360) 555-0100",
            City = "Lynden",
            State = "WA",
            ZipCode = "98264",
            Gender = "female",
            DOB = new DateTime(1990, 1, 2),
            CreatedUtc = now,
            UpdatedUtc = now
        });
        db.WebsiteLeadIntakeLinks.Add(new WebsiteLeadIntakeLink
        {
            Id = Guid.NewGuid(),
            WebsiteLeadRowId = 1,
            WebsiteLeadPublicId = websiteLeadId,
            WorkstationLeadId = "workstation-1",
            AgentUserId = "business-owner",
            CommerceBusinessId = businessId,
            Bucket = "Life",
            SubmittedUtc = now,
            CapturedUtc = now,
            Oppref = "oppref-source"
        });
        await db.SaveChangesAsync();

        var source = new AnalyticsEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "QualifiedLead",
            CommerceBusinessId = businessId,
            EventUtc = now,
            MetadataJson = "{\"leadId\":\"workstation-1\"}"
        };

        var identity = await CanonicalMarketingIdentityResolver.ResolveAsync(db, source);

        Assert.Same(source, identity.Source);
        Assert.Equal(websiteLeadId, identity.WebsiteLeadId);
        Assert.Equal("workstation-1", identity.WorkstationLeadId);
        Assert.Equal("Jamie@Example.com", identity.Email);
        Assert.Equal("+1 (360) 555-0100", identity.Phone);
        Assert.Equal("Lynden", identity.City);
        Assert.Equal("WA", identity.State);
        Assert.Equal("98264", identity.PostalCode);
        Assert.Equal("browser-ref-shared", identity.Obref);
        Assert.Equal("203.0.113.44", identity.ClientIpAddress);
        Assert.Equal("canonical-browser", identity.ClientUserAgent);
        Assert.Contains(websiteLeadId.ToString("N"), identity.ExternalIds);
        Assert.Contains("workstation-1", identity.ExternalIds);

        var user = Assert.IsType<OpenAiConversionUser>(OpenAiConversionUserMapper.Map(identity));
        Assert.Equal("browser-ref-shared", user.Obref);
        Assert.Equal(OpenAiConversionUserMapper.Hash("jamie@example.com"), Assert.Single(user.EmailsSha256!));
        Assert.Equal(OpenAiConversionUserMapper.Hash("13605550100"), Assert.Single(user.PhoneNumbersSha256!));
        Assert.Equal("wa", Assert.Single(user.Regions!));
        Assert.Equal("98264", Assert.Single(user.PostalCodes!));
    }

    [Fact]
    public async Task ConversionsApi_SerializesOpenAiCustomEventContract()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{\"received\":1}");
        using var client = new HttpClient(handler);
        var service = new OpenAiConversionsApiService(client);
        var conversion = new OpenAiConversionEvent(
            "qualified-lead-event",
            "custom",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            "https://example.com/quote",
            "web",
            new("custom"),
            "opp-custom",
            "qualifiedlead");

        var result = await service.SendAsync("pixel", "key", conversion);

        Assert.True(result.Sent);
        using var body = JsonDocument.Parse(handler.Body!);
        var evt = body.RootElement.GetProperty("events")[0];
        Assert.Equal("custom", evt.GetProperty("type").GetString());
        Assert.Equal("qualifiedlead", evt.GetProperty("custom_event_name").GetString());
        Assert.Equal("custom", evt.GetProperty("data").GetProperty("type").GetString());
        Assert.Equal("opp-custom", evt.GetProperty("oppref").GetString());
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
    [InlineData("Lead", "lead_created", null, "customer_action")]
    [InlineData("QualifiedLead", "custom", "qualifiedlead", "custom")]
    [InlineData("AppointmentBooked", "appointment_scheduled", null, "customer_action")]
    [InlineData("AppointmentCompleted", "custom", "appointmentcompleted", "custom")]
    [InlineData("ApplicationSubmitted", "custom", "applicationsubmitted", "custom")]
    [InlineData("PolicyIssued", "custom", "policyissued", "custom")]
    [InlineData("PolicyPaid", "order_created", null, "contents")]
    [InlineData("AddToCart", "items_added", null, "contents")]
    [InlineData("InitiateCheckout", "checkout_started", null, "contents")]
    [InlineData("Purchase", "order_created", null, "contents")]
    public void Mapper_ProjectsCanonicalServerEventsThroughDestinationCatalog(
        string canonical,
        string expectedProviderEvent,
        string? expectedCustomEventName,
        string expectedDataType)
    {
        var row = new AnalyticsEvent
        {
            EventId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            EventType = canonical,
            EventUtc = DateTime.UtcNow,
            Host = "shop.example.com",
            MetadataJson = "{\"sourcePath\":\"/checkout\",\"valueCents\":8900,\"currency\":\"USD\",\"productId\":\"sku-1\",\"productName\":\"Item\",\"quantity\":1}"
        };

        Assert.True(OpenAiMeasurementEventMapper.TryMap(row, out var mapped));
        Assert.Equal(row.EventId.ToString("N"), mapped.Id);
        Assert.Equal(expectedProviderEvent, mapped.Type);
        Assert.Equal(expectedCustomEventName, mapped.CustomEventName);
        Assert.Equal(expectedDataType, mapped.Data.Type);
        Assert.Equal("https://shop.example.com/checkout", mapped.SourceUrl);

        Assert.Equal(8900, mapped.Data.Amount);
        Assert.Equal("USD", mapped.Data.Currency);
    }

    [Fact]
    public void CanonicalValueProjectionFeedsMetaAndOpenAiFromTheSameOutcomeValue()
    {
        var value = CanonicalConversionValueProjection.Resolve(
            "{\"personalAmount\":1200.50,\"currency\":\"usd\"}");

        Assert.NotNull(value);
        Assert.Equal(120050, value!.AmountMinorUnits);
        Assert.Equal("USD", value.Currency);

        var row = new AnalyticsEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "PolicyIssued",
            EventUtc = DateTime.UtcNow,
            Host = "protect.mylegnd.com",
            MetadataJson = "{\"valueCents\":120050,\"currency\":\"USD\"}"
        };
        Assert.True(OpenAiMeasurementEventMapper.TryMap(row, out var mapped));
        Assert.Equal(120050, mapped.Data.Amount);
        Assert.Equal("USD", mapped.Data.Currency);

        var root = FindRoot();
        var meta = File.ReadAllText(Path.Combine(root, "Infrastructure", "Analytics", "MetaSignalOutcomeDispatcherHostedService.cs"));
        var openAi = File.ReadAllText(Path.Combine(root, "Infrastructure", "Analytics", "OpenAiMeasurementDelivery.cs"));
        Assert.Contains("CanonicalConversionValueProjection.Resolve", meta, StringComparison.Ordinal);
        Assert.Contains("CanonicalConversionValueProjection.Resolve", openAi, StringComparison.Ordinal);
        Assert.DoesNotContain("TryReadPositiveDecimal", meta, StringComparison.Ordinal);
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
    [InlineData("QualifiedLead", "qualifiedlead", MarketingConversionEventKind.Custom, false)]
    [InlineData("AppointmentBooked", "appointment_scheduled", MarketingConversionEventKind.Standard, true)]
    [InlineData("AppointmentCompleted", "appointmentcompleted", MarketingConversionEventKind.Custom, false)]
    [InlineData("ApplicationSubmitted", "applicationsubmitted", MarketingConversionEventKind.Custom, false)]
    [InlineData("PolicyIssued", "policyissued", MarketingConversionEventKind.Custom, false)]
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
        var row = new AnalyticsEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "UnknownCanonicalEvent",
            EventUtc = DateTime.UtcNow,
            Host = "example.com"
        };

        Assert.False(OpenAiMeasurementEventMapper.TryMap(row, out _));
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
        Assert.Contains("measurementConsent?.isAllowed", measurement, StringComparison.Ordinal);
        Assert.DoesNotContain("options?.consent", measurement, StringComparison.Ordinal);
        Assert.DoesNotContain("ConversionsApiKey", measurement, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("window.LegendAnalytics?.subscribe?.", measurement, StringComparison.Ordinal);
        var sendEventIndex = tracking.IndexOf("async function sendEvent(payload)", StringComparison.Ordinal);
        var postIndex = tracking.IndexOf("const result = await postBody(body);", sendEventIndex, StringComparison.Ordinal);
        var successIndex = tracking.IndexOf("if (result.ok)", postIndex, StringComparison.Ordinal);
        var projectionIndex = tracking.IndexOf("publishCanonical(body)", successIndex, StringComparison.Ordinal);
        Assert.True(sendEventIndex >= 0 && postIndex > sendEventIndex && successIndex > postIndex && projectionIndex > successIndex);
        Assert.DoesNotContain(
            "publishCanonical(body)",
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
