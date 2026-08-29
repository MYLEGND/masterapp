using System;
using System.Linq;
using AgentPortal.Services;
using Domain.Entities;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendBlindComparativeBenchmarkTests
{
    private const string Baseline =
        "gpt-5.6-sol@locked-2026-08-29";
    private const string Candidate =
        "0123456789abcdef0123456789abcdef01234567";
    private const string Manifest =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
    private static readonly DateTime MeasuredUtc =
        new(2026, 8, 29, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CaseLevelBlindResults_MustPassStatisticalGateAcrossEveryDomain()
    {
        var evaluation =
            LegendBlindComparativeBenchmarkEvaluator.Evaluate(
                Report(legendWinsPerDomain: 60));

        Assert.True(evaluation.Valid);
        Assert.NotNull(evaluation.SuiteIdentity);
        Assert.Empty(evaluation.Blockers);
        Assert.All(evaluation.Metrics.Values, metrics =>
        {
            Assert.Equal(100m, metrics["sample_size"]);
            Assert.Equal(60m, metrics["blind_win_rate"]);
            Assert.True(
                metrics["blind_win_rate_lower_confidence_bound"] > 50m);
            Assert.Equal(100m, metrics["non_inferiority_rate"]);
            Assert.Equal(100m, metrics["prompt_holdout_integrity"]);
            Assert.Equal(100m, metrics["assignment_blinding_integrity"]);
        });

        var signals =
            evaluation.BuildSignals(
                Guid.NewGuid(),
                Baseline,
                MeasuredUtc);
        var readiness =
            LegendArchitecturalTakeoverGate.Evaluate(
                LegendIntelligenceEvaluationDomainCatalog.All,
                signals);

        Assert.True(readiness.Proven);
        Assert.Equal("PROVEN", readiness.State);
        Assert.Equal(
            LegendIntelligenceEvaluationDomainCatalog.All.Count,
            readiness.DomainWins);
    }

    [Fact]
    public void RawWinRateWithoutPassingConfidenceBound_RemainsBlocked()
    {
        var evaluation =
            LegendBlindComparativeBenchmarkEvaluator.Evaluate(
                Report(legendWinsPerDomain: 59));

        Assert.True(evaluation.Valid);
        Assert.All(evaluation.Metrics.Values, metrics =>
            Assert.True(
                metrics["blind_win_rate_lower_confidence_bound"] <= 50m));

        var readiness =
            LegendArchitecturalTakeoverGate.Evaluate(
                LegendIntelligenceEvaluationDomainCatalog.All,
                evaluation.BuildSignals(
                    Guid.NewGuid(),
                    Baseline,
                    MeasuredUtc));

        Assert.False(readiness.Proven);
        Assert.Equal("BLOCKED", readiness.State);
        Assert.Contains(
            readiness.Blockers,
            item => item.Contains(
                "blind_win_rate_lower_confidence_bound",
                StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateCaseIdentity_CannotProduceTakeoverSignals()
    {
        var report = Report(legendWinsPerDomain: 60);
        var duplicate = report with
        {
            Cases = report.Cases
                .Append(report.Cases[0])
                .ToArray()
        };

        var evaluation =
            LegendBlindComparativeBenchmarkEvaluator.Evaluate(duplicate);

        Assert.False(evaluation.Valid);
        Assert.Null(evaluation.SuiteIdentity);
        Assert.Empty(
            evaluation.BuildSignals(
                Guid.NewGuid(),
                Baseline,
                MeasuredUtc));
        Assert.Contains(
            evaluation.Blockers,
            item => item.Contains(
                "duplicate case",
                StringComparison.OrdinalIgnoreCase));
    }

    private static LegendBlindBenchmarkReport Report(
        int legendWinsPerDomain)
    {
        var cases =
            LegendIntelligenceEvaluationDomainCatalog.All
                .SelectMany(domain =>
                    Enumerable.Range(0, 100)
                        .Select(index =>
                            new LegendBlindBenchmarkCaseResult(
                                domain.Key,
                                $"{domain.Key}-case-{index:D3}",
                                index < legendWinsPerDomain
                                    ? "LEGEND"
                                    : "TIE",
                                NonInferior: true,
                                AdversarialPassed: true,
                                UnsupportedRequestIntegrity: true,
                                PromptHeldOut: true,
                                AssignmentBlinded: true,
                                LegendLatencyMicroseconds: 1_000,
                                BaselineLatencyMicroseconds: 2_000,
                                LegendCostMicrounits: 1,
                                BaselineCostMicrounits: 2,
                                AgreedJudgeVotes: 3,
                                TotalJudgeVotes: 3)))
                .ToArray();

        return new(
            Baseline,
            Candidate,
            Manifest,
            MeasuredUtc,
            cases);
    }
}
