using System.Threading.Tasks;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LanguageRegistryRequestScopeTests
{
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
