using System;
using System.Linq;
using System.Text.Json;
using Infrastructure.Bookings;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class ReceptionistBookingNoticeTests
{
    private static ReceptionistBusinessConfiguration Policy(string zone = "America/Phoenix", bool saturday = false) => new()
    {
        TimeZoneId = zone,
        Hours = Enum.GetValues<DayOfWeek>().Where(d => d != DayOfWeek.Sunday && (saturday || d != DayOfWeek.Saturday))
            .Select(d => new ReceptionistHoursWindow(d, new TimeOnly(6, 0), new TimeOnly(21, 0))).ToArray(),
        BookingPolicy = new() { MinimumNoticeBusinessDays = 1, MaximumAdvanceDays = 90 }
    };
    private static DateTime Utc(string value) => DateTime.Parse(value, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

    [Theory]
    [InlineData("2026-10-24T17:00:00Z", false)] // Saturday is closed.
    [InlineData("2026-10-26T16:59:00Z", false)] // Monday before Friday's local time.
    [InlineData("2026-10-26T17:00:00Z", true)]
    public void ClosedWeekdaysDoNotCountAsNotice(string start, bool allowed) =>
        Assert.Equal(allowed, ReceptionistBookingAuthority.NoticeAllows(Utc(start), Utc("2026-10-23T17:00:00Z"), Policy()));

    [Fact]
    public void SaturdayCountsWhenConfiguredOpen()
    {
        Assert.True(ReceptionistBookingAuthority.NoticeAllows(Utc("2026-10-24T17:00:00Z"), Utc("2026-10-23T17:00:00Z"), Policy(saturday: true)));
        Assert.False(ReceptionistBookingAuthority.NoticeAllows(Utc("2026-10-25T17:00:00Z"), Utc("2026-10-24T17:00:00Z"), Policy(saturday: true)));
    }

    [Fact]
    public void NoticeUsesBusinessWallTimeAcrossDaylightSaving()
    {
        var policy = Policy("America/New_York");
        Assert.False(ReceptionistBookingAuthority.NoticeAllows(Utc("2026-03-09T13:59:00Z"), Utc("2026-03-06T15:00:00Z"), policy));
        Assert.True(ReceptionistBookingAuthority.NoticeAllows(Utc("2026-03-09T14:00:00Z"), Utc("2026-03-06T15:00:00Z"), policy));
    }

    [Fact]
    public void MinutesAndBusinessDaysBothApplyAndMissingHoursFailClosed()
    {
        var policy = Policy(saturday: true);
        Assert.False(ReceptionistBookingAuthority.NoticeAllows(Utc("2026-10-24T17:00:00Z"), Utc("2026-10-23T17:00:00Z"),
            policy with { BookingPolicy = policy.BookingPolicy! with { MinimumNoticeMinutes = 2880 } }));
        Assert.False(ReceptionistBookingAuthority.NoticeAllows(Utc("2026-10-26T17:00:00Z"), Utc("2026-10-23T17:00:00Z"), policy with { Hours = [] }));
    }

    [Fact]
    public void DefaultDoesNotChangeHistoricalPolicySerialization()
    {
        var json = JsonSerializer.Serialize(new ReceptionistBookingPolicy());
        Assert.DoesNotContain("MinimumNoticeBusinessDays", json);
        Assert.Equal(0, JsonSerializer.Deserialize<ReceptionistBookingPolicy>("{}")!.MinimumNoticeBusinessDays);
    }
}
