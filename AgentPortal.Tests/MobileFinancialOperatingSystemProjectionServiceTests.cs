using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Infrastructure.Mobile;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MobileFinancialOperatingSystemProjectionServiceTests
{
    [Fact]
    public async Task ProjectAsync_ReturnsTruthfulUnavailableSkeleton()
    {
        var service =
            new MobileFinancialOperatingSystemProjectionService();

        var beforeUtc = DateTime.UtcNow;

        var snapshot = await service.ProjectAsync(Guid.NewGuid());

        var afterUtc = DateTime.UtcNow;

        Assert.Equal("Unavailable", snapshot.Projection.Status);
        Assert.Equal(
            "FINANCIAL_PROJECTION_NOT_POPULATED",
            snapshot.Projection.ReasonCode);

        Assert.NotNull(snapshot.Projection.Summary);
        Assert.Null(snapshot.WeekAtGlance);
        Assert.Null(snapshot.MonthAtGlance);
        Assert.Empty(snapshot.Tools);

        Assert.Null(snapshot.Freshness.FinanceStateUpdatedUtc);
        Assert.Null(snapshot.Freshness.IntelligenceEvaluatedUtc);

        Assert.InRange(
            snapshot.Freshness.GeneratedUtc,
            beforeUtc,
            afterUtc);
    }

    [Fact]
    public async Task ProjectAsync_RejectsEmptyClientProfileIdentifier()
    {
        var service =
            new MobileFinancialOperatingSystemProjectionService();

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => service.ProjectAsync(Guid.Empty));

        Assert.Equal("clientProfileId", exception.ParamName);
    }

    [Fact]
    public async Task ProjectAsync_ObservesCancellation()
    {
        var service =
            new MobileFinancialOperatingSystemProjectionService();

        using var cancellationSource =
            new CancellationTokenSource();

        cancellationSource.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.ProjectAsync(
                Guid.NewGuid(),
                cancellationSource.Token));
    }

    [Fact]
    public void ProjectionService_ImplementsExpectedBoundary()
    {
        Assert.True(
            typeof(IMobileFinancialOperatingSystemProjectionService)
                .IsAssignableFrom(
                    typeof(
                        MobileFinancialOperatingSystemProjectionService)));

        var methods =
            typeof(IMobileFinancialOperatingSystemProjectionService)
                .GetMethods()
                .Select(method => method.Name)
                .ToArray();

        Assert.Single(methods);
        Assert.Equal("ProjectAsync", methods[0]);
    }
}
