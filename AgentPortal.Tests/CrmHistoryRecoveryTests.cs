using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Leads;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class CrmHistoryTests
{
    private sealed class FailFiledSubmission : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<CrmHistoryEntry>().Any(x=>x.State==EntityState.Added && x.Entity.Type=="FormSubmission"))
                throw new DbUpdateException("Synthetic failure before history commit");
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task SubmissionFailureRollsBackAnswersAndHistoryThenSameReceiptRecoversOnce()
    {
        await using var connection=new SqliteConnection("Data Source=:memory:");await connection.OpenAsync();
        var failure=new FailFiledSubmission();
        var options=new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).AddInterceptors(failure).Options;
        var trackingId=Guid.NewGuid();var receipt=Guid.NewGuid().ToString("N");
        await using(var setup=new MasterAppDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Add(new AgentTrackingProfile { Id=trackingId,AgentUserId="synthetic-owner",AgentUpn="synthetic@example.test",Slug="synthetic-recovery",CreatedUtc=DateTime.UtcNow,UpdatedUtc=DateTime.UtcNow });
            await setup.SaveChangesAsync();
        }
        WebsiteLead Submitted()=>new(){LeadId=Guid.NewGuid(),AgentTrackingProfileId=trackingId,AgentSlug="synthetic-recovery",FirstName="Synthetic",Email="synthetic@example.test",InterestType="life",SourcePageKey="quote_life",CreatedUtc=new DateTime(2026,10,30,12,0,0,DateTimeKind.Utc),MetadataJson="{\"syntheticAnswer\":false,\"amount\":0}"};
        failure.Enabled=true;
        await using(var failed=new MasterAppDbContext(options))
            await Assert.ThrowsAsync<DbUpdateException>(()=>WebsiteLeadSubmission.TryCreateAsync(failed,Submitted(),receipt));
        failure.Enabled=false;
        await using(var recovered=new MasterAppDbContext(options))
        {
            Assert.Empty(await recovered.WebsiteLeads.ToArrayAsync());
            Assert.Empty(await recovered.WebsiteLeadIntakeLinks.ToArrayAsync());
            Assert.Empty(await recovered.Set<CrmHistoryEntry>().ToArrayAsync());
            Assert.True(await WebsiteLeadSubmission.TryCreateAsync(recovered,Submitted(),receipt));
        }
        // New request context simulates a committed response lost in transit.
        await using(var retry=new MasterAppDbContext(options))
        {
            var before=await retry.WebsiteLeadIntakeLinks.AsNoTracking().SingleAsync();
            Assert.False(await WebsiteLeadSubmission.TryCreateAsync(retry,Submitted(),receipt));
            Assert.Equal(1,await retry.WebsiteLeads.CountAsync());
            Assert.Equal(1,await retry.WebsiteLeadIntakeLinks.CountAsync());
            var tile=await retry.Set<CrmHistoryEntry>().SingleAsync(x=>x.Type=="FormSubmission");
            Assert.Equal(before.Id,tile.SubmissionId);
            Assert.Equal(before.SnapshotJson,(await retry.WebsiteLeadIntakeLinks.AsNoTracking().SingleAsync()).SnapshotJson);
            Assert.Contains("syntheticAnswer",before.SnapshotJson);
        }
    }
}
