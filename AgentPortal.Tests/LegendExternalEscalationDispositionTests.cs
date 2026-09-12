using System;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendExternalEscalationDispositionTests
{
    [Theory]
    [InlineData(true, "Restricted")]
    [InlineData(false, "InsufficientEvidence")]
    public async Task ExternalAssistanceReceivesDispositionWithoutBecomingTrainingData(
        bool answered, string disposition)
    {
        await using var db = CreateDatabase();
        var writer = new LegendConnectOperationalEventWriter(db,
            NullLogger<LegendConnectOperationalEventWriter>.Instance);
        var identity = Guid.NewGuid();

        Assert.Equal(disposition, await writer.RecordEscalationDispositionAsync(identity, answered));
        Assert.Equal(disposition, await writer.RecordEscalationDispositionAsync(identity, answered));
        var entry = Assert.Single(await db.Set<LegendConnectOperationalEvent>().ToListAsync());
        Assert.Equal(identity.ToString("N"), entry.CorrelationId);
        Assert.Equal("ExternalEscalationLearning", entry.Category);
        Assert.Contains("content_retained=false", entry.Summary);
        Assert.Empty(await db.Set<LegendCorpusCandidate>().ToListAsync());
        Assert.Empty(await db.Set<LegendLanguageTeacherProposal>().ToListAsync());
        Assert.Empty(await db.Set<LegendLanguageTextUnit>().ToListAsync());
    }

    [Fact]
    public async Task CancelledRetentionDoesNotReportRecordedDisposition()
    {
        await using var db = CreateDatabase();
        var writer = new LegendConnectOperationalEventWriter(db,
            NullLogger<LegendConnectOperationalEventWriter>.Instance);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            writer.RecordEscalationDispositionAsync(Guid.NewGuid(), true, cancellation.Token));
        Assert.Empty(await db.Set<LegendConnectOperationalEvent>().ToListAsync());
    }

    private static MasterAppDbContext CreateDatabase() => new(
        new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}
