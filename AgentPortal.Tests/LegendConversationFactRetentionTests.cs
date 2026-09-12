using System;
using System.Linq;
using System.Threading.Tasks;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConversationFactRetentionTests
{
    [Fact]
    public async Task LiteralFactsSurviveServiceRecreationOnlyWithinTheirActorAndConversation()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var actor = Guid.NewGuid().ToString();
        var other = Guid.NewGuid().ToString();
        foreach (var id in new[] { actor, other })
            db.AgentProfiles.Add(new AgentProfile { Id = Guid.NewGuid(), AgentUserId = id,
                AgentUpn = id + "@example.test", NormalizedEmail = id + "@example.test", IsActive = true });
        await db.SaveChangesAsync();
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var conversation = Guid.NewGuid().ToString();
        const string source = "Record for this conversation: the archive closes on the fifth of June, and Salma owns the access review.";
        var service = new LegendFounderAiDiscourseStateService(db, new AgentProfileAccessResolver(db), operations.Object);
        Assert.NotNull(await service.RecordFactsAsync(ControllerTestHelpers.BuildUser(actor), conversation, source,
            [new("the archive", "closes on", "the fifth of June"), new("Salma", "owns", "the access review")]));
        db.ChangeTracker.Clear();
        service = new LegendFounderAiDiscourseStateService(db, new AgentProfileAccessResolver(db), operations.Object);
        var state = await service.GetStateAsync(ControllerTestHelpers.BuildUser(actor), conversation);
        var turn = Assert.Single(state!.Turns);
        Assert.Contains(turn.Nodes, node => node.SemanticValue == "the fifth of June");
        Assert.Contains(turn.Nodes, node => node.SemanticValue == "Salma");
        Assert.All(turn.Nodes, node =>
        {
            Assert.Equal("ConversationUserAssertion", node.Provenance);
            Assert.Equal(0, node.IndependentSupportCount);
        });
        Assert.False(turn.IsComposed);
        Assert.Empty((await service.GetStateAsync(ControllerTestHelpers.BuildUser(other), conversation))!.Turns);
        Assert.Empty((await service.GetStateAsync(ControllerTestHelpers.BuildUser(actor), Guid.NewGuid().ToString()))!.Turns);
        Assert.DoesNotContain(source, (await db.LegendFounderAiDiscourseTurns.SingleAsync()).MeaningGraphJson);
        Assert.Empty(await db.LegendLanguageTextUnits.ToListAsync());
        Assert.Empty(await db.LegendCorpusCandidates.ToListAsync());
        operations.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Salma", "approves", "claims")]
    [InlineData("Salma", "cannot approve", "claims")]
    [InlineData("Salma", "cannot approve", "payments without review")]
    public async Task ModelCannotInventFactsOrDropNegationAndQualifications(string subject, string relation, string value)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var service = new LegendFounderAiDiscourseStateService(db, new AgentProfileAccessResolver(db),
            new Mock<ILegendConnectOperations>(MockBehavior.Strict).Object);
        await Assert.ThrowsAsync<ArgumentException>(() => service.RecordFactsAsync(
            ControllerTestHelpers.BuildUser(Guid.NewGuid().ToString()), Guid.NewGuid().ToString(),
            "Salma cannot approve claims without review.", [new(subject, relation, value)]));
        Assert.Empty(await db.LegendFounderAiDiscourseTurns.ToListAsync());
    }
}
