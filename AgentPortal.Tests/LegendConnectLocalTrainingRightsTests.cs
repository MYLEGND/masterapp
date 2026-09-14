using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectLocalTrainingRightsTests
{
    [Fact]
    public async Task CanonicallyValidatedTeacherOutput_CannotLaunderItsOriginThroughOwnedAttestation()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        Seed(db, "Founder", null);
        var family = new LegendCurriculumFamily { FamilyKey = "machine-origin", Provenance = "SystemValidatedMachine" };
        db.Add(family);
        foreach (var unit in db.Set<LegendLanguageTextUnit>().Local.ToArray())
        {
            unit.Provenance = "SystemValidatedMachine";
            db.Add(new LegendCurriculumExample { CurriculumFamilyId = family.Id, TextUnitId = unit.Id,
                LanguageCode = unit.LanguageCode, Provenance = "SystemValidatedMachine" });
        }
        var alignment = db.Set<LegendTranslationAlignment>().Local.Single();
        alignment.Provider = "LegendSystemValidator";
        alignment.Provenance = "SystemValidatedMachine";
        db.Add(new LegendLanguageTeacherProposal { FamilyKey = family.FamilyKey, ValidationState = "Admitted", Provenance = "MachineProposed" });
        await db.SaveChangesAsync();
        var prior = await new LegendConnectTrainingDatasetCompiler(db, LegendModelTrainingTestConfiguration.Hosted).CompileAsync();
        var example = Assert.Single(prior.Training.Concat(prior.HeldOut));
        var local = await new LegendConnectTrainingDatasetCompiler(db,
            new ConfigurationBuilder().AddInMemoryCollection(Rights(example, true)).Build()).CompileAsync();
        Assert.Empty(local.Training);
        Assert.Empty(local.HeldOut);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("LocalMlx")]
    public async Task MissingConfigurationNeverDefaultsToUnrestrictedHostedCompilation(string? backend)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        Seed(db, "Founder", null);
        await db.SaveChangesAsync();
        var configuration = backend is null ? null : new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["LegendConnect:ModelTraining:Backend"] = backend }).Build();
        var manifest = await new LegendConnectTrainingDatasetCompiler(db, configuration).CompileAsync();
        Assert.Empty(manifest.Training);
        Assert.Empty(manifest.HeldOut);
    }

    [Theory]
    [InlineData(false, "Founder", null, 0)]
    [InlineData(true, "Founder", null, 1)]
    [InlineData(true, "OpenAI", null, 0)]
    [InlineData(true, "Founder", "User", 0)]
    [InlineData(true, "Founder", "Tenant", 0)]
    [InlineData(true, "Founder", "Conversation", 0)]
    public async Task SharedWeights_RequireRightsAndExcludeRestrictedOrigins(
        bool attest, string provider, string? scope, int expectedCount)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        Seed(db, provider, scope);
        await db.SaveChangesAsync();
        var legacy = await new LegendConnectTrainingDatasetCompiler(db, LegendModelTrainingTestConfiguration.Hosted).CompileAsync();
        var example = Assert.Single(legacy.Training.Concat(legacy.HeldOut));
        var settings = Rights(example, attest);
        var manifest = await new LegendConnectTrainingDatasetCompiler(db,
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build()).CompileAsync();
        Assert.Equal(expectedCount, manifest.Training.Count + manifest.HeldOut.Count);
        Assert.NotEqual(legacy.DatasetIdentity, manifest.DatasetIdentity);
        Assert.Equal(2, db.Set<LegendLanguageTextUnit>().Count());
        Assert.True(db.Set<LegendTranslationAlignment>().Single().HumanVerified);
    }

    [Fact]
    public async Task ChangedText_CannotReuseOldRightsAttestationOrDatasetIdentity()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        Seed(db, "Founder", null);
        await db.SaveChangesAsync();
        var legacy = await new LegendConnectTrainingDatasetCompiler(db, LegendModelTrainingTestConfiguration.Hosted).CompileAsync();
        var example = Assert.Single(legacy.Training.Concat(legacy.HeldOut));
        var settings = Rights(example, true);
        var compiler = new LegendConnectTrainingDatasetCompiler(db,
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        var admitted = await compiler.CompileAsync();
        db.Set<LegendLanguageTextUnit>().Single(unit => unit.LanguageCode == "en").Text = "Changed content";
        await db.SaveChangesAsync();
        var changed = await compiler.CompileAsync();
        Assert.Empty(changed.Training);
        Assert.Empty(changed.HeldOut);
        Assert.NotEqual(admitted.DatasetIdentity, changed.DatasetIdentity);
    }

    [Theory]
    [InlineData("PrivacyScope", "Private")]
    [InlineData("RightsBasis", "HumanVerified")]
    [InlineData("SharedTrainingPermitted", "false")]
    [InlineData("SourceReference", "")]
    [InlineData("TrainingPurpose", "CurrentOrganizationFacts")]
    public async Task IncompletePermission_FailsClosed(string key, string value)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        Seed(db, "Founder", null);
        await db.SaveChangesAsync();
        var legacy = await new LegendConnectTrainingDatasetCompiler(db, LegendModelTrainingTestConfiguration.Hosted).CompileAsync();
        var example = Assert.Single(legacy.Training.Concat(legacy.HeldOut));
        var settings = Rights(example, true);
        settings["LegendConnect:ModelTraining:RightsAttestations:0:" + key] = value;
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new LegendConnectTrainingDatasetCompiler(db,
                new ConfigurationBuilder().AddInMemoryCollection(settings).Build()).CompileAsync());
        Assert.Equal("local_training_rights_manifest_invalid", failure.Message);
    }

    private static Dictionary<string, string?> Rights(LegendConnectTrainingDatasetExample example, bool attest)
    {
        var result = new Dictionary<string, string?> { ["LegendConnect:ModelTraining:Backend"] = "LocalMlx" };
        if (!attest) return result;
        const string prefix = "LegendConnect:ModelTraining:RightsAttestations:0:";
        result[prefix + "EvidenceIdentity"] = example.EvidenceIdentity;
        result[prefix + "SourceTextHash"] = Hash(example.SourceText);
        result[prefix + "TargetTextHash"] = Hash(example.TargetText);
        result[prefix + "RightsBasis"] = "Owned";
        result[prefix + "SourceReference"] = "test-fixture:deterministic-public-example";
        result[prefix + "PrivacyScope"] = "PublicNonPersonal";
        result[prefix + "SharedTrainingPermitted"] = "true";
        result[prefix + "TrainingPurpose"] = "TransferableSkill";
        return result;
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static void Seed(MasterAppDbContext db, string provider, string? scope)
    {
        db.Add(new LegendConnectRuntimePolicy
        {
            ScopeKey = "Global",
            CompletedLanguageIntelligenceEvaluatorVersion = LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            TargetLanguageIntelligenceEvaluatorVersion = LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            LanguageIntelligenceReevaluationPhase = "Complete"
        });
        var source = new LegendLanguageTextUnit { LanguageCode = "en", Text = "zero", NormalizedHash = Hash("zero"), Provenance = "FounderApproved", IsTrainingEligible = true };
        var target = new LegendLanguageTextUnit { LanguageCode = "ht", Text = "zewo", NormalizedHash = Hash("zewo"), Provenance = "FounderApproved", IsTrainingEligible = true };
        db.AddRange(source, target);
        db.Add(new LegendTranslationAlignment
        {
            PairKey = "en:ht", SourceTextUnitId = source.Id, TargetTextUnitId = target.Id,
            Provider = provider, Provenance = provider == "OpenAI" ? "ProviderDerived" : "FounderApproved",
            HumanVerified = true, QualityState = "Verified", ReuseScope = scope,
            ReuseScopeIdentityHash = scope is null ? null : Hash("private-identity")
        });
    }
}
