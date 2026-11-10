using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Services;
using Domain.Messaging;
using Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendBusinessAssistantTransportTests
{
    [Fact]
    public async Task BusinessRequestBindsProjectDefaultTierAndNeverRetriesPaidAttempt()
    {
        using var handler = new RejectionHandler();
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("OpenAI")).Returns(() => new HttpClient(handler, false) { BaseAddress = new Uri("https://api.openai.com/") });
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["OpenAI:LegendFounderAiServiceTier"] = "priority" }).Build();
        var discourse = new LegendFounderAiDiscourseStateService(null!, null!, Mock.Of<ILegendConnectOperations>());
        var service = new LegendFounderAiConversationService(factory.Object, configuration, null!,
            NullLogger<LegendFounderAiConversationService>.Instance, discourse, Mock.Of<ILegendLanguageRegistry>(),
            Mock.Of<ITranslationService>(), Mock.Of<IControlledResourceAccessService>(), Mock.Of<IServiceScopeFactory>());
        var method = typeof(LegendFounderAiConversationService).GetMethod("SendResponseAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task<JsonDocument?>)method.Invoke(service, new object?[] { "synthetic-test-key", "synthetic-model", "synthetic-context",
            new object[] { new { role = "user", content = "synthetic request" } }, Array.Empty<object>(), false, false,
            TimeSpan.FromSeconds(30), "low", 512, CancellationToken.None, false, null, "proj_synthetic", "org_synthetic", null })!;
        await Assert.ThrowsAnyAsync<Exception>(async () => await task);
        Assert.Equal(1, handler.Attempts);
        Assert.Equal("proj_synthetic", handler.Project); Assert.Equal("org_synthetic", handler.Organization);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("default", body.RootElement.GetProperty("service_tier").GetString());
        Assert.False(body.RootElement.GetProperty("store").GetBoolean());
        Assert.Empty(body.RootElement.GetProperty("tools").EnumerateArray());
        Assert.Equal("none", body.RootElement.GetProperty("tool_choice").GetString());
        Assert.Equal(512, body.RootElement.GetProperty("max_output_tokens").GetInt32());
    }

    private sealed class RejectionHandler : HttpMessageHandler
    {
        public int Attempts { get; private set; }
        public string? Project { get; private set; }
        public string? Organization { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Attempts++;
            Project = string.Join("", request.Headers.GetValues("OpenAI-Project"));
            Organization = string.Join("", request.Headers.GetValues("OpenAI-Organization"));
            Body = await request.Content!.ReadAsStringAsync(ct);
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{\"error\":{\"message\":\"synthetic rejection\"}}") };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return response;
        }
    }
}
