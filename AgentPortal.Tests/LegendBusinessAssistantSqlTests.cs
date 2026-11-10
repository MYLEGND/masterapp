using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Analytics;
using Infrastructure.Data;
using Infrastructure.Reception;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class PersistentAssistantFactAttribute : FactAttribute
{
    public PersistentAssistantFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LEGEND_TEST_DB_IDENTITY")))
            Skip = "Run scripts/db.sh local test --filter FullyQualifiedName~LegendBusinessAssistantSqlTests against persistent synthetic SQL.";
    }
}

public sealed class LegendBusinessAssistantSqlTests
{
    [PersistentAssistantFact]
    public async Task ConcurrentDuplicateCannotAuthorizeAnotherProviderAttempt()
    {
        await using var f = await Fixture.CreateAsync(25m);
        var selection = await f.AddOwnerAsync();
        var operation = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => f.Admit(selection, operation)));
        Assert.Single(results.Where(x => x.Admitted));
        Assert.All(results.Where(x => !x.Admitted), x => Assert.Equal("operation_already_reserved", x.Reason));
        Assert.Single(results.Select(x => x.ReservationId).Distinct());
    }

    [PersistentAssistantFact]
    public async Task SharedFundingCapAppliesAcrossOwnersAndTenantTamperingIsRejected()
    {
        await using var f = await Fixture.CreateAsync(1m);
        var first = await f.AddOwnerAsync();
        var second = await f.AddOwnerAsync();
        Assert.True((await f.Admit(first, Guid.NewGuid())).Admitted);
        Assert.Equal("shared_cash_limit", (await f.Admit(second, Guid.NewGuid())).Reason);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Admission.ReserveAssistantAsync(second.Owner,
            first.Snapshot, first.Evidence, Guid.NewGuid(), new string('a', 64), _ => Task.CompletedTask));
    }

    [PersistentAssistantFact]
    public async Task UnknownPriorPeriodHoldSurvivesRolloverAndRevocationStopsNewWork()
    {
        await using var f = await Fixture.CreateAsync(1m);
        var selection = await f.AddOwnerAsync();
        var first = await f.Admit(selection, Guid.NewGuid());
        Assert.True(first.Admitted);
        await using (var db = f.Open())
        {
            var periods = await db.Set<ReceptionistBudgetPeriod>().Where(x => x.BudgetAccountId == selection.OwnerAccount || x.BudgetAccountId == f.FundingId).ToListAsync();
            foreach (var period in periods)
            {
                period.EndUtc = DateTime.UtcNow.AddMinutes(-1);
                db.Add(new ReceptionistBudgetPeriod { BudgetAccountId = period.BudgetAccountId, MonthKey = period.MonthKey + 1,
                    StartUtc = DateTime.UtcNow.AddSeconds(-30), EndUtc = DateTime.UtcNow.AddDays(1),
                    CashLimit = period.CashLimit, ConsumptionLimit = period.ConsumptionLimit, OpeningReconciled = true,
                    OpeningReceipt = "synthetic-rollover", ReconciledThroughUtc = DateTime.UtcNow.AddSeconds(-30) });
            }
            await db.SaveChangesAsync();
        }
        Assert.Equal("shared_cash_limit", (await f.Admit(selection, Guid.NewGuid())).Reason);
        await using (var db = f.Open())
        {
            var connection = await db.MarketingConnections.SingleAsync(x => x.Id == selection.Snapshot.ConnectionId);
            connection.DisconnectedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        Assert.Equal("connection_changed_or_revoked", (await f.Admit(selection, Guid.NewGuid())).Reason);
    }

    [PersistentAssistantFact]
    public async Task UnattributedOpeningConsumesFeatureAllocationAndExpiredEvidenceCannotAdmit()
    {
        await using var f = await Fixture.CreateAsync(25m);
        var selection = await f.AddOwnerAsync(allocation: 1m);
        await using (var db = f.Open())
        {
            var period = await db.Set<ReceptionistBudgetPeriod>().SingleAsync(x => x.BudgetAccountId == selection.OwnerAccount);
            period.OpeningCashCommitted = period.CashCommitted = 0.5m;
            await db.SaveChangesAsync();
        }
        Assert.Equal("feature_allocation_exhausted", (await f.Admit(selection, Guid.NewGuid())).Reason);
        var expired = selection with { Evidence = selection.Evidence with { ExpiresUtc = DateTime.UtcNow.AddSeconds(-1) } };
        Assert.Equal("cost_bound_unverified_or_expired", (await f.Admit(expired, Guid.NewGuid())).Reason);
    }

    private sealed record Selection(MarketingOwnerScope Owner, Guid OwnerAccount,
        BusinessAssistantConnectionStatus Snapshot, AssistantFundingEvidence Evidence);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string connection;
        private readonly List<Guid> businessIds = [];
        private readonly List<Guid> accountIds = [];
        public Guid FundingId { get; private set; }
        private readonly string project = "proj_synthetic_" + Guid.NewGuid().ToString("N");
        public ReceptionistAdmissionService Admission { get; }
        private readonly DbContextOptions<MasterAppDbContext> options;
        private Fixture(string connection)
        {
            this.connection = connection;
            options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlServer(connection).Options;
            Admission = new(options, TimeProvider.System);
        }
        public MasterAppDbContext Open() => new(options);
        public static async Task<Fixture> CreateAsync(decimal fundingLimit)
        {
            var connection = Environment.GetEnvironmentVariable("MASTERAPP_LOCAL_DB_CONNECTION") ?? throw new InvalidOperationException("Use the local test owner.");
            var target = new SqlConnectionStringBuilder(connection);
            Assert.Equal("127.0.0.1,14339", target.DataSource);
            Assert.Equal("MasterAppLocalTest", target.InitialCatalog);
            await using var sql = new SqlConnection(connection);
            await sql.OpenAsync();
            await using var command = sql.CreateCommand();
            command.CommandText = "SELECT CONVERT(nvarchar(100),value) FROM sys.extended_properties WHERE class=0 AND name=N'LegendLocalTestIdentity'";
            Assert.Equal(Environment.GetEnvironmentVariable("LEGEND_TEST_DB_IDENTITY"), await command.ExecuteScalarAsync());
            var result = new Fixture(connection);
            await using var db = result.Open();
            var funding = result.Account("funding", result.project);
            result.FundingId = funding.Id;
            db.Add(funding); db.Add(Period(funding.Id, fundingLimit)); await db.SaveChangesAsync();
            return result;
        }
        private ReceptionistBudgetAccount Account(string kind, string key)
        {
            var account = new ReceptionistBudgetAccount { Kind = kind, ScopeKey = key, Currency = "USD", TimeZoneId = "UTC", AdmissionEnabled = true };
            accountIds.Add(account.Id); return account;
        }
        private static ReceptionistBudgetPeriod Period(Guid account, decimal limit) => new() {
            BudgetAccountId = account, MonthKey = 202611, StartUtc = DateTime.UtcNow.AddDays(-1), EndUtc = DateTime.UtcNow.AddDays(1),
            CashLimit = limit, ConsumptionLimit = limit, OpeningReconciled = true, OpeningReceipt = "synthetic-opening",
            ReconciledThroughUtc = DateTime.UtcNow.AddDays(-1) };
        public async Task<Selection> AddOwnerAsync(decimal allocation = 25m)
        {
            await using var db = Open();
            var business = new CommerceBusiness { Key = "synthetic-assistant-" + Guid.NewGuid().ToString("N"), DisplayName = "Synthetic assistant test" };
            businessIds.Add(business.Id);
            var owner = MarketingOwnerScope.Business(business.Id);
            var account = Account("owner", owner.Key);
            db.Add(business); db.Add(account); db.Add(Period(account.Id, 25m));
            var configuration = new BusinessAssistantConfiguration("openai", project, null, "synthetic-no-call-model", account.Id,
                FundingId, allocation, 25m, 1m, 1m, 512, new string('a', 64), DateTime.UtcNow.AddHours(1), true);
            using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
            var row = new MarketingConnection { OwnerKey = owner.Key, OwnerType = owner.OwnerType, CommerceBusinessId = business.Id, Provider = "openai",
                AssistantConfigurationJson = System.Text.Json.JsonSerializer.Serialize(configuration, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
                AssistantCredentialCiphertext = protector.Protect(owner, "assistant-api", "synthetic-never-dispatched") };
            db.Add(row); await db.SaveChangesAsync();
            var funding = await db.Set<ReceptionistBudgetAccount>().SingleAsync(x => x.Id == FundingId);
            var snapshot = new BusinessAssistantConnectionStatus(row.Id, row.AssistantControlRevision, owner.Key, configuration, true,
                "funding_admission_required", "unknown", DateTime.UtcNow);
            return new(owner, account.Id, snapshot, new AssistantFundingEvidence { Verified = true, ConnectionId = row.Id,
                ControlRevision = row.AssistantControlRevision, ExpectedConnectionRevision = row.Revision, ExpiresUtc = DateTime.UtcNow.AddMinutes(10),
                ExpectedAccountRevisions = new Dictionary<Guid, Guid> { [account.Id] = account.Revision, [funding.Id] = funding.Revision },
                FundingScopes = new Dictionary<Guid, string> { [funding.Id] = funding.ScopeKey } });
        }
        public Task<AssistantFundingAdmission> Admit(Selection selection, Guid operation) => Admission.ReserveAssistantAsync(
            selection.Owner, selection.Snapshot, selection.Evidence, operation, new string('a', 64), _ => Task.CompletedTask);
        public async ValueTask DisposeAsync()
        {
            await using var db = Open();
            await db.Set<ReceptionistCostReservation>().Where(x => accountIds.Contains(x.OwnerBudgetAccountId)).ExecuteDeleteAsync();
            await db.Set<ReceptionistBudgetPeriod>().Where(x => accountIds.Contains(x.BudgetAccountId)).ExecuteDeleteAsync();
            await db.MarketingConnections.Where(x => x.CommerceBusinessId.HasValue && businessIds.Contains(x.CommerceBusinessId.Value)).ExecuteDeleteAsync();
            await db.Set<ReceptionistBudgetAccount>().Where(x => accountIds.Contains(x.Id)).ExecuteDeleteAsync();
            await db.CommerceBusinesses.Where(x => businessIds.Contains(x.Id)).ExecuteDeleteAsync();
        }
    }
}
