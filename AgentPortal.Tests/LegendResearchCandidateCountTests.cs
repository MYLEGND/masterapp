using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Domain.Messaging;
using Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendResearchCandidateCountTests
{
    [Theory]
    [InlineData("{\"claims\":[],\"contradictions\":[]}", false, 0, null)]
    [InlineData("{\"sources\":[],\"claims\":[{}],\"contradictions\":[]}", true, 1, 0)]
    [InlineData("{\"sources\":[],\"claims\":[],\"contradictions\":[]}", true, 0, 0)]
    public async Task SearchCounts_DistinguishMissingAdmissionFromObservedZero(
        string structured, bool expectedSuccess, int raw, int? admitted)
    {
        var payload = JsonSerializer.Serialize(new
        {
            status = "completed",
            output = new object[]
            {
                new { type = "web_search_call", action = new
                { query = "public document title", sources = new[] { new { url = "https://example.com/evidence", title = "Public document" } } } },
                new { type = "message", content = new[] { new { type = "output_text", text = structured } } }
            }
        });
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["LegendConnect:InternetResearch:ApiKey"] = "local-test-placeholder" }).Build();
        var transport = new LegendConnectConfiguredReadOnlySearchTransport(
            new Factory(payload), configuration, NullLogger<LegendConnectConfiguredReadOnlySearchTransport>.Instance);
        var result = await transport.SearchAsync(new(Guid.NewGuid(), "en",
            [new("query", 1, "public document title", "en", 8)], 8, 12));

        Assert.Equal(expectedSuccess, result.Succeeded);
        Assert.Equal(raw, result.CandidateCounts?.RawClaims);
        Assert.Equal(0, result.CandidateCounts?.RawContradictions);
        Assert.Equal(admitted, result.CandidateCounts?.AdmittedClaims);
        Assert.Null(result.CandidateCounts?.BoundClaims);
        if (!expectedSuccess)
            Assert.Equal("internet_research_search_lineage_invalid", result.FailureReason);
    }

    [Fact]
    public void BindingCounts_CountCandidateOnceAcrossMultipleDocumentsAndRetainUnknownRawCount()
    {
        var now = DateTime.UtcNow;
        var decision = LegendConnectOperations.DecideResearchNeeded("Please cite sources.", "en", null, now);
        var request = LegendConnectResearchRequestFactory.Create("Please cite sources.", decision,
            new(true, LegendConnectResearchContracts.LockedEvaluationAuthorizationProvenance, null,
                LegendConnectResearchAccessClass.PublicReadOnly, true, true), null, null, 0);
        var search = new LegendConnectResearchSearchTransportResult(true, "test", "test", null, "test",
            [], [], [], [],
            [new("bound", "Public claim.", ["https://example.com/a", "https://example.com/b"], now, "en", true),
             new("unretrieved", "Other claim.", ["https://example.com/missing"], now, "en", true)],
            [], 0, null, null, false);
        var pages = new LegendConnectResearchPageRetrievalResult(true, "test", "test", [], [], [], [],
            [new("https://example.com/a", "https://example.com/a", "source-a", "doc-a", "citation-a"),
             new("https://example.com/b", "https://example.com/b", "source-b", "doc-b", "citation-b")], [], 0, null, false);
        var method = typeof(LegendConnectOperations).GetMethod("BuildResearchEvidencePacket",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var packet = Assert.IsType<LegendConnectResearchEvidencePacket>(method.Invoke(null, [request, search, pages, 0L]));

        Assert.Equal(2, packet.ClaimEvidence.Count);
        Assert.Equal(2, packet.CandidateCounts?.AdmittedClaims);
        Assert.Equal(1, packet.CandidateCounts?.BoundClaims);
        Assert.Equal(0, packet.CandidateCounts?.BoundContradictions);
        Assert.Null(packet.CandidateCounts?.RawClaims);
    }

    private sealed class Factory(string payload) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(payload));
    }
    private sealed class Handler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) });
    }
}
