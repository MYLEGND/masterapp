using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class PublicWebsitePublicationReadTests
{
    [Theory]
    [InlineData("legend")]
    [InlineData("protect")]
    public async Task ScopeReadExcludesCompiledHtmlAndPreservesIdentityIsolationAndCardinality(string site)
    {
        using var connection = new SqliteConnection("Data Source=:memory:"); connection.Open();
        var audit = new ReadAudit();
        using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlite(connection).AddInterceptors(audit).Options);
        await db.Database.EnsureCreatedAsync();
        var state = new WebsiteContentState { OwnerKey = "synthetic-owner", SiteKey = site };
        var version = new WebsiteContentVersion { StateId = state.Id, Revision = 42,
            DocumentJson = "{\"pages\":{\"/\":{}}}", CompiledPagesJson = new string('x', 2_000_000),
            ActorUserId = "synthetic-actor", ImportReportJson = "{}" };
        state.PublishedVersionId = version.Id; db.AddRange(state, version); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var config = new ConfigurationBuilder().Build();
        var resolver = new PublicWebsiteRuntimeScopeResolver(db, new WebsiteDomainService(db, Mock.Of<IHttpClientFactory>(), config), config);
        var read = (Func<string,string,CancellationToken,Task<WebsiteContentVersion?>>)typeof(PublicWebsiteRuntimeScopeResolver)
            .GetMethod("PublishedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(Func<string,string,CancellationToken,Task<WebsiteContentVersion?>>), resolver);
        audit.Commands.Clear(); audit.Reset();
        var result = await read(state.OwnerKey, site, default);
        Assert.NotNull(result); Assert.Null(result.CompiledPagesJson);
        Assert.Equal(version.DocumentJson, result.DocumentJson); Assert.Equal(version.Id, result.Id);
        Assert.Equal(version.StateId, result.StateId); Assert.Equal(42, result.Revision);
        Assert.Equal(version.ActorUserId, result.ActorUserId); Assert.Equal(version.ImportReportJson, result.ImportReportJson);
        Assert.Single(audit.Commands); Assert.DoesNotContain("CompiledPagesJson", audit.Commands[0]);
        Assert.Contains("DocumentJson", audit.Commands[0]); Assert.False(audit.AsyncRead);
        Assert.Null(await read("foreign-owner", site, default));
        Assert.Null(await read(state.OwnerKey, "foreign-site", default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var before = audit.Commands.Count;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read(state.OwnerKey, site, cancelled.Token));
        Assert.Equal(before, audit.Commands.Count);
        // Corrupt-schema simulation is confined to this disposable in-memory DB.
        // Production retains the unique index; the reader must still fail on duplicates.
        await db.Database.ExecuteSqlRawAsync("DROP INDEX IX_WebsiteContentState_OwnerKey_SiteKey");
        var duplicate = new WebsiteContentState { OwnerKey = state.OwnerKey, SiteKey = site };
        var duplicateVersion = new WebsiteContentVersion { StateId = duplicate.Id };
        duplicate.PublishedVersionId = duplicateVersion.Id; db.AddRange(duplicate, duplicateVersion); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => read(state.OwnerKey, site, default));
        duplicateVersion.StateId = state.Id; await db.SaveChangesAsync();
        Assert.Equal(version.Id, (await read(state.OwnerKey, site, default))!.Id);
    }

    private sealed class ReadAudit : DbCommandInterceptor
    {
        public List<string> Commands { get; } = new();
        public bool AsyncRead { get; private set; }
        public void Reset() => AsyncRead = false;
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        { Commands.Add(command.CommandText); return result; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { AsyncRead = true; return new(result); }
    }
}
