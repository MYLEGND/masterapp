using System;
using System.Collections.Generic;
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

/// <summary>
/// Exact observation and reuse of new names and opaque date strings through
/// genuine Founder templates. This does not establish calendar reasoning or
/// unrestricted entity extraction from arbitrary prose.
/// </summary>
[Collection("LegendConnectFounderEnvironment")]
public sealed class LegendConnectNamedValueLanguageEndToEndTests
{
    [Fact]
    public Task UnseenNameAndOpaqueDate_PreserveExactSpellingInReplyAndConversationRecall() =>
        VerifyNameAndRecallAsync(combinedFoundation: false);

    [Fact]
    public Task CombinedFoundation_PreservesOriginalNamedValueAndRecallControl() =>
        VerifyNameAndRecallAsync(combinedFoundation: true);

    private static async Task VerifyNameAndRecallAsync(bool combinedFoundation)
    {
        using var founderScope = new FounderScope();
        await LegendFounderAiNativeOnlyProviderIsolationTests.WithProductionAuthorityAsync(async (services, db, externalCounts) =>
        {
            ControllerTestHelpers.SeedGovernedLanguageBaseline(db);
            db.AgentProfiles.Add(new AgentProfile
            {
                Id = Guid.NewGuid(), AgentUserId = FounderScope.FounderId,
                AgentUpn = "named-values@legend.test", NormalizedEmail = "named-values@legend.test", IsActive = true
            });
            await db.SaveChangesAsync();
            var curriculum = services.GetRequiredService<LegendConnectCurriculumService>();
            if (combinedFoundation)
                await LegendHeldOutFoundationPrerequisite.AdmitAsync(db, []);
            else
                await SeedTeachingAsync(curriculum);
            const string newOwner = "Dr. Mireya D'Arcy";
            const string newDate = "9 November 2032";
            var request = Source(newOwner, newDate);
            var expected = newOwner + " closes on " + newDate + ".";
            var corpus = await db.LegendLanguageTextUnits.Select(unit => unit.Text).ToArrayAsync();
            Assert.DoesNotContain(corpus, text => text.Contains(newOwner, StringComparison.Ordinal) || text.Contains(newDate, StringComparison.Ordinal));
            Assert.DoesNotContain(request, corpus);
            Assert.DoesNotContain(expected, corpus);

            var operations = services.GetRequiredService<ILegendConnectOperations>();
            var graph = await operations.AnalyzeReusableMeaningGraphAsync(request);
            Assert.True(graph.IsComposed, graph.ReasonCode);
            Assert.Equal(newOwner, Assert.Single(graph.Nodes.Where(node => node.SemanticDimension == "owner")).SemanticValue);
            Assert.Equal(newDate, Assert.Single(graph.Nodes.Where(node => node.SemanticDimension == "closing_date")).SemanticValue);
            Assert.All(graph.Nodes, node =>
            {
                var binding = Assert.IsType<LegendConnectSourceSlotBinding>(node.SourceSlotBinding);
                Assert.Equal(LegendLanguageIdentity.TextHash(request), binding.NormalizedInputHash);
                Assert.Contains(binding.Captures, item => item.SemanticDimension == "owner" && item.Surface == newOwner);
                Assert.Contains(binding.Captures, item => item.SemanticDimension == "closing_date" && item.Surface == newDate);
                Assert.Equal("CurrentTurnAssertion", node.Provenance);
                Assert.Equal(1, node.IndependentSupportCount);
            });
            var plan = await operations.TryPlanConversationAsync(request, new LegendConnectDiscourseStateSnapshot([]));
            Assert.True(plan.Supported, plan.ReasonCode);
            Assert.Equal(newOwner, plan.Plan!.ResultDimensions["owner"]);
            Assert.Equal(newDate, plan.Plan.ResultDimensions["closing_date"]);

            var founder = ControllerTestHelpers.BuildUser(FounderScope.FounderId);
            var conversation = Guid.NewGuid().ToString("D");
            var service = services.GetRequiredService<LegendFounderAiConversationService>();
            var first = await service.ReplyAsync(founder, Request(request, conversation));
            Assert.Equal("LegendAi", first.ResponseAuthority);
            Assert.Equal("native_response", first.Stage);
            Assert.Equal(expected, first.Message);

            // The next request contains neither new value nor raw first-turn
            // text. Recall must use the persisted, revalidated observation.
            db.ChangeTracker.Clear();
            var stored = await services.GetRequiredService<LegendFounderAiDiscourseStateService>().GetStateAsync(founder, conversation);
            Assert.NotNull(stored);
            Assert.Contains(stored.Turns, turn => turn.IsComposed && turn.Role == "user" &&
                turn.Nodes.Any(node => node.SemanticDimension == "owner" && node.SemanticValue == newOwner) &&
                turn.Nodes.Any(node => node.SemanticDimension == "closing_date" && node.SemanticValue == newDate));
            var recalled = await service.ReplyAsync(founder, Request("Recall the owner and date.", conversation));
            Assert.Equal("LegendAi", recalled.ResponseAuthority);
            Assert.Equal("native_response", recalled.Stage);
            Assert.Equal(expected, recalled.Message);
            Assert.False(await db.LegendLanguageMeaningNodeEvidence.AnyAsync(node =>
                node.SemanticValue == newOwner || node.SemanticValue == newDate));
            Assert.Equal((0, 0), externalCounts());
        });
    }

    internal static async Task SeedTeachingAsync(LegendConnectCurriculumService curriculum)
    {
        foreach (var (family, owner, date) in new[]
        {
            ("amber", "Anika", "2030-02-14"),
            ("copper", "Benoit", "2031-04-18"),
            ("silver", "Carla", "2033-07-12")
        })
        {
            var taught = await curriculum.SubmitFounderBatchAsync(Teaching(family, owner, date));
            Assert.True(taught.Succeeded, taught.Message);
        }
    }

    private static LegendConnectCurriculumBatchSubmission Teaching(string family, string owner, string date) =>
        new("named-value.language." + family, "Named owner and opaque closing date with explicit conversation references",
            [new(Source(owner, date), Values(("owner", owner), ("closing_date", date)),
                 new([new("owner", "owner", owner, owner), new("date", "closing_date", date, date)],
                     [new("owner", "closes-on", "date")])),
             new(owner + " closes on " + date + ".", Values(("owner", owner), ("closing_date", date), ("conversation_function", "ownership_answer")),
                 new([new("owner", "owner", owner, owner), new("date", "closing_date", date, date),
                      new("function", "conversation_function", "ownership_answer", "closes on")],
                     [new("owner", "closes-on", "date"), new("function", "announces", "owner")])),
             new("For a saved assignment ask: Recall the owner and date.", Values(("owner_reference", "recent"), ("date_reference", "recent")),
                 new([new("owner-selector", "owner_reference", "recent", "owner"), new("date-selector", "date_reference", "recent", "date")],
                     [new("owner-selector", "paired-with", "date-selector")],
                     [new("owner-selector", "owner", "recent", null, ["user"]),
                      new("date-selector", "closing_date", "recent", null, ["user"])]))],
            [new(new(Values(("owner", "$owner"), ("closing_date", "$closing_date"))),
                 new(Values(("owner", "$owner"), ("closing_date", "$closing_date"), ("conversation_function", "ownership_answer"))))]);

    private static string Source(string owner, string date) => "Owner is " + owner + "; closes on " + date + ".";
    private static Dictionary<string, string> Values(params (string Dimension, string Value)[] values) =>
        values.ToDictionary(item => item.Dimension, item => item.Value, StringComparer.Ordinal);
    private static LegendFounderAiChatRequest Request(string text, string conversation) =>
        new() { Mode = "legend", NativeOnly = true, SourceLanguageCode = "en", ConversationId = conversation, Messages = [new("user", text)] };
    private sealed class FounderScope : IDisposable
    {
        public const string FounderId = "8db720a7-fc93-458d-9adf-ed1811c2dd76";
        private readonly string? _previous = Environment.GetEnvironmentVariable("FOUNDER_OID");
        public FounderScope() => Environment.SetEnvironmentVariable("FOUNDER_OID", FounderId);
        public void Dispose() => Environment.SetEnvironmentVariable("FOUNDER_OID", _previous);
    }
}
