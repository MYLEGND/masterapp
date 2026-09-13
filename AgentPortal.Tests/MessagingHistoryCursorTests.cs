using System;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MessagingHistoryCursorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompositeCursorTranslatesRelationallyAndPreservesEqualTimestampHistory(bool sqlServer)
    {
        var options = new DbContextOptionsBuilder<MasterAppDbContext>();
        if (sqlServer)
            options.UseSqlServer("Server=query-only.invalid;Database=cursor;Integrated Security=true;TrustServerCertificate=true");
        else
            options.UseSqlite("Data Source=:memory:");
        await using var db = new MasterAppDbContext(options.Options);
        var registry = new LegendLanguageRegistry(db, new ConfigurationBuilder().Build());
        var pairSql = registry.EnabledPairQuery("en", "ht").ToQueryString();
        Assert.Contains("LegendLanguage", pairSql);
        Assert.Contains("WHERE", pairSql);
        var timestamp = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var cursor = Guid.NewGuid();
        var query = MessagingService.ApplyConversationMessageCursor(db.InternalMessages.AsNoTracking(), timestamp, cursor);
        var sql = query.Select(message => message.Id).ToQueryString();
        Assert.Contains("SentUtc", sql);
        Assert.Contains("WHERE", sql);
        if (sqlServer)
            return; // Translation-only: no production SQL connection is opened.

        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE InternalMessages (Id TEXT NOT NULL PRIMARY KEY, SentUtc TEXT NOT NULL)");
        var ids = new[]
        {
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            Guid.Parse("00000001-0000-0000-0000-000000000002"),
            Guid.Parse("00000000-0001-0000-0000-000000000003"),
            Guid.Parse("00000000-0000-0001-0000-000000000004")
        };
        foreach (var id in ids)
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO InternalMessages (Id, SentUtc) VALUES ({id}, {timestamp})");
        var newest = await db.InternalMessages.OrderByDescending(message => message.SentUtc)
            .ThenByDescending(message => message.Id).Select(message => message.Id).Take(2).ToArrayAsync();
        var older = await MessagingService.ApplyConversationMessageCursor(db.InternalMessages, timestamp, newest[^1])
            .OrderByDescending(message => message.SentUtc).ThenByDescending(message => message.Id)
            .Select(message => message.Id).Take(2).ToArrayAsync();
        Assert.Equal(2, newest.Length);
        Assert.Equal(2, older.Length);
        Assert.Equal(4, newest.Concat(older).Distinct().Count());
        Assert.Empty(await MessagingService.ApplyConversationMessageCursor(db.InternalMessages, timestamp, null)
            .Select(message => message.Id).ToArrayAsync());
    }
}
