using System;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.WebsitePublishing;
using Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPortal.Tests;

public sealed class WebsiteSnapshotRetentionTests
{
    [Fact]
    public async Task ScheduleScanFailureDoesNotSkipRetirementAndCancellationStopsBothScans()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        using var services = new ServiceCollection()
            .AddDbContext<MasterAppDbContext>(options => options.UseInMemoryDatabase(databaseName))
            .BuildServiceProvider();
        using (var seed = services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<MasterAppDbContext>();
            var state = new WebsiteContentState { OwnerKey = "retention-owner", SiteKey = "legend", Revision = 51 };
            var versions = Enumerable.Range(1, 51).Select(index => new WebsiteContentVersion
            {
                StateId = state.Id, Revision = index, DocumentJson = "{\"saved\":true}"
            }).ToArray();
            state.PublishedVersionId = versions[^1].Id;
            db.Add(state); db.AddRange(versions); await db.SaveChangesAsync();
        }
        var scopeCalls = 0;
        var factory = new Moq.Mock<IServiceScopeFactory>();
        factory.Setup(value => value.CreateScope()).Returns(() =>
        {
            if (++scopeCalls == 1) throw new InvalidOperationException("Synthetic schedule scan failure");
            return services.CreateScope();
        });
        using var worker = new WebsitePublishWorker(factory.Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WebsitePublishWorker>.Instance);
        await worker.RunMaintenanceAsync(System.Threading.CancellationToken.None);
        using (var read = services.CreateScope())
        {
            var db = read.ServiceProvider.GetRequiredService<MasterAppDbContext>();
            Assert.Equal(50, await db.Set<WebsiteContentVersion>().CountAsync(value => value.SnapshotRetiredUtc == null));
            Assert.Equal(51, await db.Set<WebsiteContentVersion>().CountAsync());
            Assert.Equal("{}", (await db.Set<WebsiteContentVersion>().SingleAsync(value => value.Revision == 1)).DocumentJson);
        }
        using var cancelled = new System.Threading.CancellationTokenSource();
        cancelled.Cancel();
        var callsBefore = scopeCalls;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunMaintenanceAsync(cancelled.Token));
        Assert.Equal(callsBefore, scopeCalls);
    }

    [Fact]
    public void SqlServerMigrationGeneratesOnlyAdditiveSchemaChange()
    {
        using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlServer("Server=localhost;Database=synthetic_retention;Integrated Security=true;TrustServerCertificate=true").Options);
        var sql = db.GetService<IMigrator>().GenerateScript(
            "20261011064911_AddBusinessScopedMerchantPayments", "20261011120000_AddWebsiteSnapshotRetirement");
        Assert.Contains("ALTER TABLE [WebsiteContentVersion] ADD [SnapshotRetiredUtc] datetime2 NULL", sql);
        Assert.DoesNotContain("DROP", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RelationalCleanupPreservesForeignKeysAndRollsBackOnPublishRace()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options;
        await using var db = new MasterAppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var business = new CommerceBusiness { Id = Guid.NewGuid(), IsActive = true, Status = "Active" };
        var site = new WebsiteContentState { OwnerKey = "business", SiteKey = "business", CommerceBusinessId = business.Id, Revision = 51 };
        var versions = Enumerable.Range(1, 51).Select(i => new WebsiteContentVersion { StateId = site.Id, Revision = i, DocumentJson = "{\"saved\":true}" }).ToArray();
        site.PublishedVersionId = versions[^1].Id;
        var inquiry = new CommerceWebsiteInquiry { CommerceBusinessId = business.Id, PublishedVersionId = versions[0].Id, SubmissionId = Guid.NewGuid(), Name = "Synthetic" };
        var comment = new WebsiteStudioComment { WebsiteContentStateId = site.Id, WebsiteContentVersionId = versions[0].Id, Body = "Synthetic review", AuthorUserId = "synthetic" };
        db.AddRange(business, site, inquiry, comment);
        db.AddRange(versions);
        await db.SaveChangesAsync();
        await using (var cleanup = new MasterAppDbContext(options))
        {
            var stale = await cleanup.Set<WebsiteContentState>().SingleAsync();
            Assert.Equal(1, await WebsiteSnapshotRetention.StageAsync(cleanup, stale));
            site.Revision++;
            await db.SaveChangesAsync();
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => cleanup.SaveChangesAsync());
        }
        db.ChangeTracker.Clear();
        Assert.Null((await db.Set<WebsiteContentVersion>().SingleAsync(v => v.Id == versions[0].Id)).SnapshotRetiredUtc);
        var current = await db.Set<WebsiteContentState>().SingleAsync();
        Assert.Equal(1, await WebsiteSnapshotRetention.StageAsync(db, current));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(versions[0].Id, (await db.Set<CommerceWebsiteInquiry>().SingleAsync()).PublishedVersionId);
        Assert.Equal(versions[0].Id, (await db.Set<WebsiteStudioComment>().SingleAsync()).WebsiteContentVersionId);
        Assert.Equal("{}", (await db.Set<WebsiteContentVersion>().SingleAsync(v => v.Id == versions[0].Id)).DocumentJson);
    }

    [Fact]
    public void MigrationOnlyAddsRetirementMetadataWithoutDeletingOrRewiringHistory()
    {
        var migration = new Infrastructure.Migrations.AddWebsiteSnapshotRetirement();
        var column = Assert.IsType<Microsoft.EntityFrameworkCore.Migrations.Operations.AddColumnOperation>(Assert.Single(migration.UpOperations));
        Assert.Equal("WebsiteContentVersion", column.Table);
        Assert.Equal("SnapshotRetiredUtc", column.Name);
        Assert.True(column.IsNullable);
        using var db = ControllerTestHelpers.BuildDb();
        foreach (var entity in new[] { typeof(CommerceWebsiteInquiry), typeof(WebsiteStudioComment) })
            Assert.Contains(db.Model.FindEntityType(entity)!.GetForeignKeys(), fk => fk.PrincipalEntityType.ClrType == typeof(WebsiteContentVersion));
    }

    [Fact]
    public async Task RetiresOnlyOverflowPayloadsAndPreservesVersionLineageAndOtherSites()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var site = new WebsiteContentState { OwnerKey = "one", SiteKey = "legend", Revision = 65 };
        var other = new WebsiteContentState { OwnerKey = "two", SiteKey = "business" };
        var versions = Enumerable.Range(1, 65).Select(i => new WebsiteContentVersion
        {
            StateId = site.Id, Revision = i, DocumentJson = "{\"content\":\"old snapshot\"}",
            CompiledPagesJson = "rendered content", ImportReportJson = "import content", ActorUserId = "publisher"
        }).ToArray();
        site.PublishedVersionId = versions[^1].Id;
        var foreign = new WebsiteContentVersion { StateId = other.Id, DocumentJson = "preserved" };
        db.AddRange(site, other, foreign);
        db.AddRange(versions);
        await db.SaveChangesAsync();
        Assert.Equal(15, await WebsiteSnapshotRetention.StageAsync(db, site));
        await db.SaveChangesAsync();
        Assert.Equal(50, versions.Count(v => v.SnapshotRetiredUtc == null));
        Assert.All(versions.Take(15), v => {
            Assert.Equal("{}", v.DocumentJson);
            Assert.Null(v.CompiledPagesJson);
            Assert.Null(v.ImportReportJson);
            Assert.Equal("publisher", v.ActorUserId);
            Assert.Equal(site.Id, v.StateId);
        });
        Assert.Null(versions[^1].SnapshotRetiredUtc);
        Assert.Equal("preserved", foreign.DocumentJson);
        Assert.Equal(66, db.Set<WebsiteContentVersion>().Count());
        Assert.Equal(0, await WebsiteSnapshotRetention.StageAsync(db, site));
    }

    [Fact]
    public async Task NewPublicationCountsTowardFiftyBeforeTheAtomicSave()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var site = new WebsiteContentState { OwnerKey = "one", SiteKey = "legend", Revision = 50 };
        db.Add(site);
        db.AddRange(Enumerable.Range(1, 50).Select(i => new WebsiteContentVersion { StateId = site.Id, Revision = i }));
        await db.SaveChangesAsync();
        var published = new WebsiteContentVersion { StateId = site.Id, Revision = 51 };
        site.PublishedVersionId = published.Id;
        site.Revision++;
        db.Add(published);
        Assert.Equal(1, await WebsiteSnapshotRetention.StageAsync(db, site));
        await db.SaveChangesAsync();
        Assert.Equal(50, db.Set<WebsiteContentVersion>().Count(v => v.SnapshotRetiredUtc == null));
        Assert.Null(published.SnapshotRetiredUtc);
    }

    [Fact]
    public async Task UnexpectedOldActivePointerFailsClosedInsteadOfDeletingActiveContent()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var site = new WebsiteContentState { OwnerKey = "one", SiteKey = "legend" };
        var versions = Enumerable.Range(1, 51).Select(i => new WebsiteContentVersion { StateId = site.Id, Revision = i }).ToArray();
        site.PublishedVersionId = versions[0].Id;
        db.Add(site);
        db.AddRange(versions);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => WebsiteSnapshotRetention.StageAsync(db, site));
        Assert.All(versions, v => Assert.Null(v.SnapshotRetiredUtc));
    }
}
