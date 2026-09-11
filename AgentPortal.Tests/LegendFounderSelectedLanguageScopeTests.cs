using System;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendFounderSelectedLanguageScopeTests
{
    [Theory]
    [InlineData("en", "en")]
    [InlineData("EN", "en")]
    [InlineData(" en ", "en")]
    [InlineData("en-us", "en-US")]
    [InlineData(" EN-US ", "en-US")]
    public async Task ExplicitEnabledSelection_PreservesItsCanonicalPartition(string requested, string expected)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        ControllerTestHelpers.SeedGovernedLanguageBaseline(db, "en", "en-US");

        var page = await Operations(db).GetFounderSectionPageAsync(
            "machine-learning-lifecycle", requested, null, null);

        Assert.Equal(expected, page.LanguageCode);
    }

    [Theory]
    [InlineData("zz")]
    [InlineData("ht")]
    [InlineData("en_US")]
    public async Task ExplicitUnavailableSelection_CannotReturnDefaultLanguageEvidence(string requested)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        ControllerTestHelpers.SeedGovernedLanguageBaseline(db, "en", "en-US", "ht");
        db.Set<LegendLanguageDefinition>().Single(item => item.LanguageCode == "ht").IsEnabled = false;
        await db.SaveChangesAsync();
        var operations = Operations(db);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            operations.GetFounderSectionPageAsync("machine-learning-lifecycle", requested, null, null));

        Assert.Equal("languageCode", exception.ParamName);
        var shell = await operations.GetFounderShellAsync(requested);
        Assert.Null(shell.SelectedLanguage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task InitialViewWithoutSelection_PreservesEnglishDefault(string? requested)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        ControllerTestHelpers.SeedGovernedLanguageBaseline(db, "ht", "en");

        var page = await Operations(db).GetFounderSectionPageAsync(
            "machine-learning-lifecycle", requested, null, null);

        Assert.Equal("en", page.LanguageCode);
    }

    [Fact]
    public async Task InitialViewWithoutEnglish_PreservesFirstEnabledDefault()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        ControllerTestHelpers.SeedGovernedLanguageBaseline(db, "ht");

        var page = await Operations(db).GetFounderSectionPageAsync(
            "machine-learning-lifecycle", null, null, null);

        Assert.Equal("ht", page.LanguageCode);
    }

    private static LegendConnectOperations Operations(MasterAppDbContext db)
    {
        var configuration = new ConfigurationBuilder().Build();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(db, registry,
            NullLogger<LegendConnectCorpusService>.Instance);
        return new LegendConnectOperations(db, registry, corpus, configuration);
    }
}
