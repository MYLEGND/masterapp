using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MobileExpenseLensMonthProjectionBridgeTests
{
    private static string ResolveRepositoryRoot(
        [CallerFilePath] string sourcePath = "")
    {
        var testDirectory =
            new DirectoryInfo(Path.GetDirectoryName(sourcePath)!);

        var repositoryRoot = testDirectory.Parent;

        if (repositoryRoot is null)
        {
            throw new DirectoryNotFoundException(
                "Could not resolve the MASTERAPP repository root.");
        }

        var projectionPath = Path.Combine(
            repositoryRoot.FullName,
            "SHARED",
            "wwwroot",
            "js",
            "expense-lens-projection.js");

        if (!File.Exists(projectionPath))
        {
            throw new FileNotFoundException(
                "Expense Lens projection module was not found.",
                projectionPath);
        }

        return repositoryRoot.FullName;
    }

    [Fact]
    public void BuildMobileMonthSnapshot_UsesAuthoritativeMonthAndWeekRows()
    {
        var output = InvokeNode(
            """
            const projectionApi = require(process.argv[2]);

            const month = {
                monthKey: "2026-07",
                label: "July 2026",
                temporalStatus: "current",
                status: "current",
                openingCashCents: 125000,
                openingDebtCents: 500000,
                scheduledIncomeCents: 450000,
                requiredExpensesCents: 195000,
                requiredDebtMinimumCents: 35000,
                extraDebtPaymentsCents: 25000,
                endingCashCents: 220000,
                endingDebtCents: 440000,
                warnings: [],
                weeks: [
                    {
                        id: "2026-07-week-1",
                        label: "Week 1",
                        startDate: new Date(2026, 5, 29),
                        endDate: new Date(2026, 6, 5),
                        status: "historical-reconciled",
                        incomeCents: 200000,
                        debitBillsCents: 80000,
                        creditBillsCents: 20000,
                        requiredDebtMinimumCents: 20000,
                        extraDebtPaymentCents: 10000,
                        closingCashCents: 210000,
                        closingDebtCents: 470000,
                        events: [
                            {
                                key: "expense:rent:2026-07-01",
                                kind: "expense",
                                label: "Rent",
                                dateKey: "2026-07-01",
                                amountCents: 75000,
                                impactCashCents: -75000
                            }
                        ]
                    },
                    {
                        id: "2026-07-week-2",
                        label: "Week 2",
                        startDate: new Date(2026, 6, 6),
                        endDate: new Date(2026, 6, 12),
                        status: "current",
                        incomeCents: 250000,
                        debitBillsCents: 50000,
                        creditBillsCents: 15000,
                        requiredDebtMinimumCents: 15000,
                        extraDebtPaymentCents: 15000,
                        closingCashCents: 220000,
                        closingDebtCents: 440000,
                        events: [
                            {
                                key: "expense:auto:2026-07-10",
                                kind: "expense",
                                label: "Auto Insurance",
                                dateKey: "2026-07-10",
                                amountCents: 19200,
                                impactCashCents: -19200
                            }
                        ]
                    }
                ]
            };

            const snapshot = projectionApi.buildMobileMonthSnapshot(
                {
                    stateVersion: 2,
                    months: [month]
                },
                new Date(2026, 6, 10)
            );

            process.stdout.write(JSON.stringify(snapshot));
            """);

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;

        Assert.Equal(
            1,
            root.GetProperty("schemaVersion").GetInt32());

        Assert.Equal(
            "2026-07",
            root.GetProperty("monthKey").GetString());

        Assert.Equal(
            "2026-07-01",
            root.GetProperty("startDate").GetString());

        Assert.Equal(
            "2026-07-31",
            root.GetProperty("endDate").GetString());

        Assert.Equal(
            125000,
            root.GetProperty("openingCashCents").GetInt32());

        Assert.Equal(
            450000,
            root.GetProperty("incomeCents").GetInt32());

        Assert.Equal(
            130000,
            root.GetProperty("debitBillsCents").GetInt32());

        Assert.Equal(
            35000,
            root.GetProperty("creditBillsCents").GetInt32());

        Assert.Equal(
            35000,
            root.GetProperty(
                "requiredDebtMinimumCents").GetInt32());

        Assert.Equal(
            25000,
            root.GetProperty(
                "extraDebtPaymentCents").GetInt32());

        Assert.Equal(
            220000,
            root.GetProperty("endingCashCents").GetInt32());

        Assert.Equal(
            440000,
            root.GetProperty("endingDebtCents").GetInt32());

        Assert.Equal(
            JsonValueKind.Null,
            root.GetProperty(
                "savingsContributionCents").ValueKind);

        Assert.Equal(
            "not-projected-by-expense-lens",
            root.GetProperty(
                "savingsProjectionStatus").GetString());

        var largest =
            root.GetProperty("largestObligation");

        Assert.Equal(
            "Rent",
            largest.GetProperty("title").GetString());

        Assert.Equal(
            75000,
            largest.GetProperty("amountCents").GetInt32());

        Assert.Equal(
            2,
            root.GetProperty("weeks").GetArrayLength());

        var firstWeek =
            root.GetProperty("weeks")[0];

        Assert.Equal(
            110000,
            firstWeek.GetProperty("outflowCents").GetInt32());
    }

    [Fact]
    public void BuildMobileMonthSnapshot_ReturnsNullWhenCurrentMonthIsMissing()
    {
        var output = InvokeNode(
            """
            const projectionApi = require(process.argv[2]);

            const snapshot = projectionApi.buildMobileMonthSnapshot(
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
                new Date(2026, 6, 10)
            );

            process.stdout.write(JSON.stringify(snapshot));
            """);

        Assert.Equal("null", output);
    }

    private static string InvokeNode(
        string runnerBody)
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
            $"legend-mobile-month-{Guid.NewGuid():N}.js");

        File.WriteAllText(
            runnerPath,
            runnerBody);

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

            var output =
                process.StandardOutput.ReadToEnd();

            var error =
                process.StandardError.ReadToEnd();

            process.WaitForExit();

            Assert.True(
                process.ExitCode == 0,
                $"Node exited with {process.ExitCode}: {error}");

            return output;
        }
        finally
        {
            File.Delete(runnerPath);
        }
    }
}
