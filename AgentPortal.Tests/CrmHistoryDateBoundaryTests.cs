using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Crm;
using Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class CrmHistoryTests
{
    [Fact]
    public void SqlServerGuidTieOrderPreservesPagePrefixes()
    {
        var ids = Enumerable.Range(1, 120).Select(index =>
            Guid.Parse($"{index:x8}-0000-0000-0000-{121-index:x12}")).ToArray();
        var databaseOrder = ids.OrderBy(id => new System.Data.SqlTypes.SqlGuid(id)).ToArray();
        Assert.False(databaseOrder.SequenceEqual(ids.OrderBy(id => id)));
        var comparer = AgentPortal.Controllers.CrmHistoryController.HistoryIdComparer(true);
        Guid[] Page(int page) => databaseOrder.Take(page * 50 + 50).OrderBy(id => id, comparer).Skip(page * 50).Take(50).ToArray();
        var all = Page(0).Concat(Page(1)).Concat(Page(2)).ToArray();
        Assert.Equal(databaseOrder, all);
        Assert.Equal(120, all.Distinct().Count());
        var wrong = databaseOrder.Take(50).OrderBy(id => id).Concat(databaseOrder.Take(100).OrderBy(id => id).Skip(50)).ToArray();
        Assert.True(wrong.Distinct().Count() < wrong.Length);
        Assert.Equal(ids.OrderBy(id => id).ToArray(), ids.OrderBy(id => id,
            AgentPortal.Controllers.CrmHistoryController.HistoryIdComparer(false)).ToArray());
    }

    [Fact]
    public async Task EqualTimePagesRemainDeterministicAcrossHistoricalImports()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.WorkstationLeadProfiles.Add(new WorkstationLeadProfile { LeadId = "synthetic", AgentUserId = "owner" });
        await db.SaveChangesAsync();
        var store = new CrmHistoryStore(db);
        var instant = new DateTime(2026, 1, 1, 17, 0, 0, DateTimeKind.Utc);
        var entries = new List<CrmHistoryEntry>();
        for (var index = 0; index < 63; index++)
        {
            var entry = New("page-" + index);
            entry.OccurredUtc = index < 61 ? instant : null;
            entry.OccurredDate = index == 61 ? "2026-01-01" : null;
            entry.TimePrecision = index < 61 ? "instant" : index == 61 ? "date" : "unknown";
            entry.RecordedUtc = instant.AddMinutes(index / 2);
            entries.Add(await store.StageAsync(entry));
        }
        await db.SaveChangesAsync();
        var controller = BuildController(db, "owner");
        async Task<JsonElement> Page(int page, DateTime? snapshot = null, bool recorded = false)
        {
            var result = Assert.IsType<OkObjectResult>(await controller.Entries("lead", "synthetic", null, null,
                null, null, null, null, null, null, null, page: page, recordedTime: recorded, snapshotUtc: snapshot));
            return JsonSerializer.SerializeToElement(result.Value);
        }
        static Guid[] Ids(JsonElement page) => page.GetProperty("entries").EnumerateArray().Select(x => x.GetProperty("Id").GetGuid()).ToArray();
        var first = await Page(0);
        var snapshot = first.GetProperty("snapshotUtc").GetDateTime();
        var second = await Page(1, snapshot);
        var actual = Ids(first).Concat(Ids(second)).ToArray();
        var expected = entries.Where(x => x.OccurredUtc.HasValue).OrderByDescending(x => x.RecordedUtc).ThenBy(x => x.Id)
            .Select(x => x.Id).Concat(entries.Where(x => x.TimePrecision == "date").Select(x => x.Id))
            .Concat(entries.Where(x => x.TimePrecision == "unknown").Select(x => x.Id)).ToArray();
        Assert.Equal(63, first.GetProperty("count").GetInt32());
        Assert.Equal(50, Ids(first).Length);
        Assert.Equal(13, Ids(second).Length);
        Assert.Equal(expected, actual);
        Assert.Equal(63, actual.Distinct().Count());
        var late = New("late-paged-import"); late.OccurredUtc = instant.AddDays(1); late.OccurredDate = null;
        late.RecordedUtc = instant.AddYears(-1);
        await store.StageAsync(late); await db.SaveChangesAsync();
        Assert.Equal(Ids(first), Ids(await Page(0, snapshot)));
        Assert.Equal(Ids(second), Ids(await Page(1, snapshot)));
        var recorded = Ids(await Page(0, snapshot, true)).Concat(Ids(await Page(1, snapshot, true))).ToArray();
        Assert.Equal(entries.OrderByDescending(x => x.RecordedUtc).ThenBy(x => x.Id).Select(x => x.Id).ToArray(), recorded);
    }

    [Theory]
    [InlineData("today")]
    [InlineData("week")]
    [InlineData("month")]
    [InlineData("custom")]
    public async Task DateRangesRespectCalendarBoundaries(string range)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.WorkstationLeadProfiles.Add(new WorkstationLeadProfile { LeadId = "synthetic", AgentUserId = "owner" });
        await db.SaveChangesAsync();
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone));
        var first = range switch { "week" => today.AddDays(-((int)today.DayOfWeek + 6) % 7),
            "month" => new DateOnly(today.Year, today.Month, 1), "custom" => new DateOnly(2025, 12, 31), _ => today };
        var last = range == "custom" ? new DateOnly(2026, 1, 1) : today;
        DateTime Midnight(DateOnly day) => TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), zone);
        var start = Midnight(first); var end = Midnight(last.AddDays(1));
        var stamps = new[] { start.AddSeconds(-1), start, end.AddSeconds(-1), end };
        var store = new CrmHistoryStore(db); var ids = new List<Guid>();
        for (var index = 0; index < stamps.Length; index++)
        {
            var entry = New("boundary-" + index); entry.OccurredDate = null; entry.OccurredUtc = stamps[index];
            entry.TimePrecision = "instant"; entry.RecordedUtc = stamps[3 - index];
            ids.Add((await store.StageAsync(entry)).Id);
        }
        await db.SaveChangesAsync();
        var controller = BuildController(db, "owner");
        foreach (var recorded in new[] { false, true })
        {
            var result = Assert.IsType<OkObjectResult>(await controller.Entries("lead", "synthetic", null, null,
                null, null, null, null, null, null, null, recordedTime: recorded, range: range,
                fromDate: first.ToString("yyyy-MM-dd"), throughDate: last.ToString("yyyy-MM-dd")));
            var json = JsonSerializer.SerializeToElement(result.Value);
            Assert.Equal(2, json.GetProperty("count").GetInt32());
            var actual = json.GetProperty("entries").EnumerateArray().Select(x => x.GetProperty("Id").GetGuid()).ToArray();
            Assert.Equal(recorded ? new[] { ids[1], ids[2] } : new[] { ids[2], ids[1] }, actual);
        }
    }
}
