using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Data.Common;
using Domain.Entities;
using Infrastructure.Businesses;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class BusinessAccountReconciliationTransactionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedCommitIsReconciledFromDurableStateBeforeRetry(bool afterCommit)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync();
        var fault = new CommitFault(afterCommit);
        var options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).AddInterceptors(fault).Options;
        var profile = new ClientProfile { ClientUserId = "account-owner", Email = "account@example.test", CrmNotes = "{\"recordType\":\"BusinessClient\"}" };
        var business = new CommerceBusiness { Key = "durable-business" };
        var member = new CommerceBusinessMember { CommerceBusinessId = business.Id, ClientProfileId = profile.Id };
        await using (var db = new MasterAppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync(); db.AddRange(profile, business, member); await db.SaveChangesAsync();
            var request = new BusinessAccountReconciliationRequest(business.Id, profile.Id, profile.ClientUserId,
                [new(member.Id, business.Id, member.RoleKey, member.Status, member.UpdatedUtc)]);
            fault.Armed = true;
            await Assert.ThrowsAsync<TimeoutException>(() => new CommerceBusinessProvisioningService(db).ReconcileExistingAccountAsync(request));
        }
        await using var fresh = new MasterAppDbContext(options);
        var durable = await fresh.CommerceBusinessMembers.SingleAsync(x => x.Id == member.Id);
        Assert.Equal(afterCommit ? "account" : "owner", durable.RoleKey);
        var beforeHistory = (await fresh.CommerceBusinesses.SingleAsync(x => x.Id == business.Id)).OwnershipHistoryJson;
        Assert.Equal(afterCommit, beforeHistory is not null);
        var reviewed = new BusinessAccountReconciliationRequest(business.Id, profile.Id, profile.ClientUserId,
            [new(durable.Id, business.Id, durable.RoleKey, durable.Status, durable.UpdatedUtc)]);
        await new CommerceBusinessProvisioningService(fresh).ReconcileExistingAccountAsync(reviewed);
        var history = (await fresh.CommerceBusinesses.SingleAsync(x => x.Id == business.Id)).OwnershipHistoryJson!;
        Assert.Single(System.Text.Json.JsonDocument.Parse(history).RootElement.EnumerateArray());
        if (afterCommit) Assert.Equal(beforeHistory, history);
    }

    private sealed class CommitFault(bool afterCommit) : DbTransactionInterceptor
    {
        public bool Armed;
        private void Fail(bool committed) { if (Armed && committed == afterCommit) { Armed = false; throw new TimeoutException("Synthetic commit acknowledgment failure"); } }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        { Fail(false); return ValueTask.FromResult(result); }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        { Fail(true); return Task.CompletedTask; }
    }
}
