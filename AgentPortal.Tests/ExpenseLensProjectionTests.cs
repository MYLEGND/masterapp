using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;

namespace AgentPortal.Tests;

public class ExpenseLensProjectionTests
{
    private const string ProjectionBridgeScript = """
ObjC.import('Foundation');

function readUtf8(path) {
    const text = $.NSString.stringWithContentsOfFileEncodingError(path, $.NSUTF8StringEncoding, null);
    if (!text) {
        throw new Error('Unable to read file: ' + path);
    }
    return ObjC.unwrap(text);
}

function pad(value) {
    return String(value).padStart(2, '0');
}

function formatLocalDate(date) {
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

function run(argv) {
    const functionName = argv[0];
    const modulePath = argv[1];
    const payload = argv[2] ? JSON.parse(argv[2]) : {};
    const globalObj = (typeof globalThis !== 'undefined') ? globalThis : this;
    var module = { exports: {} };
    var exports = module.exports;

    eval(readUtf8(modulePath));

    const api = module.exports && Object.keys(module.exports).length
        ? module.exports
        : globalObj.LegendExpenseLensProjection;
    if (!api) {
        throw new Error('LegendExpenseLensProjection did not load.');
    }

    let result;
    if (functionName === 'projectExpenseLensTimeline') {
        result = api.projectExpenseLensTimeline(payload);
    } else if (functionName === 'normalizeState') {
        result = api.normalizeState(payload);
    } else if (functionName === 'getScheduledOccurrenceDays') {
        result = api.getScheduledOccurrenceDays(payload.anchorDate, payload.frequency, payload.options || {})
            .map(formatLocalDate);
    } else {
        throw new Error('Unsupported function: ' + functionName);
    }

    return JSON.stringify(result);
}
""";

    private static string RepoRoot => ResolveRepoRoot();

    private static readonly string SourceFilePath = GetSourceFilePath();

    private static string ProjectionModulePath =>
        Path.Combine(RepoRoot, "SHARED", "wwwroot", "js", "expense-lens-projection.js");

    private static string GetSourceFilePath([CallerFilePath] string path = "") => path;

    private static string ResolveRepoRoot()
    {
        var candidates = new List<string>();
        var currentDirectory = Directory.GetCurrentDirectory();
        if (!string.IsNullOrWhiteSpace(currentDirectory))
        {
            candidates.Add(Path.GetFullPath(currentDirectory));
        }

        if (!string.IsNullOrWhiteSpace(SourceFilePath))
        {
            candidates.Add(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourceFilePath) ?? string.Empty, "..")));
        }

        var baseDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        while (baseDirectory != null)
        {
            candidates.Add(baseDirectory.FullName);
            baseDirectory = baseDirectory.Parent;
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var sharedFinanceModule = Path.Combine(candidate, "SHARED", "wwwroot", "js", "expense-lens-projection.js");
            if (File.Exists(sharedFinanceModule))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("Unable to locate the MASTERAPP repository root containing SHARED/wwwroot/js/expense-lens-projection.js.");
    }

    private static JsonDocument InvokeProjectionApi(string functionName, object payload)
    {
        var tempScriptPath = Path.Combine(Path.GetTempPath(), $"expense-lens-projection-{Guid.NewGuid():N}.js");
        File.WriteAllText(tempScriptPath, ProjectionBridgeScript);

        try
        {
            var psi = new ProcessStartInfo("/usr/bin/osascript")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("-l");
            psi.ArgumentList.Add("JavaScript");
            psi.ArgumentList.Add(tempScriptPath);
            psi.ArgumentList.Add(functionName);
            psi.ArgumentList.Add(ProjectionModulePath);
            psi.ArgumentList.Add(JsonSerializer.Serialize(payload));

            using var process = Process.Start(psi);
            Assert.NotNull(process);

            var stdout = process!.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(
                process.ExitCode == 0,
                $"/usr/bin/osascript failed for {functionName}.{Environment.NewLine}STDERR:{Environment.NewLine}{stderr}{Environment.NewLine}STDOUT:{Environment.NewLine}{stdout}");

            return JsonDocument.Parse(stdout.Trim());
        }
        finally
        {
            if (File.Exists(tempScriptPath))
            {
                File.Delete(tempScriptPath);
            }
        }
    }

    [Fact]
    public void ProjectExpenseLensTimeline_UsesOnlyLegitimateRemainingCashForExtraDebt()
    {
        using var result = InvokeProjectionApi("projectExpenseLensTimeline", new
        {
            selectedMonthKey = "2026-01",
            asOfDate = "2026-01-15",
            horizonMonths = 24,
            state = new
            {
                incomeStreams = new
                {
                    primary = new[]
                    {
                        new { id = "pay", amount = "4000", frequency = "monthly", anchorDate = "2026-01-01" }
                    },
                    secondary = Array.Empty<object>()
                },
                categories = new object[]
                {
                    new { id = "housing", name = "Housing", amount = "3000", due = "2026-01-02", frequency = "monthly", paymentMethod = "debit" },
                    new { id = "cc-min", name = "Debt Payment - Credit Cards", amount = "200", due = "2026-01-03", frequency = "monthly", paymentMethod = "debit", debtCategory = "tracked-unsecured-minimum" }
                },
                debt = new
                {
                    openingBalance = 5000,
                    asOfDate = "2026-01-01"
                },
                monthlyStartingBalanceOverrides = new Dictionary<string, object?>
                {
                    ["2026-01"] = new { amount = 500 }
                }
            }
        });

        var selectedMonth = result.RootElement.GetProperty("selectedMonth");
        Assert.Equal(20000, selectedMonth.GetProperty("requiredDebtMinimumCents").GetInt32());
        Assert.Equal(130000, selectedMonth.GetProperty("extraDebtPaymentsCents").GetInt32());
        Assert.Equal(0, selectedMonth.GetProperty("endingCashCents").GetInt32());
        Assert.Equal(350000, selectedMonth.GetProperty("endingDebtCents").GetInt32());
    }

    [Fact]
    public void ProjectExpenseLensTimeline_DoesNotInventExtraDebtWhenCashIsShort()
    {
        using var result = InvokeProjectionApi("projectExpenseLensTimeline", new
        {
            selectedMonthKey = "2026-01",
            asOfDate = "2026-01-20",
            horizonMonths = 24,
            state = new
            {
                incomeStreams = new
                {
                    primary = new[]
                    {
                        new { id = "pay", amount = "2000", frequency = "monthly", anchorDate = "2026-01-01" }
                    },
                    secondary = Array.Empty<object>()
                },
                categories = new object[]
                {
                    new { id = "bills", name = "Bills", amount = "2300", due = "2026-01-05", frequency = "monthly", paymentMethod = "debit" }
                },
                debt = new
                {
                    openingBalance = 5000,
                    asOfDate = "2026-01-01"
                },
                monthlyStartingBalanceOverrides = new Dictionary<string, object?>
                {
                    ["2026-01"] = new { amount = 0 }
                }
            }
        });

        var selectedMonth = result.RootElement.GetProperty("selectedMonth");
        var summary = result.RootElement.GetProperty("summary");

        Assert.Equal(0, selectedMonth.GetProperty("extraDebtPaymentsCents").GetInt32());
        Assert.Equal(-30000, selectedMonth.GetProperty("endingCashCents").GetInt32());
        Assert.Equal(500000, selectedMonth.GetProperty("endingDebtCents").GetInt32());
        Assert.Equal(720000, summary.GetProperty("maximumProjectedCashDeficitCents").GetInt32());
    }

    [Fact]
    public void ProjectExpenseLensTimeline_RespectsMonthlyStartingBalanceOverridesWithoutRewritingPriorMonth()
    {
        using var result = InvokeProjectionApi("projectExpenseLensTimeline", new
        {
            selectedMonthKey = "2026-02",
            asOfDate = "2026-02-10",
            horizonMonths = 24,
            state = new
            {
                incomeStreams = new
                {
                    primary = new[]
                    {
                        new { id = "pay", amount = "1000", frequency = "monthly", anchorDate = "2026-01-01" }
                    },
                    secondary = Array.Empty<object>()
                },
                categories = Array.Empty<object>(),
                debt = new
                {
                    openingBalance = 0,
                    asOfDate = "2026-01-01"
                },
                monthlyStartingBalanceOverrides = new Dictionary<string, object?>
                {
                    ["2026-02"] = new { amount = 250, note = "Bank reconciliation" }
                }
            }
        });

        var months = result.RootElement.GetProperty("months");
        var january = months[0];
        var february = result.RootElement.GetProperty("selectedMonth");

        Assert.Equal("2026-01", january.GetProperty("monthKey").GetString());
        Assert.Equal(100000, january.GetProperty("endingCashCents").GetInt32());
        Assert.Equal(25000, february.GetProperty("openingCashCents").GetInt32());
        Assert.Equal("manual-override", february.GetProperty("startingBalanceSource").GetString());
        Assert.Equal("Bank reconciliation", february.GetProperty("override").GetProperty("note").GetString());
    }

    [Fact]
    public void ProjectExpenseLensTimeline_StopsExtraDebtAtZeroAndLeavesRemainingCash()
    {
        using var result = InvokeProjectionApi("projectExpenseLensTimeline", new
        {
            selectedMonthKey = "2026-01",
            asOfDate = "2026-01-10",
            horizonMonths = 24,
            state = new
            {
                incomeStreams = new
                {
                    primary = Array.Empty<object>(),
                    secondary = Array.Empty<object>()
                },
                categories = Array.Empty<object>(),
                debt = new
                {
                    openingBalance = 300,
                    asOfDate = "2026-01-01"
                },
                monthlyStartingBalanceOverrides = new Dictionary<string, object?>
                {
                    ["2026-01"] = new { amount = 800 }
                }
            }
        });

        var selectedMonth = result.RootElement.GetProperty("selectedMonth");
        Assert.Equal(30000, selectedMonth.GetProperty("extraDebtPaymentsCents").GetInt32());
        Assert.Equal(0, selectedMonth.GetProperty("endingDebtCents").GetInt32());
        Assert.Equal(50000, selectedMonth.GetProperty("endingCashCents").GetInt32());
    }

    [Fact]
    public void ProjectExpenseLensTimeline_GroupsCreditBillsOntoConfiguredMonthlyPaymentDay()
    {
        using var result = InvokeProjectionApi("projectExpenseLensTimeline", new
        {
            selectedMonthKey = "2026-08",
            asOfDate = "2026-08-01",
            horizonMonths = 24,
            state = new
            {
                incomeStreams = new
                {
                    primary = new[]
                    {
                        new { id = "pay", amount = "4000", frequency = "monthly", anchorDate = "2026-08-01" }
                    },
                    secondary = Array.Empty<object>()
                },
                categories = new object[]
                {
                    new { id = "rent", name = "Rent", amount = "1000", due = "2026-08-02", frequency = "monthly", paymentMethod = "debit" },
                    new { id = "groceries", name = "Groceries", amount = "200", due = "2026-08-03", frequency = "monthly", paymentMethod = "credit" },
                    new { id = "utilities", name = "Utilities", amount = "300", due = "2026-08-19", frequency = "monthly", paymentMethod = "credit" }
                },
                projectionSettings = new
                {
                    creditPaymentDayOfMonth = 25
                },
                debt = new
                {
                    openingBalance = 0,
                    asOfDate = "2026-08-01"
                },
                monthlyStartingBalanceOverrides = new Dictionary<string, object?>
                {
                    ["2026-08"] = new { amount = 0 }
                }
            }
        });

        var selectedMonth = result.RootElement.GetProperty("selectedMonth");
        var weeks = selectedMonth.GetProperty("weeks").EnumerateArray().ToList();

        var creditEvents = weeks
            .SelectMany(week => week.GetProperty("events").EnumerateArray())
            .Where(eventItem =>
                eventItem.GetProperty("kind").GetString() == "expense"
                && eventItem.GetProperty("paymentMethod").GetString() == "credit")
            .ToList();

        Assert.Equal(2, creditEvents.Count);
        Assert.All(creditEvents, eventItem => Assert.Equal("2026-08-25", eventItem.GetProperty("dateKey").GetString()));

        var paymentWeek = weeks.Single(week =>
            week.GetProperty("events").EnumerateArray().Any(eventItem =>
                eventItem.GetProperty("dateKey").GetString() == "2026-08-25"));

        Assert.Equal(50000, paymentWeek.GetProperty("creditBillsCents").GetInt32());
        Assert.Equal(
            weeks.Count - 1,
            weeks.Count(week => week.GetProperty("creditBillsCents").GetInt32() == 0));
    }

    [Fact]
    public void ProjectExpenseLensTimeline_PreservesLegacyCompletedCreditHistoryWhenPaymentDayGroupingChangesKeys()
    {
        using var result = InvokeProjectionApi("projectExpenseLensTimeline", new
        {
            selectedMonthKey = "2026-08",
            asOfDate = "2026-08-10",
            horizonMonths = 24,
            state = new
            {
                incomeStreams = new
                {
                    primary = Array.Empty<object>(),
                    secondary = Array.Empty<object>()
                },
                categories = new object[]
                {
                    new { id = "card-bill", name = "Card Bill", amount = "120", due = "2026-08-03", frequency = "monthly", paymentMethod = "credit" }
                },
                projectionSettings = new
                {
                    creditPaymentDayOfMonth = 25
                },
                occurrenceHistory = new
                {
                    expenses = new Dictionary<string, object?>
                    {
                        ["expense:card-bill:2026-08-03"] = new
                        {
                            status = "completed",
                            dateKey = "2026-08-03",
                            actualAmountCents = 12000,
                            paymentMethod = "credit",
                            frequency = "monthly",
                            label = "Card Bill",
                            sourceType = "expense",
                            sourceId = "card-bill"
                        }
                    }
                },
                debt = new
                {
                    openingBalance = 0,
                    asOfDate = "2026-08-01"
                }
            }
        });

        var expenseEvents = result.RootElement
            .GetProperty("selectedMonth")
            .GetProperty("weeks")
            .EnumerateArray()
            .SelectMany(week => week.GetProperty("events").EnumerateArray())
            .Where(eventItem => eventItem.GetProperty("kind").GetString() == "expense")
            .ToList();

        Assert.Single(expenseEvents);
        Assert.Equal("2026-08-25", expenseEvents[0].GetProperty("dateKey").GetString());
        Assert.Equal("actual", expenseEvents[0].GetProperty("status").GetString());
    }

    [Fact]
    public void ProjectExpenseLensTimeline_PlacesTrackedDebtMinimumInFinalWeekAfterBills()
    {
        using var result = InvokeProjectionApi("projectExpenseLensTimeline", new
        {
            selectedMonthKey = "2026-08",
            asOfDate = "2026-08-01",
            horizonMonths = 24,
            state = new
            {
                incomeStreams = new
                {
                    primary = new[]
                    {
                        new { id = "pay", amount = "3000", frequency = "monthly", anchorDate = "2026-08-01" }
                    },
                    secondary = Array.Empty<object>()
                },
                categories = new object[]
                {
                    new { id = "insurance", name = "Insurance", amount = "500", due = "2026-08-15", frequency = "monthly", paymentMethod = "debit" },
                    new { id = "cc-min", name = "Debt Payment - Credit Cards", amount = "200", due = "2026-08-03", frequency = "monthly", paymentMethod = "debit", debtCategory = "tracked-unsecured-minimum" }
                },
                debt = new
                {
                    openingBalance = 1000,
                    asOfDate = "2026-08-01"
                },
                monthlyStartingBalanceOverrides = new Dictionary<string, object?>
                {
                    ["2026-08"] = new { amount = 0 }
                }
            }
        });

        var selectedMonth = result.RootElement.GetProperty("selectedMonth");
        var weeks = selectedMonth.GetProperty("weeks").EnumerateArray().ToList();
        var finalWeek = weeks.Last();

        var debtMinimumWeeks = weeks
            .Where(week => week.GetProperty("events").EnumerateArray().Any(eventItem =>
                eventItem.GetProperty("kind").GetString() == "expense"
                && eventItem.TryGetProperty("debtCategory", out var debtCategory)
                && debtCategory.GetString() == "tracked-unsecured-minimum"))
            .ToList();

        Assert.Single(debtMinimumWeeks);
        Assert.Equal(finalWeek.GetProperty("id").GetString(), debtMinimumWeeks[0].GetProperty("id").GetString());

        var finalWeekEvents = finalWeek.GetProperty("events").EnumerateArray().ToList();
        Assert.True(finalWeekEvents.Count >= 2);
        Assert.Equal("tracked-unsecured-minimum", finalWeekEvents[^2].GetProperty("debtCategory").GetString());
        Assert.Equal("extraDebt", finalWeekEvents[^1].GetProperty("kind").GetString());
    }

    [Fact]
    public void GetScheduledOccurrenceDays_PreservesFourteenDayBiWeeklyAnchorCadence()
    {
        using var july = InvokeProjectionApi("getScheduledOccurrenceDays", new
        {
            anchorDate = "2026-07-17",
            frequency = "biweekly",
            options = new { monthKey = "2026-07" }
        });
        using var august = InvokeProjectionApi("getScheduledOccurrenceDays", new
        {
            anchorDate = "2026-07-17",
            frequency = "biweekly",
            options = new { monthKey = "2026-08" }
        });

        var julyDates = july.RootElement.EnumerateArray().Select(item => item.GetString()).ToArray();
        var augustDates = august.RootElement.EnumerateArray().Select(item => item.GetString()).ToArray();

        Assert.Equal(new[] { "2026-07-17", "2026-07-31" }, julyDates);
        Assert.Equal(new[] { "2026-08-14", "2026-08-28" }, augustDates);
        Assert.DoesNotContain("2026-08-03", julyDates.Concat(augustDates));
    }

    [Fact]
    public void NormalizeState_MigratesLegacyExpenseLensPayloadWithoutDroppingExistingData()
    {
        using var result = InvokeProjectionApi("normalizeState", new
        {
            income = "4000",
            expenses = new object[]
            {
                new { name = "Rent", occurrenceAmount = "1500", due = "2026-01-05", frequency = "monthly", paymentMethod = "debit" }
            }
        });

        var normalized = result.RootElement;
        Assert.Equal(2, normalized.GetProperty("stateVersion").GetInt32());
        Assert.Equal(1, normalized.GetProperty("categories").GetArrayLength());
        Assert.Equal(0, normalized.GetProperty("debt").GetProperty("openingBalanceCents").GetInt32());
        Assert.True(normalized.GetProperty("monthlyStartingBalanceOverrides").EnumerateObject().Count() == 0);
        Assert.True(normalized.GetProperty("occurrenceHistory").GetProperty("incomes").EnumerateObject().Count() == 0);
        Assert.True(normalized.GetProperty("occurrenceHistory").GetProperty("expenses").EnumerateObject().Count() == 0);
    }
}
