using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Security;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class ApplicationTranslationAdmissionTests
{
    [Fact]
    public async Task Import_ServesAcrossActorsAndSwitchBack_WithTruthfulProvenanceAndZeroProviderCalls()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var setup = await Setup(db);
        var artifact = Artifact(setup.Manifest);
        var json = JsonSerializer.Serialize(artifact);
        var admitted = await setup.Service.AdmitArtifactAsync(json);
        Assert.Equal(6, admitted.Imported);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant(), admitted.ArtifactSha256);
        var replay = await setup.Service.AdmitArtifactAsync(json);
        Assert.Equal(0, replay.Imported);
        Assert.Equal(6, replay.Reused);
        foreach (var language in new[] { "ht", "es", "en", "fr", "ht" })
        {
            setup.Preferences.Setup(item => item.GetCanonicalPreferredLanguageAsync(It.IsAny<MessagingActor>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(language);
            foreach (var actor in new[] { "web-user", "ios-user", "android-user" })
            {
                var catalog = await setup.Service.GetCatalogAsync(new MessagingActor(actor, MessagingParticipantTypes.Client));
                Assert.True(catalog.IsComplete);
                Assert.All(catalog.Entries, entry => Assert.True(entry.Reused));
                if (language != "en") Assert.All(catalog.Entries, entry => Assert.Equal("AssistantGenerated", entry.Provenance));
            }
        }
        var single = await setup.Router.TranslateRetainedAsync(Request(setup.Manifest.Manifest.Entries[0], "ht"));
        Assert.True(single.Succeeded);
        Assert.Equal("AssistantGenerated", single.Provenance);
        Assert.All(await db.LegendTranslationAlignments.ToListAsync(), row =>
        {
            Assert.Equal("OpenAI", row.Provider);
            Assert.Equal("gpt-6-astra", row.ProviderModel);
            Assert.Equal("sha256:" + admitted.ArtifactSha256, row.ProviderVersion);
            Assert.False(row.HumanVerified);
            Assert.Equal("StructurallyValidated", row.QualityState);
            Assert.Equal("Global", row.ReuseScope);
            Assert.Equal(string.Empty, row.ReuseScopeIdentityHash);
        });
        Assert.All(await db.LegendLanguageTextUnits.ToListAsync(), unit => Assert.False(unit.IsTrainingEligible));
        AssertNoProviderCalls(setup.Provider);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("source")]
    [InlineData("context")]
    [InlineData("scope")]
    [InlineData("placeholders")]
    [InlineData("numbers")]
    [InlineData("brand")]
    [InlineData("language")]
    [InlineData("duplicate")]
    [InlineData("catalog")]
    [InlineData("approval")]
    public async Task InvalidLastEntry_RejectsEntireBatchWithoutWritesOrProvider(string failure)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var setup = await Setup(db);
        var artifact = Artifact(setup.Manifest);
        var entries = artifact.Entries.ToArray();
        var last = entries[^1];
        entries[^1] = failure switch
        {
            "revision" => last with { SourceRevision = "stale" },
            "source" => last with { Source = "Private customer message" },
            "context" => last with { Context = "private message" },
            "scope" => last with { ReuseScope = "User" },
            "placeholders" => last with { Text = last.Text + " {private}" },
            "numbers" => last with { Text = last.Text.Replace("2", "3", StringComparison.Ordinal) },
            "brand" => last with { Text = last.Text.Replace("LEGEND", "Brand", StringComparison.Ordinal) },
            "language" => last with { LanguageCode = "unregistered-language" },
            "duplicate" => entries[0],
            "approval" => last with { TranslationPolicy = "ApprovedOnly" },
            _ => last
        };
        artifact = artifact with { Entries = entries, CatalogVersion = failure == "catalog" ? "old-catalog" : artifact.CatalogVersion };
        await Assert.ThrowsAsync<ArgumentException>(() => setup.Service.AdmitArtifactAsync(JsonSerializer.Serialize(artifact)));
        Assert.Empty(await db.LegendTranslationAlignments.ToListAsync());
        Assert.Empty(await db.LegendLanguageTextUnits.ToListAsync());
        AssertNoProviderCalls(setup.Provider);
    }

    [Fact]
    public async Task SourceChange_InvalidatesOnlyAffectedIdentity_AndApprovalOnlyCannotBeImported()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var setup = await Setup(db);
        await setup.Service.AdmitArtifactAsync(JsonSerializer.Serialize(Artifact(setup.Manifest)));
        setup.Manifest.Manifest = setup.Manifest.Manifest with
        {
            Entries = setup.Manifest.Manifest.Entries.Select((entry, index) => index == 0 ? entry with { SourceRevision = "r2" } : entry).ToArray()
        };
        var inspected = await setup.Service.InspectCatalogAsync("ht");
        Assert.Single(inspected.Entries.Where(entry => entry.FailureCode is not null));
        Assert.Equal("test.first", inspected.Entries.Single(entry => entry.FailureCode is not null).Id);
        setup.Manifest.Manifest = setup.Manifest.Manifest with
        {
            Entries = setup.Manifest.Manifest.Entries.Select(entry => entry with { TranslationPolicy = "ApprovedOnly" }).ToArray()
        };
        var pending = await setup.Service.InspectCatalogAsync("ht");
        Assert.All(pending.Entries, entry => Assert.Equal("approved_translation_unavailable", entry.FailureCode));
        await Assert.ThrowsAsync<ArgumentException>(() => setup.Service.AdmitArtifactAsync(JsonSerializer.Serialize(Artifact(setup.Manifest))));
        Assert.Equal(6, await db.LegendTranslationAlignments.CountAsync());
        AssertNoProviderCalls(setup.Provider);
    }

    [Fact]
    public async Task ExistingAzureTranslationIsPreserved_AndOtherLanguagesAndPrivatePathsStaySeparate()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var setup = await Setup(db);
        var entry = setup.Manifest.Manifest.Entries[0];
        var request = Request(entry, "ht");
        setup.Provider.SetupGet(provider => provider.ProviderName).Returns("AzureTranslator");
        setup.Provider.SetupGet(provider => provider.ProviderVersion).Returns("test-v1");
        setup.Provider.Setup(provider => provider.TranslateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<LegendConnectExternalProviderPolicy?>()))
            .ReturnsAsync((string text, string language, string? source, CancellationToken _, LegendConnectExternalProviderPolicy? policy) => new TranslationProviderResult(true, "[" + language + "] " + text, "en", "AzureTranslator"));
        var original = await setup.Router.TranslateRetainedAsync(request);
        var admitted = await setup.Service.AdmitArtifactAsync(JsonSerializer.Serialize(Artifact(setup.Manifest)));
        Assert.Equal(5, admitted.Imported);
        Assert.Equal(1, admitted.Reused);
        var reused = await setup.Router.TranslateRetainedAsync(request);
        Assert.Equal(original.Text, reused.Text);
        Assert.Equal("AzureTranslator", reused.Provider);
        var privateRequest = Request(setup.Manifest.Manifest.Entries[1], "ht") with { ReuseScope = "User", ScopeIdentityHash = new string('a', 64) };
        var privateResult = await setup.Router.TranslateRetainedAsync(privateRequest);
        Assert.Equal("AzureTranslator", privateResult.Provider);
        var ordinary = await setup.Router.TranslateAsync(setup.Manifest.Manifest.Entries[1].Source, "ht", "en");
        Assert.Equal("AzureTranslator", ordinary.Provider);
        var anotherLanguage = await setup.Router.TranslateRetainedAsync(Request(entry, "de"));
        Assert.Equal("AzureTranslator", anotherLanguage.Provider);
        setup.Provider.Verify(provider => provider.TranslateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<LegendConnectExternalProviderPolicy?>()), Times.Exactly(4));
    }

    [Fact]
    public async Task InvalidatedArtifact_IsNotServedOrResurrectedByReplay()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var setup = await Setup(db);
        var artifact = JsonSerializer.Serialize(Artifact(setup.Manifest));
        await setup.Service.AdmitArtifactAsync(artifact);
        var request = Request(setup.Manifest.Manifest.Entries[0], "ht");
        await setup.Intelligence.InvalidateRetainedTranslationAsync(LegendConnectTranslationRouter.ArtifactIdentity(request, "en", "ht"));
        Assert.Single((await setup.Service.InspectCatalogAsync("ht")).Entries.Where(entry => entry.FailureCode is not null));
        await Assert.ThrowsAsync<ArgumentException>(() => setup.Service.AdmitArtifactAsync(artifact));
        Assert.Equal(6, await db.LegendTranslationAlignments.CountAsync());
        Assert.Single(await db.LegendTranslationAlignments.Where(row => row.SupersededUtc != null).ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedAtomicImport_RestoresTrackingAndLeavesNoPartialBatch(bool collision)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var interceptor = new ImportInterruption { Collision = collision };
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlite(connection).AddInterceptors(interceptor).Options);
        await db.Database.EnsureCreatedAsync();
        var setup = await Setup(db);
        var unrelated = new AgentProfile { AgentUserId = "unrelated", ShortBio = "original", IsActive = true };
        db.AgentProfiles.Add(unrelated);
        await db.SaveChangesAsync();
        unrelated.ShortBio = "pending";
        interceptor.Enabled = true;
        var action = () => setup.Service.AdmitArtifactAsync(JsonSerializer.Serialize(Artifact(setup.Manifest)));
        if (collision) await Assert.ThrowsAsync<DbUpdateException>(action);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(action);
        Assert.Empty(await db.LegendTranslationAlignments.AsNoTracking().ToListAsync());
        Assert.Empty(await db.LegendLanguageTextUnits.AsNoTracking().ToListAsync());
        Assert.Empty(db.ChangeTracker.Entries<LegendTranslationAlignment>());
        Assert.Equal("pending", unrelated.ShortBio);
        Assert.Equal(EntityState.Modified, db.Entry(unrelated).State);
        Assert.Equal(collision ? 2 : 1, interceptor.Attempts);
    }

    [Fact]
    public async Task ConcurrentWinningImport_IsReusedWithNoOverwriteOrProviderCall()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var interceptor = new ImportInterruption { Collision = true };
        var options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).AddInterceptors(interceptor).Options;
        await using var db = new MasterAppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var setup = await Setup(db);
        var json = JsonSerializer.Serialize(Artifact(setup.Manifest));
        interceptor.BeforeFailure = async () =>
        {
            await using var winner = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
            var other = await Setup(winner);
            var result = await other.Service.AdmitArtifactAsync(json);
            Assert.Equal(6, result.Imported);
        };
        interceptor.Enabled = true;
        var result = await setup.Service.AdmitArtifactAsync(json);
        Assert.Equal(0, result.Imported);
        Assert.Equal(6, result.Reused);
        Assert.Equal(6, await db.LegendTranslationAlignments.CountAsync());
        Assert.True((await setup.Service.InspectCatalogAsync("ht")).IsComplete);
        AssertNoProviderCalls(setup.Provider);
    }

    [Fact]
    public async Task LateTextUnitFormattingConflict_RestoresEarlierRowsBeforeAnyLaterSave()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var setup = await Setup(db);
        var artifact = Artifact(setup.Manifest);
        var target = artifact.Entries[1];
        db.LegendLanguageTextUnits.Add(new LegendLanguageTextUnit
        {
            Id = Guid.NewGuid(), LanguageCode = target.LanguageCode, StoragePartition = "/ht",
            NormalizedHash = LegendLanguageIdentity.TextHash(target.Text), Text = target.Text.Replace("Open ", "Open  ", StringComparison.Ordinal),
            Provenance = "FounderApproved", IsTrainingEligible = true, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => setup.Service.AdmitArtifactAsync(JsonSerializer.Serialize(artifact)));
        await db.SaveChangesAsync();
        Assert.Empty(await db.LegendTranslationAlignments.ToListAsync());
        var existing = Assert.Single(await db.LegendLanguageTextUnits.ToListAsync());
        Assert.Contains("Open  ", existing.Text, StringComparison.Ordinal);
        Assert.True(existing.IsTrainingEligible);
        Assert.Empty(db.ChangeTracker.Entries<LegendTranslationAlignment>());
    }

    [Fact]
    public async Task RejectedArtifactQuality_IsNotReusableEvenWithoutSupersededTimestamp()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var setup = await Setup(db);
        await setup.Service.AdmitArtifactAsync(JsonSerializer.Serialize(Artifact(setup.Manifest)));
        var alignment = await db.LegendTranslationAlignments.FirstAsync(row => row.PairKey == "en:ht");
        alignment.QualityState = "Rejected";
        await db.SaveChangesAsync();
        var result = await setup.Service.InspectCatalogAsync("ht");
        Assert.Single(result.Entries.Where(entry => entry.FailureCode is not null));
        await Assert.ThrowsAsync<ArgumentException>(() => setup.Service.AdmitArtifactAsync(JsonSerializer.Serialize(Artifact(setup.Manifest))));
        AssertNoProviderCalls(setup.Provider);
    }

    [Theory]
    [InlineData("Visit https://example.com", "Vizite https://different.example.com")]
    [InlineData("<b>LEGEND</b><i>2</i>", "<i>2</i><b>LEGEND</b>")]
    [InlineData("Price $20", "Prix 20")]
    [InlineData("Hello {name}", "Bonjour")]
    [InlineData("First\nSecond", "Premier Deuxième")]
    public void StructuralValidationRejectsTokenAndFormattingDamage(string source, string translated) =>
        Assert.False(ApplicationLocalizationService.IsValidArtifactText(source, translated,
            string.Join(',', TranslationOutputValidator.PlaceholderNames(source))));

    [Fact]
    public void FounderImportRequiresAuthenticationFounderAndAntiforgery()
    {
        var controller = typeof(LegendConnectController);
        Assert.NotNull(Attribute.GetCustomAttribute(controller, typeof(AuthorizeAttribute)));
        Assert.NotNull(Attribute.GetCustomAttribute(controller, typeof(FounderOnlyAttribute)));
        Assert.NotNull(Attribute.GetCustomAttribute(controller.GetMethod(nameof(LegendConnectController.ImportApplicationCopy))!, typeof(ValidateAntiForgeryTokenAttribute)));
    }

    [Fact]
    public async Task ImportSharingEligibleUnits_DoesNotBecomeTrainingMaterialOrChangeTheirEligibility()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var setup = await Setup(db);
        var artifact = Artifact(setup.Manifest);
        var units = artifact.Entries.SelectMany(entry => new[] { (Language: "en", Text: entry.Source), (Language: entry.LanguageCode, Text: entry.Text) })
            .Distinct().Select(item => new LegendLanguageTextUnit
            {
                Id = Guid.NewGuid(), LanguageCode = item.Language, StoragePartition = "/" + item.Language,
                Text = item.Text, NormalizedHash = LegendLanguageIdentity.TextHash(item.Text),
                Provenance = "SystemValidatedMachine", IsTrainingEligible = true,
                CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow
            }).ToArray();
        db.LegendLanguageTextUnits.AddRange(units);
        db.Set<LegendConnectRuntimePolicy>().Add(new LegendConnectRuntimePolicy
        {
            ScopeKey = "Global", CompletedLanguageIntelligenceEvaluatorVersion = LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            TargetLanguageIntelligenceEvaluatorVersion = LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            LanguageIntelligenceReevaluationPhase = "Complete"
        });
        await db.SaveChangesAsync();
        await setup.Service.AdmitArtifactAsync(JsonSerializer.Serialize(artifact));
        var dataset = await new LegendConnectTrainingDatasetCompiler(db, LegendModelTrainingTestConfiguration.Hosted).CompileAsync();
        Assert.Empty(dataset.Training);
        Assert.Empty(dataset.HeldOut);
        Assert.All(units, unit => Assert.True(unit.IsTrainingEligible));
        Assert.Equal(6, await db.LegendTranslationAlignments.CountAsync());
        AssertNoProviderCalls(setup.Provider);
    }

    [Fact]
    public async Task InventoryNeverSavesEvenForInvalidRetainedRows()
    {
        var counter = new SaveCounter();
        await using var db = ControllerTestHelpers.BuildDb(counter);
        var setup = await Setup(db);
        await setup.Service.AdmitArtifactAsync(JsonSerializer.Serialize(Artifact(setup.Manifest)));
        var targetId = (await db.LegendTranslationAlignments.FirstAsync(row => row.PairKey == "en:ht")).TargetTextUnitId;
        var target = await db.LegendLanguageTextUnits.SingleAsync(unit => unit.Id == targetId);
        target.Text = "Malformed output {unexpected}";
        await db.SaveChangesAsync();
        counter.Attempts = 0;
        var inventory = await setup.Service.InspectCatalogAsync("ht");
        Assert.False(inventory.IsComplete);
        Assert.Equal(0, counter.Attempts);
        AssertNoProviderCalls(setup.Provider);
    }

    [Fact]
    public async Task ConcurrentRetainedReadsAcrossIndependentContextsNeverCallProvider()
    {
        var options = new DbContextOptionsBuilder<MasterAppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using (var seed = new MasterAppDbContext(options))
        {
            var setup = await Setup(seed);
            await setup.Service.AdmitArtifactAsync(JsonSerializer.Serialize(Artifact(setup.Manifest)));
        }
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 12).Select(async index =>
        {
            await using var db = new MasterAppDbContext(options);
            var setup = await Setup(db);
            await start.Task;
            var language = new[] { "ht", "es", "fr" }[index % 3];
            for (var repeat = 0; repeat < 3; repeat++)
            {
                var results = await setup.Router.TranslateRetainedBatchAsync(setup.Manifest.Manifest.Entries.Select(entry => Request(entry, language)).ToArray());
                Assert.All(results, item => { Assert.True(item.Succeeded); Assert.True(item.Reused); Assert.Equal("AssistantGenerated", item.Provenance); });
                Assert.True((await setup.Router.TranslateRetainedAsync(Request(setup.Manifest.Manifest.Entries[0], language))).Reused);
            }
            AssertNoProviderCalls(setup.Provider);
        }).ToArray();
        start.SetResult();
        await Task.WhenAll(tasks);
    }

    private sealed class SaveCounter : SaveChangesInterceptor
    {
        public int Attempts { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Attempts++;
            return ValueTask.FromResult(result);
        }
    }

    private static void AssertNoProviderCalls(Mock<ITranslationProvider> provider) =>
        Assert.DoesNotContain(provider.Invocations, call => !call.Method.Name.StartsWith("get_", StringComparison.Ordinal) && call.Method.Name != "RequestCharacterCount");

    private static async Task<Fixture> Setup(MasterAppDbContext db)
    {
        var config = new ConfigurationBuilder().Build();
        var languages = new LegendLanguageRegistry(db, config);
        await languages.ListEnabledTranslationLanguagesAsync();
        var intelligence = new LegendConnectTranslationIntelligence(db, config);
        var provider = new Mock<ITranslationProvider>(MockBehavior.Strict);
        provider.SetupGet(value => value.ProviderName).Returns("AzureTranslator");
        provider.SetupGet(value => value.ProviderVersion).Returns("test-v1");
        provider.Setup(value => value.RequestCharacterCount(It.IsAny<string>())).Returns((string text) => text.Length);
        var capacity = new Mock<ITranslationCapacityAuthority>();
        capacity.Setup(value => value.TryReserveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<TranslationCapacityPurpose>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranslationCapacityReservationResult(new TranslationCapacityReservation("AzureTranslator", DateOnly.FromDateTime(DateTime.UtcNow), 1, TranslationCapacityPurpose.Live, Guid.NewGuid())));
        var router = new LegendConnectTranslationRouter(provider.Object, languages, capacity.Object,
            NullLogger<LegendConnectTranslationRouter>.Instance, intelligence: intelligence);
        var preferences = new Mock<IControlledResourceAccessService>(MockBehavior.Strict);
        preferences.Setup(value => value.GetCanonicalPreferredLanguageAsync(It.IsAny<MessagingActor>(), It.IsAny<CancellationToken>())).ReturnsAsync("ht");
        var manifest = new TestManifest();
        var service = new ApplicationLocalizationService(manifest, preferences.Object, languages, router, intelligence,
            NullLogger<ApplicationLocalizationService>.Instance);
        // Metadata property reads are not provider operations.
        provider.Invocations.Clear();
        return new(manifest, service, router, intelligence, provider, preferences);
    }

    private static ApplicationTranslationArtifact Artifact(TestManifest manifest) => new(
        "application-copy-admission-v1", manifest.Manifest.CatalogVersion, "en", "gpt-6-astra", "Codex coding session",
        new[] { "ht", "es", "fr" }.SelectMany(language => manifest.Manifest.Entries.Select(entry => new ApplicationTranslationArtifactEntry(
            entry.Id, entry.Source, entry.SourceRevision, entry.Context, entry.TranslationPolicy, entry.ReuseScope,
            language, "[" + language + "] " + entry.Source, entry.Placeholders))).ToArray());

    private static RetainedTranslationRequest Request(ApplicationCopyManifestEntry entry, string language) => new(
        entry.Id, entry.Source, "en", language, entry.SourceRevision, entry.Context, string.Join(',', entry.Placeholders), "Global");

    private sealed class TestManifest : IApplicationCopyManifestSource
    {
        public ApplicationCopyManifest Manifest { get; set; } = new("test-catalog", "en", new[]
        {
            new ApplicationCopyManifestEntry("test.first", "Welcome, {name}.", "home heading", "r1", new[] { "name" }, "AzureAllowed", "Global"),
            new ApplicationCopyManifestEntry("test.second", "Open LEGEND in 2 steps", "button label", "r1", Array.Empty<string>(), "AzureAllowed", "Global")
        });
    }

    private sealed record Fixture(TestManifest Manifest, ApplicationLocalizationService Service,
        LegendConnectTranslationRouter Router, LegendConnectTranslationIntelligence Intelligence,
        Mock<ITranslationProvider> Provider, Mock<IControlledResourceAccessService> Preferences);

    private sealed class ImportInterruption : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public bool Collision { get; set; }
        public int Attempts { get; private set; }
        public Func<Task>? BeforeFailure { get; set; }
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<LegendTranslationAlignment>().Any(entry => entry.State == EntityState.Added))
            {
                Attempts++;
                if (BeforeFailure is not null) await BeforeFailure();
                if (Collision) throw new DbUpdateException("synthetic import collision");
                throw new OperationCanceledException("synthetic import cancellation");
            }
            return result;
        }
    }
}
