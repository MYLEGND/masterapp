namespace AgentPortal.Helpers;

public static class TimeZoneIdMapper
{
    private static readonly IReadOnlyDictionary<string, string> IanaToWindows = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["America/New_York"] = "Eastern Standard Time",
        ["America/Chicago"] = "Central Standard Time",
        ["America/Denver"] = "Mountain Standard Time",
        ["America/Phoenix"] = "US Mountain Standard Time",
        ["America/Los_Angeles"] = "Pacific Standard Time",
        ["America/Anchorage"] = "Alaskan Standard Time",
        ["Pacific/Honolulu"] = "Hawaiian Standard Time"
    };

    public static string ToWindowsId(string? timeZoneId)
    {
        var id = (timeZoneId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(id))
            return "UTC";

        return IanaToWindows.TryGetValue(id, out var windowsId)
            ? windowsId
            : id;
    }

    public static string ToGraphTimeZoneId(TimeZoneInfo? timeZone)
        => ToWindowsId(timeZone?.Id);

    public static bool TryFindTimeZone(string? timeZoneId, out TimeZoneInfo timeZone)
    {
        var id = (timeZoneId ?? "").Trim();

        foreach (var candidate in new[] { id, ToWindowsId(id) }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                timeZone = TimeZoneInfo.FindSystemTimeZoneById(candidate);
                return true;
            }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        timeZone = TimeZoneInfo.Utc;
        return false;
    }
}
