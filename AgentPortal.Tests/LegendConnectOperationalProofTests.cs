using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
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
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    private const string FounderHeader = "X-Legend-Connect-Founder";

    [Fact]
    public async Task FounderManualTraining_HttpPostWithAntiforgery_ReachesTheCanonicalRouteThenRedirects()
    {
        var previousFounder = Environment.GetEnvironmentVariable("FOUNDER_OID");
        var founderId = Guid.NewGuid().ToString();
        Environment.SetEnvironmentVariable("FOUNDER_OID", founderId);
        try
        {
            using var host = await BuildFounderHttpHostAsync();
            var client = host.GetTestClient();
            var tokenRequest = new HttpRequestMessage(HttpMethod.Get, "/__legend-connect-proof/token");
            tokenRequest.Headers.Add(FounderHeader, founderId);
            var tokenResponse = await client.SendAsync(tokenRequest);
            tokenResponse.EnsureSuccessStatusCode();
            var token = await tokenResponse.Content.ReadFromJsonAsync<AntiforgeryTokenDto>();
            Assert.NotNull(token);

            var post = new HttpRequestMessage(HttpMethod.Post, "/founder/legend-connect/knowledge")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["SourceLanguageCode"] = "en",
                    ["TargetLanguageCode"] = "ht",
                    ["ContextCategory"] = "Greeting",
                    ["SourceText"] = "Good morning, how are you today?",
                    ["TargetText"] = "Bonjou, kijan ou ye jodi a?"
                })
            };
            post.Headers.Add(FounderHeader, founderId);
            post.Headers.Add("RequestVerificationToken", token!.RequestToken);
            var antiforgeryCookie = ExtractAntiforgeryCookie(tokenResponse);
            if (!string.IsNullOrWhiteSpace(antiforgeryCookie))
                post.Headers.Add("Cookie", antiforgeryCookie);

            var response = await client.SendAsync(post);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/founder/legend-connect?language=en&pair=en%3Aht", response.Headers.Location?.ToString());
            await using var scope = host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
            Assert.Equal(2, await db.LegendLanguageTextUnits.CountAsync());
            Assert.Single(await db.LegendTranslationAlignments.ToListAsync());
            Assert.Contains(await db.LegendConnectKnowledgeAuditEntries.ToListAsync(), item =>
                item.Action == "FounderKnowledgeSubmitted" && item.Result == "Succeeded");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", previousFounder);
        }
    }

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
    public async Task FounderMonolingualSeed_QueuesExistingAutonomousCandidatesAndExpandsThroughTheExistingPipeline()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration(corpusAcquisitionEnabled: true);
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var operations = new LegendConnectOperations(db, registry, corpus, configuration);

        var seed = await operations.SubmitFounderKnowledgeAsync("founder", new LegendConnectKnowledgeSubmission(
            "en", "Are you coming over for dinner tonight?", null, null, "Conversation", "Everyday", null, "FounderApproved"));

        Assert.True(seed.Succeeded);
        var haitianCandidate = Assert.Single(await db.LegendCorpusCandidates
            .Where(item => item.SourceLanguageCode == "en" && item.TargetLanguageCode == "ht")
            .ToListAsync());
        Assert.True(haitianCandidate.IsApproved);
        Assert.Equal("Pending", haitianCandidate.ProcessingState);
        db.LegendTranslationPairDemands.Add(new LegendTranslationPairDemand
        {
            Id = Guid.NewGuid(), PairKey = "en:ht", TranslationRequestCount = 100, LastRequestedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var provider = new RecordingProvider();
        var autonomous = new LegendConnectAutonomousLearningService(
            db,
            registry,
            provider,
            new TranslationCapacityAuthority(db, configuration, NullLogger<TranslationCapacityAuthority>.Instance),
            corpus,
            new LegendConnectAutonomousGapPlanner(db, registry),
            configuration);
        await autonomous.ProcessOneAsync();

        Assert.Equal(1, provider.TranslateCalls);
        Assert.Equal("Queued", (await db.LegendCorpusCandidates.SingleAsync(item => item.Id == haitianCandidate.Id)).ProcessingState);
        Assert.Contains(await db.LegendLanguageTextUnits.ToListAsync(), item =>
            item.LanguageCode == "ht" && item.Text == "Unexpected provider result");
        Assert.Contains(await db.LegendTranslationAlignments.ToListAsync(), item => item.PairKey == "en:ht" && item.SupersededUtc == null);
        Assert.Contains(await db.LegendLanguageContextRelationships.ToListAsync(), item => item.PairKey == "en:ht");
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

    private static IConfiguration Configuration(bool corpusAcquisitionEnabled = false) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendConnect:CorpusAcquisition:Enabled"] = corpusAcquisitionEnabled ? "true" : "false",
            ["LegendConnect:Providers:AzureTranslator:MonthlyCapacityCharacters"] = "10000",
            ["LegendConnect:Providers:AzureTranslator:LiveReserveCharacters"] = "100",
            ["LegendConnect:ContextualComposition:Mode"] = "Shadow",
            ["LegendConnect:ContextualComposition:MinimumConfidence"] = "0.98",
            ["AzureTranslator:Endpoint"] = "https://translator.example.test",
            ["AzureTranslator:Key"] = "test-key"
        }).Build();

    private static Task<IHost> BuildFounderHttpHostAsync()
    {
        var databaseName = "legend-connect-founder-http-" + Guid.NewGuid().ToString("N");
        return new HostBuilder()
            .ConfigureWebHost(webBuilder => webBuilder
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddDataProtection();
                    services.AddControllersWithViews()
                        .AddApplicationPart(typeof(LegendConnectController).Assembly)
                        .AddApplicationPart(typeof(LegendConnectOperationalProofTests).Assembly);
                    services.AddAntiforgery(options => options.HeaderName = "RequestVerificationToken");
                    services.AddAuthentication("LegendConnectTest")
                        .AddScheme<AuthenticationSchemeOptions, LegendConnectFounderAuthHandler>("LegendConnectTest", _ => { });
                    services.AddAuthorization(options => options.DefaultPolicy = new AuthorizationPolicyBuilder("LegendConnectTest")
                        .RequireAuthenticatedUser()
                        .Build());
                    services.AddDbContext<MasterAppDbContext>(options => options.UseInMemoryDatabase(databaseName));
                    services.AddSingleton<IConfiguration>(Configuration());
                    services.AddScoped<ILegendLanguageRegistry, LegendLanguageRegistry>();
                    services.AddScoped<LegendConnectCorpusService>();
                    services.AddScoped<ILegendConnectOperations, LegendConnectOperations>();
                    services.AddScoped<FounderLegendConnectService>();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                }))
            .StartAsync();
    }

    private static string ExtractAntiforgeryCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(value => value.StartsWith(".AspNetCore.Antiforgery", StringComparison.OrdinalIgnoreCase))?
                .Split(';')[0] ?? string.Empty
            : string.Empty;

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

[Route("__legend-connect-proof")]
public sealed class LegendConnectOperationalProofController : ControllerBase
{
    [HttpGet("token")]
    public IActionResult Token([FromServices] IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new AntiforgeryTokenDto(tokens.RequestToken ?? string.Empty));
    }
}

public sealed record AntiforgeryTokenDto(string RequestToken);

public sealed class LegendConnectFounderAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public LegendConnectFounderAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Legend-Connect-Founder", out var founder) ||
            string.IsNullOrWhiteSpace(founder))
            return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity(new[] { new Claim("oid", founder.ToString()) }, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
