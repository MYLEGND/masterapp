using System;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectRetainedKnowledgeScaleTests
{
    [Fact]
    public async Task RetainedKnowledgeSearch_FindsOlderFounderApprovedKnowledgeBeyondTheRecentFourHundredWindow()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var registry = new LegendLanguageRegistry(db, configuration);
        var operations = new LegendConnectOperations(
            db,
            registry,
            new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance),
            configuration);

        var older = new LegendLanguageTextUnit
        {
            Id = Guid.NewGuid(),
            LanguageCode = "en",
            StoragePartition = "/en",
            NormalizedHash = "retained-scale-older",
            Text = "The sapphire lantern marks the founder-approved retrieval distinction.",
            Provenance = "FounderApproved",
            IsTrainingEligible = true,
            CreatedUtc = DateTime.UtcNow.AddDays(-10),
            UpdatedUtc = DateTime.UtcNow.AddDays(-10)
        };
        db.LegendLanguageTextUnits.Add(older);

        var now = DateTime.UtcNow;
        for (var i = 0; i < 425; i++)
        {
            db.LegendLanguageTextUnits.Add(new LegendLanguageTextUnit
            {
                Id = Guid.NewGuid(),
                LanguageCode = "en",
                StoragePartition = "/en",
                NormalizedHash = $"retained-scale-newer-{i:D3}",
                Text = $"Newer unrelated retained knowledge row {i:D3}.",
                Provenance = "FounderApproved",
                IsTrainingEligible = true,
                CreatedUtc = now.AddMinutes(i),
                UpdatedUtc = now.AddMinutes(i)
            });
        }

        await db.SaveChangesAsync();

        var snapshot = await operations.SearchRetainedKnowledgeAsync(
            "sapphire lantern retrieval distinction",
            sourceLanguageCode: "en",
            take: 12);

        Assert.Contains(snapshot.Items, item =>
            item.Kind == "CanonicalText" &&
            item.AuthorityState == "FounderApproved" &&
            item.Content == older.Text);
    }
}
