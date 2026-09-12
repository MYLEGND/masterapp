using System.Threading.Tasks;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LanguageRegistryRequestScopeTests
{
    [Fact]
    public async Task PairReadPreservesAliasesAndImmediatelyReflectsLanguageAndPairEligibility()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var registry = new LegendLanguageRegistry(db, new ConfigurationBuilder().Build());
        var pair = await registry.GetOrCreateEnabledPairAsync("en", "ht");
        Assert.NotNull(pair);
        var source = await registry.GetLanguageAsync("en");
        var target = await registry.GetLanguageAsync("ht");
        Assert.Equal(pair, await registry.GetEnabledPairAsync(source!.DisplayName, target!.NativeName));
        Assert.Equal(pair, await registry.GetEnabledPairAsync("en-US", "ht"));
        Assert.Null(await registry.GetEnabledPairAsync("en", "en"));
        Assert.Null(await registry.GetEnabledPairAsync("missing-language", "ht"));
        var row = await db.Set<Domain.Entities.LegendLanguagePair>().SingleAsync(item => item.PairKey == pair!.PairKey);
        row.IsEnabled = false;
        await db.SaveChangesAsync();
        Assert.Null(await registry.GetEnabledPairAsync("en", "ht"));
        row.IsEnabled = true;
        var definition = await db.Set<Domain.Entities.LegendLanguageDefinition>().SingleAsync(item => item.LanguageCode == "ht");
        definition.IsTranslationEnabled = false;
        await db.SaveChangesAsync();
        Assert.Null(await registry.GetEnabledPairAsync("en", "ht"));
        definition.IsTranslationEnabled = true;
        await db.SaveChangesAsync();
        Assert.NotNull(await registry.GetEnabledPairAsync("en", "ht"));
    }

    [Fact]
    public async Task ReusingRegistryStillReadsCurrentLanguageEligibility()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var registry = new LegendLanguageRegistry(db, new ConfigurationBuilder().Build());
        Assert.NotNull(await registry.GetLanguageAsync("en"));
        var language = await db.Set<Domain.Entities.LegendLanguageDefinition>().SingleAsync(x => x.LanguageCode == "en");
        language.IsTranslationEnabled = false;
        await db.SaveChangesAsync();
        Assert.Null(await registry.GetLanguageAsync("en"));
        language.IsTranslationEnabled = true;
        await db.SaveChangesAsync();
        Assert.NotNull(await registry.GetLanguageAsync("en"));
    }
}
