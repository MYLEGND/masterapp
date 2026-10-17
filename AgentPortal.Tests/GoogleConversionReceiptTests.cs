using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Analytics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class GoogleConversionReceiptTests
{
    [Theory]
    [InlineData("valid", 200, true, false)]
    [InlineData("offset", 200, true, false)]
    [InlineData("missing-zone", 200, false, true)]
    [InlineData("empty", 200, false, true)]
    [InlineData("object", 200, false, true)]
    [InlineData("array", 200, false, true)]
    [InlineData("null", 200, false, true)]
    [InlineData("scalar", 200, false, true)]
    [InlineData("malformed", 200, false, true)]
    [InlineData("missing", 200, false, true)]
    [InlineData("empty-result", 200, false, true)]
    [InlineData("multiple", 200, false, true)]
    [InlineData("wrong-action", 200, false, true)]
    [InlineData("wrong-click", 200, false, true)]
    [InlineData("wrong-time", 200, false, true)]
    [InlineData("partial", 200, false, false)]
    [InlineData("error", 200, false, false)]
    [InlineData("valid", 429, false, true)]
    [InlineData("valid", 503, false, true)]
    public async Task OnlyMatchingProviderResultsProveAcceptanceAndRetriesPreserveIdentity(string mode, int status, bool sent, bool retryable)
    {
        using var db = ControllerTestHelpers.BuildDb();
        using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
        var store = new MarketingConnectionStore(db, protector);
        var owner = MarketingOwnerScope.Business(Guid.NewGuid());
        db.CommerceBusinesses.Add(new CommerceBusiness { Id = owner.CommerceBusinessId!.Value, Key = "synthetic", DisplayName = "Synthetic", IsActive = true, Status = "Active" });
        await store.SaveProviderCredentialAsync(owner, "google", "synthetic-refresh", MarketingProviderAuthorizationMethods.GoogleOAuthRefreshToken,
            "{}", null, "1111111111", "Synthetic");
        var connection = await store.GetProviderConnectionAsync(owner, "google");
        var measurement = await store.SaveProviderMeasurementConfigurationAsync(owner, new("google", null, "click",
            [new("Lead", "Lead", "customers/1111111111/conversionActions/77")], connection.Revision));
        var mapping = Assert.Single(measurement.Mappings);
        var source = new AnalyticsEvent { EventId = Guid.NewGuid(), EventType = "Lead", CommerceBusinessId = owner.CommerceBusinessId,
            EventUtc = DateTime.UtcNow.AddMinutes(-1), ReceivedUtc = DateTime.UtcNow, Host = "synthetic.example", Environment = "Production",
            MetadataJson = "{\"gclid\":\"synthetic-click\",\"valueCents\":2500,\"currency\":\"USD\"}" };
        db.AnalyticsEvents.Add(source);
        await db.SaveChangesAsync();
        var responseBody = Response(mode, mapping.DestinationId!, source.EventUtc);
        var bodies = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.Host == "oauth2.googleapis.com")
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"synthetic-access\",\"expires_in\":3600}") };
            Assert.Equal("googleads.googleapis.com", request.RequestUri!.Host);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("synthetic-access", request.Headers.Authorization?.Parameter);
            Assert.False(request.Headers.Contains("developer-token"));
            bodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new((HttpStatusCode)status) { Content = new StringContent(responseBody) };
        }));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["GoogleAds:ClientId"] = "synthetic-client", ["GoogleAds:ClientSecret"] = "synthetic-secret" }).Build();
        var factory = new Mock<IHttpClientFactory>(); factory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(http);
        var oauth = new MarketingExternalAdsOAuthService(factory.Object, config, new EphemeralDataProtectionProvider(), store, NullLogger<MarketingExternalAdsOAuthService>.Instance);
        var transport = new ExternalAdsConversionApiService(http, oauth, store, config);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await transport.SendAsync(owner, connection, measurement, mapping, source, "synthetic-click");
            Assert.Equal(sent, result.Sent);
            Assert.Equal(retryable, result.Retryable);
        }
        Assert.Equal(bodies[0], bodies[1]);
        using var requestBody = JsonDocument.Parse(bodies[0]);
        Assert.Equal(CanonicalAdvertisingEventProjection.ResolveEventId(source), requestBody.RootElement.GetProperty("conversions")[0].GetProperty("orderId").GetString());
        // Historical sent/HTTP receipts stay immutable resend fences, but reports need provider proof.
        var receipt = new MarketingDestinationDelivery { OwnerKey = owner.Key, OwnerType = owner.OwnerType, CommerceBusinessId = owner.CommerceBusinessId,
            AnalyticsEventId = source.Id, Provider = "google", Channel = "server", CanonicalSource = nameof(AnalyticsEvent), CanonicalEventId = source.EventId.ToString("N"),
            CanonicalEventName = "Lead", ProviderEventName = "Lead", AdvertiserAccountId = connection.AccountId, ConversionDataSourceId = mapping.DestinationId,
            Status = "sent", LastHttpStatusCode = status, AttemptCount = 1, SentUtc = DateTime.UtcNow, ProviderReceiptJson = responseBody, CreatedUtc = DateTime.UtcNow };
        db.MarketingDestinationDeliveries.Add(receipt); await db.SaveChangesAsync();
        Assert.Equal(sent, MarketingDeliveryEvidencePolicy.GoogleProviderAccepted(receipt, source));
        var openAi = new OpenAiAdsAccountConnectionAuthority(db, protector);
        var evidence = await new MarketingMeasurementEvidenceService(db, config, store, openAi).GetAsync(owner);
        Assert.Equal(sent ? 1 : 0, evidence.Google.Accepted);
        Assert.Equal(status is >= 200 and < 300 ? 1 : 0, evidence.Google.TransportAcknowledged);
        Assert.Equal("sent", receipt.Status);
        Assert.Equal(1, receipt.AttemptCount);
    }

    private static string Response(string mode, string action, DateTime when)
    {
        var time = new DateTimeOffset(DateTime.SpecifyKind(when, DateTimeKind.Utc));
        object result = new { conversionAction = mode == "wrong-action" ? "foreign-action" : action,
            gclid = mode == "wrong-click" ? "foreign-click" : "synthetic-click",
            conversionDateTime = (mode == "wrong-time" ? time.AddSeconds(1) : mode == "offset" ? time.ToOffset(TimeSpan.FromHours(-7)) : time).ToString(mode == "missing-zone" ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm:sszzz") };
        return mode switch {
            "empty" => "", "object" => "{}", "array" => "[]", "null" => "null", "scalar" => "42", "malformed" => "{",
            "missing" => "{\"results\":[]}", "empty-result" => "{\"results\":[{}]}",
            "multiple" => JsonSerializer.Serialize(new { results = new[] { result, result } }),
            "partial" => JsonSerializer.Serialize(new { results = new[] { result }, partialFailureError = new { code = 3, message = "synthetic rejection" } }),
            "error" => JsonSerializer.Serialize(new { results = new[] { result }, error = new { code = 3 } }),
            _ => JsonSerializer.Serialize(new { results = new[] { result } }) };
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response(request));
    }
}
