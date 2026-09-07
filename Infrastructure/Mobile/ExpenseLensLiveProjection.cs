using System.Globalization;
using System.Text.Json;
using Jint;

namespace Infrastructure.Mobile;

/// <summary>Executes the exact web calculator against database inputs. No snapshots are saved.</summary>
internal static class ExpenseLensLiveProjection
{
    private static readonly string Calculator = LoadCalculator();

    public static JsonDocument Project(JsonElement state, DateOnly date, CancellationToken cancellationToken)
    {
        // Only trusted embedded source executes; account data enters as a JSON string.
        using var engine = new Engine(options => options
            .CancellationToken(cancellationToken)
            .LimitMemory(64_000_000)
            .TimeoutInterval(TimeSpan.FromSeconds(5))
            .MaxStatements(5_000_000)
            .LocalTimeZone(TimeZoneInfo.Utc));
        engine.Execute(Calculator);
        engine.SetValue("financeJson", state.GetRawText());
        engine.SetValue("reportDate", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var json = engine.Evaluate("""
            JSON.stringify((function () {
                const state = JSON.parse(financeJson);
                const date = new Date(reportDate + 'T12:00:00Z');
                const api = LegendExpenseLensProjection;
                const projection = api.projectExpenseLensTimeline({
                    state: state, asOfDate: date, selectedMonthKey: reportDate.slice(0, 7)
                });
                return {
                    mobileWeekProjection: api.buildMobileWeekSnapshot(projection, date),
                    mobileMonthProjection: api.buildMobileMonthSnapshot(projection, date)
                };
            })())
            """).AsString();
        return JsonDocument.Parse(json);
    }

    private static string LoadCalculator()
    {
        using var stream = typeof(ExpenseLensLiveProjection).Assembly
            .GetManifestResourceStream("Legend.ExpenseLensProjection.js")
            ?? throw new InvalidOperationException("The shared Expense Lens calculator is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
