using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using AgentPortal.Services;
using Domain.Messaging;
using Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CloudflareQualificationFactAttribute : FactAttribute
{
    public CloudflareQualificationFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LEGEND_CLOUDFLARE_QUALIFICATION_CONFIG")))
            Skip = "Live Cloudflare qualification requires its isolated, budgeted configuration. This is not a passing live result.";
    }
}

public sealed class LegendCloudflareLiveQualificationTests
{
    [CloudflareQualificationFact]
    [Trait("Category", "CloudflareLiveQualification")]
    public async Task HeldOutTasksThroughActualFoundationTransport()
    {
        var path = Environment.GetEnvironmentVariable("LEGEND_CLOUDFLARE_QUALIFICATION_CONFIG")!;
        var config = new ConfigurationBuilder().AddJsonFile(path).Build();
        var suitePath = config["Qualification:SuitePath"]!;
        var bytes = await File.ReadAllBytesAsync(suitePath);
        Assert.Equal(config["Qualification:SuiteSha256"], Convert.ToHexStringLower(SHA256.HashData(bytes)));
        var outputPath = config["Qualification:OutputPath"]!;
        Assert.False(File.Exists(outputPath), "A qualification run must not overwrite earlier evidence.");
        using var suite = JsonDocument.Parse(bytes);
        var factory = new CloudOnlyFactory();
        var transport = new LegendConnectModelInferenceTransport(factory, config,
            NullLogger<LegendConnectModelInferenceTransport>.Instance);
        var records = new List<object>();
        try
        {
            foreach (var scenario in suite.RootElement.GetProperty("cases").EnumerateArray())
            {
                if (config["Qualification:CaseId"] is { Length: > 0 } selectedCase &&
                    scenario.GetProperty("id").GetString() != selectedCase) continue;
                var history = new List<object>();
                var conversationId = Guid.NewGuid().ToString("D");
                foreach (var turn in scenario.GetProperty("userTurns").EnumerateArray())
                {
                    var prompt = turn.GetString()!;
                    history.Add(new { role = "user", content = prompt });
                    var instructions = LegendFounderAiConversationService.BuildInstructions(
                        "legend", sourceLanguageCode: "en", preferredLanguageCode: "en", cloudflareHosted: true);
                    var instructionsSha256 = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(instructions)));
                    var clock = Stopwatch.StartNew();
                    var result = await transport.GenerateAsync(config["LegendConnect:Foundation:Model"]!, new(
                        "conversation", instructions,
                        prompt, "governed_response", ConversationInput: JsonSerializer.SerializeToElement(history),
                        ProviderPolicy: LegendConnectExternalProviderPolicy.CloudflareFoundation,
                        RequestingActorId: config["Qualification:UserId"],
                        CloudflareScope: new(Guid.NewGuid().ToString("D"), config["Qualification:TenantId"]!,
                            config["Qualification:UserId"]!, "qualification-session", conversationId,
                            ["LegendQualification"], "qualification-v1")));
                    records.Add(new { caseId = scenario.GetProperty("id").GetString(), prompt, response = result.Text,
                        instructions, instructionsSha256,
                        instructionAuthority = "LegendFounderAiConversationService.BuildInstructions:legend:en:en:cloudflareHosted=true",
                        result.Succeeded, result.ErrorCode, result.ModelVersion, result.Hosting, result.CostMicrounits, result.InferenceSettings,
                        elapsedMs = clock.ElapsedMilliseconds, evidence = "live-cloudflare-via-dotnet-transport",
                        scope = "synthetic-isolated-qualification-not-production-founder-session" });
                    Assert.True(result.Succeeded, result.ErrorCode);
                    Assert.Equal("CloudflareHosted", result.Hosting);
                    history.Add(new { role = "assistant", content = result.Text });
                }
            }
        }
        finally
        {
            await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(new
            {
                revision = config["Qualification:Revision"], suiteSha256 = config["Qualification:SuiteSha256"],
                suiteVersion = suite.RootElement.GetProperty("version").GetString(),
                instructionSource = "shared-production-authority",
                historicalRunsUnchanged = true,
                records, modelCalls = factory.Calls, localInferenceCalls = 0,
                limitation = "Verifies real model and authoritative transport; does not establish authenticated Azure end-to-end, tools, sandbox or production acceptance."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private sealed class CloudOnlyFactory : IHttpClientFactory
    {
        public int Calls;
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("LegendCloudflareFoundation", name);
            Calls++;
            return new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false })
            { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        }
    }
}
