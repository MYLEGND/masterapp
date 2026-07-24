using System;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Entities.FinancialIntelligence;
using Domain.FinancialIntelligence;
using Infrastructure.Data;
using Infrastructure.FinancialIntelligence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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
        Assert.Equal(4, services.Count(descriptor => descriptor.ServiceType.Namespace == "Domain.FinancialIntelligence"));
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
}
