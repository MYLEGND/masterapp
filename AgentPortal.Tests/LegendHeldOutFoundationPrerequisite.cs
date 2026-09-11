using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

/// <summary>
/// Test-only prerequisite composed from teaching already present at 0ec4ea35deb33e3f918f346c52b48f4721c40cfc.
/// No production Founder approval or deployment is implied. Neither requests nor expected
/// answers enter the teaching helpers; held-out inputs are used solely for contamination checks.
/// </summary>
internal static class LegendHeldOutFoundationPrerequisite
{
    internal static async Task AdmitAsync(MasterAppDbContext db, IReadOnlyList<string> heldOutInputs)
    {
        var configuration = new ConfigurationBuilder().Build();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        await LegendConnectComputedLanguageEndToEndContractTests.SeedTeachingAsync(
            curriculum, identityNamespace: "foundation.numeric.");
        await LegendConnectNamedValueLanguageEndToEndTests.SeedTeachingAsync(curriculum);
        await LegendConnectBatchLanguageEndToEndContractTests.SeedTeachingAsync(
            curriculum, identityNamespace: "foundation.batch.");

        // Inspect the entire admitted text corpus, including derived text units.
        // Shared ordinary words/numbers are permitted; these existing fixtures cannot
        // contain a frozen request, its distinctive entities, or its fraction answer.
        var texts = await db.LegendLanguageTextUnits.AsNoTracking().Select(item => item.Text).ToArrayAsync();
        Assert.NotEmpty(texts);
        foreach (var input in heldOutInputs)
            Assert.DoesNotContain(texts, text => string.Equals(
                LegendLanguageIdentity.NormalizeText(text), LegendLanguageIdentity.NormalizeText(input), StringComparison.Ordinal));
        foreach (var forbidden in new[] { "Devon", "Kestrel", "Marlin", "Corine", "2/17" })
            Assert.DoesNotContain(texts, text => text.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        Assert.All(db.ChangeTracker.Entries(), entry => Assert.Equal(EntityState.Unchanged, entry.State));
    }
}
