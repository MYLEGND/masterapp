using System;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Entities.FinancialIntelligence;
using Domain.FinancialIntelligence;
using Infrastructure.Data;
using Infrastructure.FinancialIntelligence;
using Infrastructure.FinancialIntelligence.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.Finance;
using System.Text.Json;
using Xunit;

namespace AgentPortal.Tests;

public sealed class FinancialIntelligenceServicesTests
{
    [Fact]
    public async Task ImportAsync_UpsertsFacts_AndCreatesOneMonthlyRecurringStream()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var connection = await AddConnectionAsync(db, profile.Id);
        var recurring = new RecurringFinancialStreamService(
            db,
            NullLogger<RecurringFinancialStreamService>.Instance);
        var importer = new FinancialImportService(
            db,
            recurring,
            NullLogger<FinancialImportService>.Instance);

        var firstImport = await importer.ImportAsync(CreateImportCommand(profile.Id, connection.Id, 125_00));

        Assert.True(firstImport.Success);
        Assert.Equal(1, await db.ImportedFinancialAccounts.CountAsync());
        Assert.Equal(3, await db.ImportedFinancialTransactions.CountAsync());
        var stream = await db.RecurringFinancialStreams.SingleAsync();
        Assert.Equal("Monthly", stream.Cadence);
        Assert.Equal("Candidate", stream.Status);
        Assert.Equal(125_00, stream.AverageAmountCents);

        var secondImport = await importer.ImportAsync(CreateImportCommand(profile.Id, connection.Id, 130_00));

        Assert.True(secondImport.Success);
        Assert.Equal(1, await db.ImportedFinancialAccounts.CountAsync());
        Assert.Equal(3, await db.ImportedFinancialTransactions.CountAsync());
        Assert.Equal(1, await db.RecurringFinancialStreams.CountAsync());
        Assert.Equal(130_00, (await db.RecurringFinancialStreams.SingleAsync()).AverageAmountCents);
    }

    [Fact]
    public async Task SynchronizeAsync_MarksOnlyDeletedExpenseLensItemsAsStale()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var stream = new RecurringFinancialStream
        {
            ClientProfileId = profile.Id,
            StreamKey = "checking:rent",
            NormalizedMerchantKey = "rent",
            DisplayName = "Rent",
            Cadence = "Monthly",
            AverageAmountCents = 150_000,
            Status = "Candidate",
            Confidence = 0.9m,
            EvidenceJson = "{}",
            FirstSeenUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            LastSeenUtc = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        db.RecurringFinancialStreams.Add(stream);
        db.FinanceToolStates.Add(new FinanceToolState
        {
            ClientProfileId = profile.Id,
            ToolId = "ExpenseLens",
            JsonState = """{"categories":[{"id":"rent"}]}"""
        });
        await db.SaveChangesAsync();

        var synchronization = new ExpenseLensSynchronizationService(
            db,
            NullLogger<ExpenseLensSynchronizationService>.Instance);
        var link = await synchronization.LinkStreamAsync(
            new ExpenseLensStreamLinkCommand(
                profile.Id,
                stream.Id,
                "rent",
                Confirmed: true,
                ConfirmedByUserId: "client-user"));

        Assert.True(link.Success);
        Assert.Equal("Confirmed", link.Link!.Status);

        var state = await db.FinanceToolStates.SingleAsync();
        state.JsonState = """{"categories":[]}""";
        await db.SaveChangesAsync();

        var result = await synchronization.SynchronizeAsync(profile.Id);

        Assert.True(result.Success);
        Assert.Equal(0, result.ValidLinkCount);
        Assert.Equal(1, result.StaleLinkCount);
        Assert.Equal("Stale", (await db.ExpenseLensStreamLinks.SingleAsync()).Status);
    }

    [Fact]
    public async Task SynchronizeAsync_UsesLegacyExpensesWhenCategoriesAreEmpty()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var stream = new RecurringFinancialStream
        {
            ClientProfileId = profile.Id,
            StreamKey = "checking:rent",
            NormalizedMerchantKey = "rent",
            DisplayName = "Rent",
            Cadence = "Monthly",
            AverageAmountCents = 150_000,
            Status = "Candidate",
            Confidence = 0.9m,
            EvidenceJson = "{}",
            FirstSeenUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            LastSeenUtc = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        db.RecurringFinancialStreams.Add(stream);
        db.FinanceToolStates.Add(new FinanceToolState
        {
            ClientProfileId = profile.Id,
            ToolId = "ExpenseLens",
            JsonState = """{"categories":[],"expenses":[{"id":"rent"}]}"""
        });
        await db.SaveChangesAsync();

        var synchronization = new ExpenseLensSynchronizationService(
            db,
            NullLogger<ExpenseLensSynchronizationService>.Instance);
        var link = await synchronization.LinkStreamAsync(
            new ExpenseLensStreamLinkCommand(
                profile.Id,
                stream.Id,
                "rent",
                Confirmed: true,
                ConfirmedByUserId: "client-user"));

        Assert.True(link.Success);

        var result = await synchronization.SynchronizeAsync(profile.Id);

        Assert.True(result.Success);
        Assert.Equal(1, result.ValidLinkCount);
        Assert.Equal(0, result.StaleLinkCount);
        Assert.Equal("Confirmed", (await db.ExpenseLensStreamLinks.SingleAsync()).Status);
    }

    [Fact]
    public void AddMasterAppFinancialIntelligence_RegistersEveryPhaseTwoServiceOnce()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<MasterAppDbContext>(options =>
            options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddMasterAppFinancialIntelligence(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.IsAssignableFrom<IFinancialConnectionService>(
            scope.ServiceProvider.GetRequiredService<IFinancialConnectionService>());
        Assert.IsAssignableFrom<IFinancialImportService>(
            scope.ServiceProvider.GetRequiredService<IFinancialImportService>());
        Assert.IsAssignableFrom<IRecurringFinancialStreamService>(
            scope.ServiceProvider.GetRequiredService<IRecurringFinancialStreamService>());
        Assert.IsAssignableFrom<IExpenseLensSynchronizationService>(
            scope.ServiceProvider.GetRequiredService<IExpenseLensSynchronizationService>());
        Assert.IsAssignableFrom<IFinancialIntelligenceEvaluationService>(
            scope.ServiceProvider.GetRequiredService<IFinancialIntelligenceEvaluationService>());
        Assert.Equal(1, services.Count(descriptor => descriptor.ServiceType == typeof(IFinancialConnectionService)));
        Assert.Equal(1, services.Count(descriptor => descriptor.ServiceType == typeof(IFinancialImportService)));
        Assert.Equal(1, services.Count(descriptor => descriptor.ServiceType == typeof(IRecurringFinancialStreamService)));
        Assert.Equal(1, services.Count(descriptor => descriptor.ServiceType == typeof(IExpenseLensSynchronizationService)));
        Assert.Equal(1, services.Count(descriptor => descriptor.ServiceType == typeof(IFinancialIntelligenceEvaluationService)));
        Assert.Equal(3, services.Count(descriptor => descriptor.ServiceType == typeof(IFinancialIntelligenceRule)));
    }

    [Fact]
    public async Task EvaluateAsync_ProducesIdempotentRecurringChargeReview_WithDecimalSafeImpact()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var connection = await AddConnectionAsync(db, profile.Id);
        var account = new ImportedFinancialAccount
        {
            ClientProfileId = profile.Id,
            FinancialDataConnectionId = connection.Id,
            ProviderAccountId = "acct-cad",
            Name = "Canadian checking",
            AccountType = "depository",
            CurrencyCode = "CAD"
        };
        db.ImportedFinancialAccounts.Add(account);
        db.RecurringFinancialStreams.Add(new RecurringFinancialStream
        {
            ClientProfileId = profile.Id,
            FinancialDataConnectionId = connection.Id,
            ImportedFinancialAccountId = account.Id,
            StreamKey = "checking:music",
            NormalizedMerchantKey = "music",
            DisplayName = "Music service",
            Cadence = "Monthly",
            AverageAmountCents = 12_501,
            Status = "Candidate",
            Confidence = 0.90m,
            EvidenceJson = "{}",
            FirstSeenUtc = Utc(2026, 1, 1),
            LastSeenUtc = Utc(2026, 3, 1)
        });
        await db.SaveChangesAsync();
        var evaluator = BuildEvaluator(db);

        var first = await evaluator.EvaluateAsync(ClientActor(profile));
        var second = await evaluator.EvaluateAsync(ClientActor(profile));

        Assert.True(first.Success);
        Assert.True(second.Success);
        var finding = await db.FinancialFindings.SingleAsync();
        var observation = await db.FinancialObservations.SingleAsync();
        Assert.Equal(12_501m / 100m, finding.EstimatedImpact);
        Assert.Equal(12_501m / 100m, observation.NumericValue);
        Assert.Equal("CAD per Monthly", finding.ImpactUnit);
        Assert.Equal(FinancialFindingStatuses.Active, finding.Status);
        Assert.Equal(1, await db.FinancialFindings.CountAsync());
        Assert.Equal(1, await db.FinancialObservations.CountAsync());
        Assert.Single(await db.FinancialFindingObservations.ToListAsync());
    }

    [Fact]
    public async Task EvaluateAsync_MissingReliableInputs_ProducesNoFinding_AndLeavesFinanceStateUntouched()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        const string stateJson = """{"cashFlow":{"earnings":0}}""";
        db.FinanceToolStates.Add(new FinanceToolState
        {
            ClientProfileId = profile.Id,
            ToolId = LegendLivingBalanceSheetConstants.ToolId,
            JsonState = stateJson
        });
        await db.SaveChangesAsync();

        var result = await BuildEvaluator(db).EvaluateAsync(ClientActor(profile));

        Assert.True(result.Success);
        Assert.Empty(await db.FinancialFindings.ToListAsync());
        Assert.Equal(stateJson, (await db.FinanceToolStates.SingleAsync()).JsonState);
        Assert.Equal(0m, result.Snapshot!.DataCompletenessScore);
    }

    [Fact]
    public async Task EvaluateAsync_ResolvedRecurringCondition_ClosesPriorFindingWithoutDeletingHistory()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        await AddConnectionAsync(db, profile.Id);
        var stream = new RecurringFinancialStream
        {
            ClientProfileId = profile.Id,
            StreamKey = "checking:utility",
            NormalizedMerchantKey = "utility",
            DisplayName = "Utility service",
            Cadence = "Monthly",
            AverageAmountCents = 95_00,
            Status = "Candidate",
            Confidence = 0.90m,
            EvidenceJson = "{}",
            FirstSeenUtc = Utc(2026, 1, 1),
            LastSeenUtc = Utc(2026, 3, 1)
        };
        db.RecurringFinancialStreams.Add(stream);
        await db.SaveChangesAsync();
        var evaluator = BuildEvaluator(db);
        await evaluator.EvaluateAsync(ClientActor(profile));

        stream.Status = "Inactive";
        await db.SaveChangesAsync();
        await evaluator.EvaluateAsync(ClientActor(profile));

        var finding = await db.FinancialFindings.SingleAsync();
        var observation = await db.FinancialObservations.SingleAsync();
        Assert.Equal(FinancialFindingStatuses.Resolved, finding.Status);
        Assert.NotNull(finding.ResolvedUtc);
        Assert.Equal("Superseded", observation.Status);
        Assert.NotNull(observation.SupersededUtc);
    }

    [Fact]
    public async Task CashFlowShortfall_RequiresAgentReview_AndClientSeesItOnlyAfterReview()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        db.AgentClients.Add(new AgentClient
        {
            AgentUserId = "agent-finance",
            ClientUserId = profile.ClientUserId
        });
        db.FinanceToolStates.Add(new FinanceToolState
        {
            ClientProfileId = profile.Id,
            ToolId = LegendLivingBalanceSheetConstants.ToolId,
            JsonState = JsonSerializer.Serialize(new LegendLivingBalanceSheetState
            {
                CashFlow = new LegendBalanceSheetCashFlow
                {
                    Earnings = 100_000m,
                    InsuranceCosts = 10_000m,
                    AnnualSavings = 15_000m,
                    DebtObligations = 85_000m
                }
            })
        });
        await db.SaveChangesAsync();
        var evaluator = BuildEvaluator(db);

        var evaluation = await evaluator.EvaluateAsync(ClientActor(profile));
        var clientBeforeReview = await evaluator.GetSnapshotAsync(ClientActor(profile));
        var agentActor = AgentActor(profile, "agent-finance");
        var agentSnapshot = await evaluator.GetSnapshotAsync(agentActor);

        Assert.True(evaluation.Success);
        Assert.Empty(clientBeforeReview!.Findings);
        var finding = Assert.Single(agentSnapshot!.Findings);
        Assert.True(finding.RequiresAgentReview);
        Assert.Equal(FinancialFindingCategories.Risk, finding.Category);

        var feedback = await evaluator.RecordFeedbackAsync(
            agentActor,
            new FinancialIntelligenceFeedbackCommand(finding.Id, FinancialFindingFeedbackTypes.AgentReviewed));
        var clientAfterReview = await evaluator.GetSnapshotAsync(ClientActor(profile));

        Assert.True(feedback.Success);
        Assert.Single(clientAfterReview!.Findings);
        Assert.NotNull((await db.FinancialFindings.SingleAsync()).AgentReviewedUtc);
    }

    [Fact]
    public async Task Feedback_ChangesLaterRankingWithinBoundedRules_AndCannotDismissUrgentRisk()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        await AddConnectionAsync(db, profile.Id);
        db.AgentClients.Add(new AgentClient { AgentUserId = "agent-finance", ClientUserId = profile.ClientUserId });
        db.RecurringFinancialStreams.Add(new RecurringFinancialStream
        {
            ClientProfileId = profile.Id,
            StreamKey = "checking:news",
            NormalizedMerchantKey = "news",
            DisplayName = "News service",
            Cadence = "Monthly",
            AverageAmountCents = 50_00,
            Status = "Candidate",
            Confidence = 0.90m,
            EvidenceJson = "{}",
            FirstSeenUtc = Utc(2026, 1, 1),
            LastSeenUtc = Utc(2026, 3, 1)
        });
        db.FinanceToolStates.Add(new FinanceToolState
        {
            ClientProfileId = profile.Id,
            ToolId = LegendLivingBalanceSheetConstants.ToolId,
            JsonState = JsonSerializer.Serialize(new LegendLivingBalanceSheetState
            {
                CashFlow = new LegendBalanceSheetCashFlow { Earnings = 10_000m, DebtObligations = 15_000m }
            })
        });
        await db.SaveChangesAsync();
        var evaluator = BuildEvaluator(db);
        await evaluator.EvaluateAsync(ClientActor(profile));

        var recurring = await db.FinancialFindings.SingleAsync(x => x.FindingType == "RecurringChargeReview");
        var scoreBeforeHelpful = recurring.PriorityScore;
        await evaluator.RecordFeedbackAsync(
            ClientActor(profile),
            new FinancialIntelligenceFeedbackCommand(recurring.Id, FinancialFindingFeedbackTypes.Helpful));
        await evaluator.EvaluateAsync(ClientActor(profile));
        var scoreAfterHelpful = (await db.FinancialFindings.SingleAsync(x => x.Id == recurring.Id)).PriorityScore;

        var cashFlow = await db.FinancialFindings.SingleAsync(x => x.FindingType == "AnnualCashFlowShortfall");
        await evaluator.RecordFeedbackAsync(
            AgentActor(profile, "agent-finance"),
            new FinancialIntelligenceFeedbackCommand(cashFlow.Id, FinancialFindingFeedbackTypes.AgentReviewed));
        var dismiss = await evaluator.RecordFeedbackAsync(
            ClientActor(profile),
            new FinancialIntelligenceFeedbackCommand(cashFlow.Id, FinancialFindingFeedbackTypes.Dismissed));

        Assert.True(scoreAfterHelpful > scoreBeforeHelpful);
        Assert.True(FinancialIntelligencePrioritization.CalculateFeedbackAdjustment(Enumerable.Repeat(FinancialFindingFeedbackTypes.Helpful, 20)) <= 8m);
        Assert.True(dismiss.Success);
        Assert.Equal(FinancialFindingStatuses.Active, (await db.FinancialFindings.SingleAsync(x => x.Id == cashFlow.Id)).Status);
    }

    [Fact]
    public async Task EvaluationAndSnapshots_AreClientIsolated_AndUnauthorizedAgentIsRejected()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var first = await AddClientProfileAsync(db);
        var second = await AddClientProfileAsync(db);
        await AddConnectionAsync(db, first.Id);
        db.RecurringFinancialStreams.Add(new RecurringFinancialStream
        {
            ClientProfileId = first.Id,
            StreamKey = "checking:first",
            NormalizedMerchantKey = "first",
            DisplayName = "First client charge",
            Cadence = "Monthly",
            AverageAmountCents = 10_00,
            Status = "Candidate",
            Confidence = 0.90m,
            EvidenceJson = "{}",
            FirstSeenUtc = Utc(2026, 1, 1),
            LastSeenUtc = Utc(2026, 3, 1)
        });
        await db.SaveChangesAsync();
        var evaluator = BuildEvaluator(db);

        await evaluator.EvaluateAsync(ClientActor(first));
        var crossClientSnapshot = await evaluator.GetSnapshotAsync(ClientActor(second) with { ClientProfileId = first.Id });
        var unauthorizedAgentResult = await evaluator.EvaluateAsync(AgentActor(second, "agent-without-link"));

        Assert.Null(crossClientSnapshot);
        Assert.False(unauthorizedAgentResult.Success);
        Assert.Equal(1, await db.FinancialFindings.CountAsync(x => x.ClientProfileId == first.Id));
        Assert.Equal(0, await db.FinancialFindings.CountAsync(x => x.ClientProfileId == second.Id));
    }

    [Fact]
    public async Task FinancialIntelligenceModel_CreatesWithExistingContext()
    {
        await using var db = ControllerTestHelpers.BuildDb();

        Assert.True(await db.Database.EnsureCreatedAsync());
    }

    [Fact]
    public void Prioritization_IsDeterministic_AndPreservesUrgentRiskVisibility()
    {
        var first = FinancialIntelligencePrioritization.Calculate(
            250m, "High", 0.90m, 0.50m, "Review", new[] { FinancialFindingFeedbackTypes.Dismissed });
        var second = FinancialIntelligencePrioritization.Calculate(
            250m, "High", 0.90m, 0.50m, "Review", new[] { FinancialFindingFeedbackTypes.Dismissed });

        Assert.Equal(first, second);
        Assert.InRange(first, 70m, 100m);
    }

    private static FinancialImportCommand CreateImportCommand(
        Guid clientProfileId,
        Guid connectionId,
        long amountCents)
    {
        return new FinancialImportCommand(
            clientProfileId,
            connectionId,
            new[]
            {
                new FinancialAccountImport(
                    ProviderAccountId: "acct-checking",
                    Name: "Checking",
                    AccountType: "depository")
            },
            new[]
            {
                new FinancialTransactionImport("rent-1", "acct-checking", "Rent Payment", Utc(2026, 1, 3), amountCents, OriginalMerchantName: "Northstar Rentals"),
                new FinancialTransactionImport("rent-2", "acct-checking", "Rent Payment", Utc(2026, 2, 3), amountCents, OriginalMerchantName: "Northstar Rentals"),
                new FinancialTransactionImport("rent-3", "acct-checking", "Rent Payment", Utc(2026, 3, 3), amountCents, OriginalMerchantName: "Northstar Rentals")
            },
            NextSyncCursor: "cursor-3");
    }

    private static async Task<ClientProfile> AddClientProfileAsync(MasterAppDbContext db)
    {
        var profile = new ClientProfile
        {
            ClientUserId = $"client-{Guid.NewGuid():N}",
            FirstName = "Financial",
            LastName = "Client",
            Email = "financial.client@example.test",
            Phone = "555-0100"
        };
        db.ClientProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile;
    }

    private static async Task<FinancialDataConnection> AddConnectionAsync(MasterAppDbContext db, Guid clientProfileId)
    {
        var connection = new FinancialDataConnection
        {
            ClientProfileId = clientProfileId,
            ProviderKey = "test-provider",
            ProviderItemId = $"item-{Guid.NewGuid():N}",
            Status = "Active"
        };
        db.FinancialDataConnections.Add(connection);
        await db.SaveChangesAsync();
        return connection;
    }

    private static DateTime Utc(int year, int month, int day) =>
        new(year, month, day, 12, 0, 0, DateTimeKind.Utc);

    private static FinancialIntelligenceEvaluationService BuildEvaluator(MasterAppDbContext db) => new(
        db,
        new IFinancialIntelligenceRule[]
        {
            new RecurringChargeReviewRule(),
            new StaleFinancialDataRule(),
            new CashFlowShortfallRule()
        },
        NullLogger<FinancialIntelligenceEvaluationService>.Instance);

    private static FinancialIntelligenceActor ClientActor(ClientProfile profile) => new(
        profile.Id,
        profile.ClientUserId,
        FinancialIntelligenceActorTypes.Client);

    private static FinancialIntelligenceActor AgentActor(ClientProfile profile, string agentUserId) => new(
        profile.Id,
        agentUserId,
        FinancialIntelligenceActorTypes.Agent);
}
