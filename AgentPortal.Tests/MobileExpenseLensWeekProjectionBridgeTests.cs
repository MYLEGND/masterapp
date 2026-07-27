using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MobileExpenseLensWeekProjectionBridgeTests
{
    private static string ResolveRepositoryRoot(
        [CallerFilePath] string sourceFilePath = "")
    {
        var testProjectDirectory = Directory.GetParent(sourceFilePath)
            ?? throw new DirectoryNotFoundException(
                $"Could not resolve the test project directory from: {sourceFilePath}");

        var repositoryRoot = testProjectDirectory.Parent?.FullName
            ?? throw new DirectoryNotFoundException(
                $"Could not resolve the repository root from: {sourceFilePath}");

        var projectionPath = Path.Combine(
            repositoryRoot,
            "SHARED",
            "wwwroot",
            "js",
            "expense-lens-projection.js");

        if (!File.Exists(projectionPath))
        {
            throw new FileNotFoundException(
                "Expected Expense Lens projection file was not found.",
                projectionPath);
        }

        return repositoryRoot;
    }

    [Fact]
    public void BuildMobileWeekSnapshot_UsesAuthoritativeProjectionWeek()
    {
        var repositoryRoot = ResolveRepositoryRoot();
        var modulePath = Path.Combine(
            repositoryRoot,
            "SHARED",
            "wwwroot",
            "js",
            "expense-lens-projection.js");

        var runnerPath = Path.Combine(
            Path.GetTempPath(),
            $"legend-mobile-week-projection-{Guid.NewGuid():N}.js");

        var runner = """
const projectionApi = require(process.argv[2]);

const week = {
    id: "2026-07-week-4",
    label: "Week 4",
    startDate: new Date(2026, 6, 22),
    endDate: new Date(2026, 6, 28),
    status: "current",
    openingCashCents: 125000,
    incomeCents: 250000,
    debitBillsCents: 80000,
    creditBillsCents: 35000,
    requiredExpensesCents: 115000,
    requiredDebtMinimumCents: 20000,
    extraDebtPaymentCents: 15000,
    closingCashCents: 100000,
    openingDebtCents: 500000,
    closingDebtCents: 465000,
    events: [
        {
            key: "income:paycheck:2026-07-24",
            kind: "income",
            label: "Paycheck",
            dateKey: "2026-07-24",
            status: "projected",
            amountCents: 250000,
            impactCashCents: 250000,
            cashAfterCents: 375000,
            debtAfterCents: 500000
        }
    ]
};

const projection = {
    stateVersion: 2,
    months: [
        {
            monthKey: "2026-07",
            label: "July 2026",
            weeks: [week]
        }
    ]
};

const snapshot = projectionApi.buildMobileWeekSnapshot(
    projection,
    new Date(2026, 6, 26)
);

process.stdout.write(JSON.stringify(snapshot));
""";

        File.WriteAllText(runnerPath, runner);

        try
        {
            var processStart = new ProcessStartInfo
            {
                FileName = "node",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            processStart.ArgumentList.Add(runnerPath);
            processStart.ArgumentList.Add(modulePath);

            using var process = Process.Start(processStart)
                ?? throw new InvalidOperationException(
                    "Node process could not be started.");

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();

            process.WaitForExit();

            Assert.True(
                process.ExitCode == 0,
                $"Node exited with {process.ExitCode}: {error}");

            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;

            Assert.Equal(
                "2026-07-week-4",
                root.GetProperty("weekId").GetString());

            Assert.Equal(
                125000,
                root.GetProperty("openingCashCents").GetInt32());

            Assert.Equal(
                250000,
                root.GetProperty("incomeCents").GetInt32());

            Assert.Equal(
                100000,
                root.GetProperty("closingCashCents").GetInt32());

            Assert.Equal(
                465000,
                root.GetProperty("closingDebtCents").GetInt32());

            Assert.Single(
                root.GetProperty("events").EnumerateArray());
        }
        finally
        {
            File.Delete(runnerPath);
        }
    }

    [Fact]
    public void BuildMobileWeekSnapshot_ReturnsNullWhenCurrentWeekIsMissing()
    {
        var repositoryRoot = ResolveRepositoryRoot();
        var modulePath = Path.Combine(
            repositoryRoot,
            "SHARED",
            "wwwroot",
            "js",
            "expense-lens-projection.js");

        var runnerPath = Path.Combine(
            Path.GetTempPath(),
            $"legend-mobile-week-missing-{Guid.NewGuid():N}.js");

        var runner = """
const projectionApi = require(process.argv[2]);

const snapshot = projectionApi.buildMobileWeekSnapshot(
    {
        stateVersion: 2,
        months: [
            {
                monthKey: "2026-06",
                label: "June 2026",
                weeks: []
            }
        ]
    },
    new Date(2026, 6, 26)
);

process.stdout.write(JSON.stringify(snapshot));
""";

        File.WriteAllText(runnerPath, runner);

        try
        {
            var processStart = new ProcessStartInfo
            {
                FileName = "node",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            processStart.ArgumentList.Add(runnerPath);
            processStart.ArgumentList.Add(modulePath);

            using var process = Process.Start(processStart)
                ?? throw new InvalidOperationException(
                    "Node process could not be started.");

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();

            process.WaitForExit();

            Assert.True(
                process.ExitCode == 0,
                $"Node exited with {process.ExitCode}: {error}");

            Assert.Equal("null", output);
        }
        finally
        {
            File.Delete(runnerPath);
        }
    }
}
