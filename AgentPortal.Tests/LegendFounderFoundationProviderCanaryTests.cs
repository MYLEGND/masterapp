using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Services;
using AgentPortal.Services.Analytics;
using Domain.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class LegendFounderAiModeIsolationTests
{
    private async Task VerifyFoundationProviderAnswersHeldOutCreoleWithEmptyCurriculumAsync()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        Assert.False(string.IsNullOrWhiteSpace(OpenAiKeyResolver.Resolve(configuration)),
            "NOT_CONFIGURED: the actual provider canary requires the existing account credential.");
        var selectedModel = configuration["OpenAI:LegendFounderAiModel"];
        Assert.False(string.IsNullOrWhiteSpace(selectedModel),
            "NOT_CONFIGURED: the exact centrally selected foundation model is required.");
        var artifactPath = Environment.GetEnvironmentVariable("LEGEND_FOUNDATION_PROVIDER_CANARY_ARTIFACT");
        Assert.False(string.IsNullOrWhiteSpace(artifactPath),
            "NOT_CONFIGURED: an evidence artifact path is required.");

        var writes = new LegendFounderAiComprehensiveDiagnosticContractTests.ResourceWriteGuard();
        using var factory = new LiveOpenAiHttpClientFactory();
        var request = new LegendFounderAiChatRequest
        {
            Mode = "legend",
            SourceLanguageCode = "ht",
            Messages =
            [
                new("user", "Mwen gen 18 mèt riban. Chak pakè bezwen 3 mèt. Konbyen pakè mwen ka fè?"),
                new("assistant", "Ou ka fè 6 pakè."),
                new("user", "Si mwen fin fè 4 nan pakè sa yo, konbyen mèt mwen rete? Reponn ak yon sèl fraz an kreyòl ayisyen.")
            ]
        };
        LegendFounderAiChatResponse? reply = null;
        var clock = Stopwatch.StartNew();
        var status = "FAILED";
        try
        {
            await LegendFounderAiNativeOnlyProviderIsolationTests.WithProductionAuthorityAsync(
                async (services, db, externalCounts) =>
                {
                    var founder = await AddFounderProfileAsync(db);
                    Assert.Empty(db.LegendLanguageTextUnits);
                    writes.Armed = true;
                    var service = new LegendFounderAiConversationService(
                        factory, configuration,
                        services.GetRequiredService<FounderLegendConnectService>(),
                        NullLogger<LegendFounderAiConversationService>.Instance,
                        services.GetRequiredService<LegendFounderAiDiscourseStateService>(),
                        services.GetRequiredService<ILegendLanguageRegistry>(),
                        services.GetRequiredService<ITranslationService>());
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    reply = await service.ReplyAsync(founder, request, deadline.Token);
                    Assert.True(reply.Succeeded, Describe(reply));
                    Assert.Equal("HostedFoundation", reply.ResponseAuthority);
                    Assert.Equal("foundation_response", reply.Stage);
                    Assert.Equal(selectedModel, reply.FoundationModel);
                    Assert.Equal("ExternalHosted", reply.FoundationHosting);
                    Assert.True(reply.ExternalAnsweringUsed);
                    Assert.False(reply.EscalationUsed);
                    Assert.Equal("NotRequired", reply.ResearchState);
                    Assert.Null(reply.ResearchOutcome);
                    Assert.Null(reply.LearningState);
                    Assert.Contains("6", reply.Message);
                    Assert.Contains("mèt", reply.Message, StringComparison.OrdinalIgnoreCase);
                    Assert.Single(factory.HttpCalls);
                    Assert.Equal((0, 0), externalCounts());
                    Assert.Equal(0, writes.BlockedWrites);
                    Assert.Equal(0, writes.SaveChangesAttempts);
                    Assert.Empty(db.LegendLanguageTextUnits);
                }, writes);
            status = "PASSED";
        }
        finally
        {
            var report = new
            {
                Category = "Candidate authoritative ReplyAsync + production DI authorities + actual hosted provider; synthetic authenticated Founder and empty in-memory data. Not live-Founder or production-data proof.",
                CodeRevision = typeof(LegendFounderAiConversationService).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                SelectedModel = selectedModel,
                Status = status,
                Request = request,
                Response = reply,
                HttpCalls = factory.HttpCalls,
                factory.CreateClientCount,
                writes.SaveChangesAttempts,
                writes.BlockedWrites,
                ElapsedMilliseconds = clock.ElapsedMilliseconds
            };
            await File.WriteAllTextAsync(artifactPath!, JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
