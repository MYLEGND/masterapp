using System;
using System.Linq;
using System.Threading.Tasks;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentPortal.Tests;

// Uses the production service registrations and canonical curriculum admission.
// The database and the refusing/counting HTTP factory remain test boundaries.
// SourceLanguageCode is absent, matching the actual browser request contract.
[Collection("LegendConnectFounderEnvironment")]
public sealed class LegendFounderAiAutomaticLanguageComputationTests
{
    [Theory]
    [InlineData("147", "26", "121")]
    [InlineData("-19", "26", "-45")]
    public async Task UndeclaredLanguage_UnseenOperandsReachNativeComputationWithoutExternalClients(
        string left, string right, string expected)
    {
        const string founderId = "630c734f-4239-4ea6-bf55-18e9c7f5b984";
        var previousFounder = Environment.GetEnvironmentVariable("FOUNDER_OID");
        Environment.SetEnvironmentVariable("FOUNDER_OID", founderId);
        try
        {
            await LegendFounderAiNativeOnlyProviderIsolationTests.WithProductionAuthorityAsync(
                async (services, db, externalCounts) =>
                {
                    ControllerTestHelpers.SeedGovernedLanguageBaseline(db);
                    db.AgentProfiles.Add(new AgentProfile
                    {
                        Id = Guid.NewGuid(), AgentUserId = founderId,
                        AgentUpn = "automatic-language@legend.test",
                        NormalizedEmail = "automatic-language@legend.test", IsActive = true
                    });
                    await db.SaveChangesAsync();
                    await LegendConnectComputedLanguageEndToEndContractTests.SeedTeachingAsync(
                        services.GetRequiredService<LegendConnectCurriculumService>());

                    var prompt = "Compute " + left + " against " + right + ".";
                    var corpus = await db.LegendLanguageTextUnits.Select(unit => unit.Text).ToArrayAsync();
                    Assert.DoesNotContain(prompt, corpus);
                    foreach (var heldOut in new[] { left, right, expected })
                        Assert.DoesNotContain(corpus, text => text.Contains(heldOut, StringComparison.Ordinal));
                    Assert.False(await db.LegendLanguageMeaningNodeEvidence.AnyAsync(node =>
                        node.SemanticDimension == "total" && node.SemanticValue == expected));

                    var response = await services.GetRequiredService<LegendFounderAiConversationService>()
                        .ReplyAsync(ControllerTestHelpers.BuildUser(founderId), new LegendFounderAiChatRequest
                        {
                            Mode = "legend", NativeOnly = true, SourceLanguageCode = null,
                            Messages = [new("user", prompt)]
                        });

                    Assert.True(response.Succeeded, response.Error);
                    Assert.Equal("native_response", response.Stage);
                    Assert.Equal("LegendAi", response.ResponseAuthority);
                    Assert.Equal(LegendConnectResearchEvidenceOrigin.InternalKnowledge, response.EvidenceOrigin);
                    Assert.Equal("The result is " + expected + ".", response.Message);
                    Assert.NotEmpty(response.ReasoningTransitionPath ?? []);
                    Assert.Equal((0, 0), externalCounts());
                });
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", previousFounder);
        }
    }
}
