using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Models;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

/// <summary>
/// Controlled Founder-facing proof for the existing Legend Connect authority.
/// These tests exercise the actual MVC POST actions and then query fresh
/// server projections; no alternate Founder pipeline or test-only data store
/// is introduced.
/// </summary>
[Collection("LegendConnectFounderEnvironment")]
public sealed class LegendConnectOperationalProofTests
{
    [Fact]
    public async Task FounderManualTraining_PostsPrgPersistsAuditsAndRefreshesTheAuthoritativeProjection()
    {
        var previousFounder = Environment.GetEnvironmentVariable("FOUNDER_OID");
        var founderId = Guid.NewGuid().ToString();
        Environment.SetEnvironmentVariable("FOUNDER_OID", founderId);
        try
        {
            await using var db = ControllerTestHelpers.BuildDb();
            var configuration = Configuration();
            var registry = new LegendLanguageRegistry(db, configuration);
            var operations = Operations(db, registry, configuration);
            var founder = ControllerTestHelpers.BuildUser(founderId);
            var service = new FounderLegendConnectService(operations);
            var controller = Controller(service, founder);

            var route = Assert.IsType<RedirectToActionResult>(await controller.SubmitKnowledge(
                new FounderLegendConnectKnowledgeInput
                {
                    SourceLanguageCode = "en",
                    SourceText = "Good morning, how are you today?",
                    TargetLanguageCode = "ht",
                    TargetText = "Bonjou, kijan ou ye jodi a?",
                    ContextCategory = "Greeting",
                    UsageRegister = "Everyday"
                }, CancellationToken.None));

            Assert.Equal(nameof(LegendConnectController.Index), route.ActionName);
            Assert.Equal("en", route.RouteValues!["language"]);
            Assert.Equal("en:ht", route.RouteValues["pair"]);
            Assert.Equal("Approved knowledge was saved.", controller.TempData["LegendConnectSuccess"]);
            Assert.Equal(2, await db.LegendLanguageTextUnits.CountAsync());
            Assert.Single(await db.LegendTranslationAlignments.ToListAsync());
            Assert.Single(await db.LegendLanguageContextRelationships.ToListAsync());
            Assert.Contains(await db.LegendConnectKnowledgeAuditEntries.ToListAsync(), item =>
                item.Action == "FounderKnowledgeSubmitted" && item.Result == "Succeeded");

            var freshDashboard = await service.GetDashboardAsync(founder, "en", "en:ht");
            Assert.Equal(1, freshDashboard.SelectedLanguage!.CanonicalEntryCount);
            Assert.Single(freshDashboard.SelectedPair!.RecentAlignments);

            controller.TempData = NewTempData(controller.HttpContext);
            var duplicateRoute = Assert.IsType<RedirectToActionResult>(await controller.SubmitKnowledge(
                new FounderLegendConnectKnowledgeInput
                {
                    SourceLanguageCode = "en",
                    SourceText = "  Good morning, how are you today?  ",
                    TargetLanguageCode = "ht",
                    TargetText = "Bonjou, kijan ou ye jodi a?"
                }, CancellationToken.None));

            Assert.Equal(nameof(LegendConnectController.Index), duplicateRoute.ActionName);
            Assert.Equal("This exact entry already exists in this language.", controller.TempData["LegendConnectError"]);
            Assert.Equal(2, await db.LegendLanguageTextUnits.CountAsync());
            Assert.Contains(await db.LegendConnectKnowledgeAuditEntries.ToListAsync(), item =>
                item.Action == "FounderKnowledgeSubmitted" && item.Result == "DuplicatePrevented");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", previousFounder);
        }
    }

    [Fact]
    public async Task FounderCorrection_IsOneRelationalTransactionAndTrustedMemoryUsesTheReplacement()
    {
        var databaseName = "legend-connect-operational-" + Guid.NewGuid().ToString("N");
        await using var keeper = new SqliteConnection($"Data Source=file:{databaseName}?mode=memory&cache=shared");
        await keeper.OpenAsync();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(keeper).Options;
        await using var db = new MasterAppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var operations = Operations(db, registry, configuration);

        var submitted = await operations.SubmitFounderKnowledgeAsync("founder", new LegendConnectKnowledgeSubmission(
            "en", "Good morning", "ht", "Bonjou", "Greeting", null, null, "FounderApproved"));
        Assert.True(submitted.Succeeded);

        var corrected = await operations.CorrectFounderKnowledgeAsync("founder", submitted.AlignmentId!.Value,
            new LegendConnectKnowledgeSubmission(
                "en", "Good morning", "ht", "Bonjou zanmi", "Greeting", null, null, "FounderApproved"));

        Assert.True(corrected.Succeeded);
        var active = await db.LegendTranslationAlignments.Where(item => item.SupersededUtc == null).ToListAsync();
        Assert.Single(active);
        Assert.Equal(corrected.AlignmentId, active[0].Id);
        Assert.NotEqual(submitted.AlignmentId, active[0].Id);
        Assert.NotNull((await db.LegendTranslationAlignments.SingleAsync(item => item.Id == submitted.AlignmentId)).SupersededUtc);
        Assert.Contains(await db.LegendConnectKnowledgeAuditEntries.ToListAsync(), item =>
            item.Action == "FounderKnowledgeCorrected" && item.AlignmentId == corrected.AlignmentId &&
            item.SupersededAlignmentId == submitted.AlignmentId);

        var provider = new RecordingProvider();
        var router = new LegendConnectTranslationRouter(
            provider,
            registry,
            new TranslationCapacityAuthority(db, configuration, NullLogger<TranslationCapacityAuthority>.Instance),
            NullLogger<LegendConnectTranslationRouter>.Instance,
            intelligence: new LegendConnectTranslationIntelligence(db, configuration));
        var translation = await router.TranslateAsync("Good morning", "ht", "en");

        Assert.True(translation.Succeeded);
        Assert.Equal("Bonjou zanmi", translation.TranslatedText);
        Assert.Equal("LegendConnectTranslationMemory", translation.Provider);
        Assert.Equal(0, provider.TranslateCalls);
    }

    [Fact]
    public async Task FounderValidationFailure_UsesTheExistingPrgFeedbackInsteadOfAQuietFailure()
    {
        var previousFounder = Environment.GetEnvironmentVariable("FOUNDER_OID");
        var founderId = Guid.NewGuid().ToString();
        Environment.SetEnvironmentVariable("FOUNDER_OID", founderId);
        try
        {
            await using var db = ControllerTestHelpers.BuildDb();
            var configuration = Configuration();
            var registry = new LegendLanguageRegistry(db, configuration);
            var controller = Controller(new FounderLegendConnectService(Operations(db, registry, configuration)),
                ControllerTestHelpers.BuildUser(founderId));

            var route = Assert.IsType<RedirectToActionResult>(await controller.SubmitKnowledge(
                new FounderLegendConnectKnowledgeInput
                {
                    SourceLanguageCode = "en",
                    SourceText = "Source only",
                    TargetLanguageCode = "ht"
                }, CancellationToken.None));

            Assert.Equal(nameof(LegendConnectController.Index), route.ActionName);
            Assert.Equal("A translation pair requires both a target language and target text.", controller.TempData["LegendConnectError"]);
            Assert.Empty(await db.LegendLanguageTextUnits.ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", previousFounder);
        }
    }

    private static LegendConnectOperations Operations(
        MasterAppDbContext db,
        ILegendLanguageRegistry registry,
        IConfiguration configuration) =>
        new(db, registry, new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance), configuration);

    private static LegendConnectController Controller(FounderLegendConnectService service, ClaimsPrincipal founder)
    {
        var http = new DefaultHttpContext { User = founder };
        return new LegendConnectController(service, NullLogger<LegendConnectController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = NewTempData(http)
        };
    }

    private static TempDataDictionary NewTempData(HttpContext context) =>
        new(context, Mock.Of<ITempDataProvider>());

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendConnect:CorpusAcquisition:Enabled"] = "false",
            ["LegendConnect:Providers:AzureTranslator:MonthlyCapacityCharacters"] = "10000",
            ["LegendConnect:Providers:AzureTranslator:LiveReserveCharacters"] = "100",
            ["LegendConnect:ContextualComposition:Mode"] = "Shadow",
            ["LegendConnect:ContextualComposition:MinimumConfidence"] = "0.98",
            ["AzureTranslator:Endpoint"] = "https://translator.example.test",
            ["AzureTranslator:Key"] = "test-key"
        }).Build();

    private sealed class RecordingProvider : ITranslationProvider
    {
        public string ProviderName => "AzureTranslator";
        public int TranslateCalls { get; private set; }

        public Task<TranslationDetectionResult> DetectLanguageAsync(
            string text,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranslationDetectionResult(true, "en"));

        public Task<TranslationProviderResult> TranslateAsync(
            string text,
            string targetLanguage,
            string? sourceLanguage = null,
            CancellationToken cancellationToken = default)
        {
            TranslateCalls++;
            return Task.FromResult(new TranslationProviderResult(true, "Unexpected provider result", sourceLanguage, ProviderName));
        }
    }
}
