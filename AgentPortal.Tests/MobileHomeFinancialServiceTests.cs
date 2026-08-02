using System;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.FinancialIntelligence;
using Domain.JourneyCircles;
using Domain.Messaging;
using Infrastructure.Mobile;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MobileHomeFinancialServiceTests
{
    [Fact]
    public async Task GetHomeAsync_DoesNotEvaluateFinancialIntelligenceUntilTheProfileDrawerRequestsIt()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var client = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "client-home-identity",
            FirstName = "Client",
            LastName = "Home",
            Email = "client.home@example.test"
        };
        db.ClientProfiles.Add(client);
        await db.SaveChangesAsync();

        var messaging = new Mock<IMessagingService>(MockBehavior.Strict);
        messaging
            .Setup(service => service.ListConversationsAsync(
                It.Is<MessagingActor>(actor =>
                    actor.UserId == client.ClientUserId &&
                    actor.ParticipantType == MessagingParticipantTypes.Client),
                It.IsAny<MessagingConversationListQuery>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MessagingConversationListResult(
                true,
                null,
                null,
                Array.Empty<MessagingConversationSummary>()));

        var journey = new Mock<IJourneyCirclesService>(MockBehavior.Strict);
        journey
            .Setup(service => service.GetDashboardAsync(
                client.ClientUserId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyJourneyDashboard());

        var financialIntelligence = new Mock<IFinancialIntelligenceEvaluationService>(
            MockBehavior.Strict);
        var financialOperatingSystem = new Mock<
            IMobileFinancialOperatingSystemProjectionService>(MockBehavior.Strict);
        var service = new MobileHomeService(
            db,
            messaging.Object,
            journey.Object,
            financialIntelligence.Object,
            financialOperatingSystem.Object,
            new Infrastructure.DailyScripture.DailyScriptureService());

        var result = await service.GetHomeAsync(new MobileResolvedActor(
            new MessagingActor(client.ClientUserId, MessagingParticipantTypes.Client),
            client.Id,
            "Client Home"));

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Home);
        messaging.VerifyAll();
        journey.VerifyAll();
        financialIntelligence.VerifyNoOtherCalls();
        financialOperatingSystem.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetFinancialAsync_UsesOnlyThePersistedActiveAgentClientsRelationship()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var client = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "client-financial-identity",
            FirstName = "Client",
            LastName = "One",
            Email = "client.one@example.test"
        };
        db.ClientProfiles.Add(client);
        db.AgentProfiles.AddRange(
            new AgentProfile
            {
                Id = Guid.NewGuid(),
                AgentUserId = "assigned-agent-oid",
                AgentUpn = "agent@example.test",
                FullName = "Morgan Riley",
                IsActive = true
            },
            new AgentProfile
            {
                Id = Guid.NewGuid(),
                AgentUserId = "upn-only-agent-oid",
                AgentUpn = "assigned-agent-oid",
                FullName = "Incorrect UPN Match",
                IsActive = true
            });
        db.AgentClients.Add(new AgentClient
        {
            AgentUserId = "assigned-agent-oid",
            ClientUserId = client.ClientUserId,
            CreatedUtc = new DateTime(2026, 7, 27, 0, 0, 0, DateTimeKind.Utc)
        });
        await db.SaveChangesAsync();

        var service = CreateService(db, OperatingSystem());

        var result = await service.GetFinancialAsync(new MobileResolvedActor(
            new MessagingActor(client.ClientUserId, MessagingParticipantTypes.Client),
            client.Id,
            "Client One"));

        var presentation = Assert.IsType<MobileFinancialPresentation>(result.Snapshot!.Presentation);
        Assert.True(presentation.AssignedAgent.HasAssignedAgent);
        Assert.Equal("Morgan Riley", presentation.AssignedAgent.DisplayName);
        Assert.Equal("Morgan", presentation.AssignedAgent.FirstName);
        Assert.All(
            presentation.PrioritySections,
            section => Assert.Contains("Morgan", section.DiscussionPrompt));
        Assert.DoesNotContain(
            presentation.PrioritySections,
            section => section.DiscussionPrompt.Contains("Incorrect", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetFinancialAsync_DoesNotExposeAnotherClientsAgentContext()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var firstClient = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "first-client-oid",
            FirstName = "First",
            LastName = "Client",
            Email = "first@example.test"
        };
        var secondClient = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "second-client-oid",
            FirstName = "Second",
            LastName = "Client",
            Email = "second@example.test"
        };
        db.AddRange(firstClient, secondClient);
        db.AgentProfiles.Add(new AgentProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = "first-client-agent",
            FullName = "Taylor Agent",
            IsActive = true
        });
        db.AgentClients.Add(new AgentClient
        {
            AgentUserId = "first-client-agent",
            ClientUserId = firstClient.ClientUserId
        });
        await db.SaveChangesAsync();

        var service = CreateService(db, OperatingSystem());

        var result = await service.GetFinancialAsync(new MobileResolvedActor(
            new MessagingActor(secondClient.ClientUserId, MessagingParticipantTypes.Client),
            secondClient.Id,
            "Second Client"));

        var presentation = Assert.IsType<MobileFinancialPresentation>(result.Snapshot!.Presentation);
        Assert.False(presentation.AssignedAgent.HasAssignedAgent);
        Assert.Null(presentation.AssignedAgent.DisplayName);
        Assert.Null(presentation.AssignedAgent.FirstName);
        Assert.DoesNotContain(
            presentation.PrioritySections,
            section => section.DiscussionPrompt.Contains("Taylor", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetFinancialAsync_UsesOnlyTheAgentsOwnFinancialAuthority()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        db.AgentFinanceToolStates.Add(new AgentFinanceToolState
        {
            AgentUserId = "agent-oid",
            ToolId = "LegendLivingBalanceSheet",
            JsonState =
                """
                {
                  "assets": { "savings": 42000 },
                  "liabilities": { "shortTerm": 2000 },
                  "cashFlow": {
                    "earnings": 120000,
                    "insuranceCosts": 10000,
                    "annualSavings": 20000,
                    "debtObligations": 5000
                  }
                }
                """
        });
        await db.SaveChangesAsync();
        var service = CreateService(db, OperatingSystem());

        var result = await service.GetFinancialAsync(new MobileResolvedActor(
            new MessagingActor("agent-oid", MessagingParticipantTypes.Agent),
            Guid.NewGuid(),
            "Agent"));

        Assert.True(result.Succeeded);
        var snapshot = Assert.IsType<MobileFinancialSnapshot>(result.Snapshot);
        var presentation = Assert.IsType<MobileFinancialPresentation>(
            snapshot.Presentation);
        Assert.False(presentation.AssignedAgent.HasAssignedAgent);
        Assert.Equal(40000m, snapshot.Position?.NetWorth);
        Assert.Equal(120000m, snapshot.Position?.AnnualEarnings);
        Assert.Contains(
            presentation.PrioritySections,
            section => section.Key == "current-outlook");
    }

    private static JourneyCircleDashboard EmptyJourneyDashboard() => new(
        Profile: null,
        Preferences: null,
        Recommendations: Array.Empty<JourneyCircleRecommendation>(),
        Connections: Array.Empty<JourneyCircleConnectionSummary>(),
        Requests: Array.Empty<JourneyCircleConnectionSummary>(),
        Goals: Array.Empty<string>(),
        Circles: Array.Empty<string>(),
        LifeStages: Array.Empty<string>(),
        Locations: Array.Empty<string>(),
        Interests: Array.Empty<string>(),
        ConnectionTypes: Array.Empty<string>(),
        CommunicationStyles: Array.Empty<string>(),
        AccountabilityFrequencies: Array.Empty<string>());

    private static MobileHomeService CreateService(
        Infrastructure.Data.MasterAppDbContext db,
        MobileFinancialOperatingSystemSnapshot operatingSystem)
    {
        var financialIntelligence = new Mock<IFinancialIntelligenceEvaluationService>(MockBehavior.Strict);
        financialIntelligence
            .Setup(service => service.GetSnapshotAsync(
                It.IsAny<FinancialIntelligenceActor>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((FinancialIntelligenceSnapshot?)null);
        var operatingSystemService = new Mock<IMobileFinancialOperatingSystemProjectionService>(MockBehavior.Strict);
        operatingSystemService
            .Setup(service => service.ProjectAsync(
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(operatingSystem);
        operatingSystemService
            .Setup(service => service.ProjectAgentAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(operatingSystem);

        return new MobileHomeService(
            db,
            new Mock<IMessagingService>().Object,
            new Mock<IJourneyCirclesService>().Object,
            financialIntelligence.Object,
            operatingSystemService.Object,
            new Mock<Infrastructure.DailyScripture.IDailyScriptureService>().Object);
    }

    private static MobileFinancialOperatingSystemSnapshot OperatingSystem() =>
        new(
            new MobileFinancialProjectionStatus("Available", null, "Projection is current."),
            new MobileFinancialDataFreshness(null, null, DateTime.UtcNow),
            new MobileFinancialWeekAtGlance(
                "2026-07-27",
                new DateOnly(2026, 7, 27),
                new DateOnly(2026, 8, 2),
                OpeningCashCents: 0,
                IncomeCents: 100_00,
                DebitExpenseCents: 0,
                CreditExpenseCents: 0,
                RequiredDebtPaymentCents: 0,
                ExtraDebtPaymentCents: 0,
                EndingCashCents: 100_00,
                OpeningDebtCents: 0,
                EndingDebtCents: 0,
                PressureStatus: "Healthy",
                PressureSummary: null,
                Events: []),
            new MobileFinancialMonthAtGlance(
                "2026-08",
                new DateOnly(2026, 8, 1),
                new DateOnly(2026, 8, 31),
                OpeningCashCents: 100_00,
                IncomeCents: 100_00,
                DebitExpenseCents: 0,
                CreditExpenseCents: 0,
                RequiredDebtPaymentCents: 0,
                ExtraDebtPaymentCents: 0,
                EndingCashCents: 200_00,
                OpeningDebtCents: 0,
                EndingDebtCents: 0,
                SavingsContributionCents: 0,
                PressureStatus: "Healthy",
                PressureSummary: null,
                LargestObligation: null,
                Weeks: []),
            []);
}
