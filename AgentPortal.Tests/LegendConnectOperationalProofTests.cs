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
using AgentPortal.Security;
using AgentPortal.Services;
using Domain.Billing;
using Domain.Entities;
using Domain.Messaging;
using Domain.Moderation;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Infrastructure.Moderation;
using Infrastructure.Notifications;
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
            using var host = await BuildFounderHttpHostAsync(founderId);
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
    public async Task FounderFixTargetTranslation_HttpPostPreservesNormalModeThenUsesTheCanonicalResolver()
    {
        var previousFounder = Environment.GetEnvironmentVariable("FOUNDER_OID");
        var founderId = Guid.NewGuid().ToString();
        Environment.SetEnvironmentVariable("FOUNDER_OID", founderId);
        try
        {
            using var host = await BuildFounderHttpHostAsync(founderId);
            var client = host.GetTestClient();
            var tokenRequest = new HttpRequestMessage(HttpMethod.Get, "/__legend-connect-proof/token");
            tokenRequest.Headers.Add(FounderHeader, founderId);
            var tokenResponse = await client.SendAsync(tokenRequest);
            tokenResponse.EnsureSuccessStatusCode();
            var token = await tokenResponse.Content.ReadFromJsonAsync<AntiforgeryTokenDto>();
            Assert.NotNull(token);
            var cookie = ExtractAntiforgeryCookie(tokenResponse);

            await AssertFounderRedirectAsync(client, founderId, token!.RequestToken, cookie,
                "/founder/legend-connect/knowledge", new Dictionary<string, string>
                {
                    ["SourceLanguageCode"] = "en",
                    ["SourceText"] = "The Founder already approved this canonical source.",
                    // This mode-specific field must be inert while the toggle is off.
                    ["TargetTranslationRows"] = "ignored source | ignored target"
                });

            await using (var initialScope = host.Services.CreateAsyncScope())
            {
                var initialDb = initialScope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
                Assert.Single(await initialDb.LegendFounderTrainingSubmissions.ToListAsync());
                Assert.Single(await initialDb.LegendLanguageTextUnits.Where(item => item.LanguageCode == "en").ToListAsync());
                Assert.Empty(await initialDb.LegendTranslationAlignments.ToListAsync());
            }

            await AssertFounderRedirectAsync(client, founderId, token.RequestToken, cookie,
                "/founder/legend-connect/knowledge", new Dictionary<string, string>
                {
                    ["FixTargetTranslation"] = "true",
                    ["SourceLanguageCode"] = "en",
                    ["TargetLanguageCode"] = "ht",
                    ["TargetTranslationRows"] = "The Founder already approved this canonical source. | Fondatè a deja apwouve sous kanonik sa a."
                });

            await using var verificationScope = host.Services.CreateAsyncScope();
            var db = verificationScope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
            Assert.Single(await db.LegendFounderTrainingSubmissions.ToListAsync());
            Assert.Single(await db.LegendLanguageTextUnits.Where(item => item.LanguageCode == "en").ToListAsync());
            var alignment = Assert.Single(await db.LegendTranslationAlignments.ToListAsync());
            Assert.True(alignment.HumanVerified);
            Assert.Equal("FounderApproved", alignment.Provenance);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", previousFounder);
        }
    }

    [Fact]
    public async Task FounderMutationPosts_ResolveTheCanonicalAgentThenPersistEveryFounderOperation()
    {
        var previousFounder = Environment.GetEnvironmentVariable("FOUNDER_OID");
        var founderId = Guid.NewGuid().ToString();
        Environment.SetEnvironmentVariable("FOUNDER_OID", founderId);
        try
        {
            using var host = await BuildFounderHttpHostAsync(founderId);
            var client = host.GetTestClient();
            var tokenRequest = new HttpRequestMessage(HttpMethod.Get, "/__legend-connect-proof/token");
            tokenRequest.Headers.Add(FounderHeader, founderId);
            var tokenResponse = await client.SendAsync(tokenRequest);
            tokenResponse.EnsureSuccessStatusCode();
            var token = await tokenResponse.Content.ReadFromJsonAsync<AntiforgeryTokenDto>();
            Assert.NotNull(token);
            var cookie = ExtractAntiforgeryCookie(tokenResponse);

            await AssertFounderRedirectAsync(client, founderId, token!.RequestToken, cookie,
                "/founder/legend-connect/runtime-policy", new Dictionary<string, string>
                {
                    ["LearningEnabled"] = "true",
                    ["ContextualCompositionMode"] = "Shadow",
                    ["ContextualMinimumConfidence"] = "0.98"
                });
            await AssertFounderRedirectAsync(client, founderId, token.RequestToken, cookie,
                "/founder/legend-connect/knowledge", new Dictionary<string, string>
                {
                    ["SourceLanguageCode"] = "en",
                    ["SourceText"] = "Are you coming over for dinner tonight?",
                    ["ContextCategory"] = "Everyday conversation",
                    ["UsageRegister"] = "Plans"
                });
            await AssertFounderRedirectAsync(client, founderId, token.RequestToken, cookie,
                "/founder/legend-connect/knowledge", new Dictionary<string, string>
                {
                    ["SourceLanguageCode"] = "en",
                    ["SourceText"] = "Good evening",
                    ["TargetLanguageCode"] = "ht",
                    ["TargetText"] = "Bonswa",
                    ["ContextCategory"] = "Greeting"
                });
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var policy = scope.ServiceProvider.GetRequiredService<ILegendConnectRuntimePolicyAuthority>();
                await policy.RecordWorkerHeartbeatAsync("Learning");
                await policy.RecordWorkerHeartbeatAsync("Acquisition");
            }

            await AssertFounderRedirectAsync(client, founderId, token.RequestToken, cookie,
                "/founder/legend-connect/activate", new Dictionary<string, string>
                {
                    ["FocusEnabled"] = "true",
                    ["FocusLanguageCodes"] = "ht"
                });
            await AssertFounderRedirectAsync(client, founderId, token.RequestToken, cookie,
                "/founder/legend-connect/pause", new Dictionary<string, string>());
            await AssertFounderRedirectAsync(client, founderId, token.RequestToken, cookie,
                "/founder/legend-connect/entitlement", new Dictionary<string, string>
                {
                    ["TargetUserId"] = "active-paid-client",
                    ["TargetParticipantType"] = MessagingParticipantTypes.Client,
                    ["AccessGranted"] = "true",
                    ["EntitlementMode"] = "Custom",
                    ["CustomCharacterAllowance"] = "500"
                });

            await using var verificationScope = host.Services.CreateAsyncScope();
            var db = verificationScope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
            var runtime = await db.LegendConnectRuntimePolicies.AsNoTracking().SingleAsync();
            // Capacity values are deliberately absent from the Founder form.
            // This test host has no Azure capacity source, so the existing
            // test-only bootstrap default remains inert legacy data rather
            // than a user-editable authority.
            Assert.Equal(10_000, runtime.MonthlyProviderCapacityCharacters);
            Assert.False(runtime.CorpusAcquisitionEnabled);
            Assert.Contains(await db.LegendConnectAutonomousLanguageFocuses.AsNoTracking().ToListAsync(), focus =>
                focus.RuntimePolicyId == runtime.Id && focus.TargetLanguageCode == "ht");
            Assert.Contains(await db.LegendCorpusCandidates.AsNoTracking().ToListAsync(), candidate =>
                candidate.SourceText == "Are you coming over for dinner tonight?" && candidate.IsApproved);
            Assert.Contains(await db.LegendTranslationAlignments.AsNoTracking().ToListAsync(), alignment =>
                alignment.PairKey == "en:ht" && alignment.SupersededUtc == null);
            Assert.Contains(await db.LegendTranslationEntitlements.AsNoTracking().ToListAsync(), entitlement =>
                entitlement.UserId == "active-paid-client" && entitlement.MonthlyCharacterAllowance == 500);
            Assert.Contains(await db.ControlledResourceGrants.AsNoTracking().ToListAsync(), grant =>
                grant.UserId == "active-paid-client" && grant.ResourceType == ControlledResourceTypes.LanguageTranslation && grant.IsActive);

            var audits = await db.LegendConnectKnowledgeAuditEntries.AsNoTracking().ToListAsync();
            Assert.Contains(audits, entry => entry.FounderUserId == founderId && entry.Action == "RuntimeCompositionPolicyChanged");
            Assert.Contains(audits, entry => entry.FounderUserId == founderId && entry.Action == "FounderKnowledgeSubmitted");
            Assert.Contains(audits, entry => entry.FounderUserId == founderId && entry.Action == "FounderAutonomousLanguageFocusEnabled");
            Assert.Contains(audits, entry => entry.FounderUserId == founderId && entry.Action == "AutonomousAcquisitionActivated");
            Assert.Contains(audits, entry => entry.FounderUserId == founderId && entry.Action == "AutonomousAcquisitionPaused");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", previousFounder);
        }
    }

    [Fact]
    public async Task FounderProductionCompositionControl_PostsThroughTheCanonicalModeAuthorityAndReloadsServerState()
    {
        var previousFounder = Environment.GetEnvironmentVariable("FOUNDER_OID");
        var founderId = Guid.NewGuid().ToString();
        Environment.SetEnvironmentVariable("FOUNDER_OID", founderId);
        try
        {
            using var host = await BuildFounderHttpHostAsync(founderId);
            var client = host.GetTestClient();
            var tokenRequest = new HttpRequestMessage(HttpMethod.Get, "/__legend-connect-proof/token");
            tokenRequest.Headers.Add(FounderHeader, founderId);
            var tokenResponse = await client.SendAsync(tokenRequest);
            tokenResponse.EnsureSuccessStatusCode();
            var token = await tokenResponse.Content.ReadFromJsonAsync<AntiforgeryTokenDto>();
            Assert.NotNull(token);
            var cookie = ExtractAntiforgeryCookie(tokenResponse);

            await AssertFounderRedirectAsync(client, founderId, token!.RequestToken, cookie,
                "/founder/legend-connect/composition-mode", new Dictionary<string, string>
                {
                    ["ContextualCompositionMode"] = "Disabled"
                });
            await AssertFounderRedirectAsync(client, founderId, token.RequestToken, cookie,
                "/founder/legend-connect/composition-mode", new Dictionary<string, string>
                {
                    ["ContextualCompositionMode"] = "Active"
                });

            await using (var reloadScope = host.Services.CreateAsyncScope())
            {
                var runtime = reloadScope.ServiceProvider.GetRequiredService<ILegendConnectRuntimePolicyAuthority>();
                Assert.Equal("Active", (await runtime.GetEffectiveAsync()).ContextualCompositionMode);
            }

            await AssertFounderRedirectAsync(client, founderId, token.RequestToken, cookie,
                "/founder/legend-connect/composition-mode", new Dictionary<string, string>
                {
                    ["ContextualCompositionMode"] = "Shadow"
                });

            await using var verificationScope = host.Services.CreateAsyncScope();
            var db = verificationScope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
            var persisted = await db.LegendConnectRuntimePolicies.AsNoTracking().SingleAsync();
            Assert.Equal("Shadow", persisted.ContextualCompositionMode);
            Assert.Contains(await db.LegendConnectKnowledgeAuditEntries.AsNoTracking().ToListAsync(), entry =>
                entry.FounderUserId == founderId &&
                entry.Action == "ContextualCompositionModeChanged" &&
                entry.Detail!.Contains("context mode: Disabled → Active"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", previousFounder);
        }
    }

    [Fact]
    public async Task FounderActorResolution_UsesTheSharedCanonicalClaimAndFailsClosedWithoutAnAgentAccount()
    {
        var previousFounder = Environment.GetEnvironmentVariable("FOUNDER_OID");
        var founderId = Guid.NewGuid().ToString();
        Environment.SetEnvironmentVariable("FOUNDER_OID", founderId);
        try
        {
            await using var db = ControllerTestHelpers.BuildDb();
            await SeedFounderAgentAsync(db, founderId);
            var configuration = Configuration();
            var registry = new LegendLanguageRegistry(db, configuration);
            var service = Service(db, Operations(db, registry, configuration));
            var canonicalClaimPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("http://schemas.microsoft.com/identity/claims/objectidentifier", founderId)
            }, "TestAuth"));

            var result = await service.SubmitAsync(canonicalClaimPrincipal,
                new FounderLegendConnectKnowledgeInput
                {
                    SourceLanguageCode = "en",
                    SourceText = "Canonical Founder proof"
                });

            Assert.True(result.Succeeded);
            Assert.Contains(await db.LegendConnectKnowledgeAuditEntries.ToListAsync(), entry =>
                entry.FounderUserId == founderId && entry.Action == "FounderKnowledgeSubmitted");

            var unmappedFounder = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("oid", founderId),
                new Claim("founder", "true")
            }, "TestAuth"));
            await using var unmappedDb = ControllerTestHelpers.BuildDb();
            var unmappedService = Service(unmappedDb, Operations(
                unmappedDb,
                new LegendLanguageRegistry(unmappedDb, configuration),
                configuration));
            await Assert.ThrowsAsync<ForbidResultException>(() => unmappedService.SubmitAsync(
                unmappedFounder,
                new FounderLegendConnectKnowledgeInput { SourceLanguageCode = "en", SourceText = "Must not persist" }));

            var spoofed = ControllerTestHelpers.BuildUser(Guid.NewGuid().ToString());
            await Assert.ThrowsAsync<ForbidResultException>(() => service.SubmitAsync(
                spoofed,
                new FounderLegendConnectKnowledgeInput { SourceLanguageCode = "en", SourceText = "Spoofed claim" }));
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", previousFounder);
        }
    }

    [Fact]
    public async Task FounderPostRoutes_ChallengeUnauthenticatedAndForbidNonFounderPrincipals()
    {
        var previousFounder = Environment.GetEnvironmentVariable("FOUNDER_OID");
        var founderId = Guid.NewGuid().ToString();
        Environment.SetEnvironmentVariable("FOUNDER_OID", founderId);
        try
        {
            using var host = await BuildFounderHttpHostAsync(founderId);
            var client = host.GetTestClient();
            var tokenRequest = new HttpRequestMessage(HttpMethod.Get, "/__legend-connect-proof/token");
            tokenRequest.Headers.Add(FounderHeader, founderId);
            var tokenResponse = await client.SendAsync(tokenRequest);
            tokenResponse.EnsureSuccessStatusCode();
            var token = await tokenResponse.Content.ReadFromJsonAsync<AntiforgeryTokenDto>();
            Assert.NotNull(token);
            var cookie = ExtractAntiforgeryCookie(tokenResponse);

            using var nonFounder = new HttpRequestMessage(HttpMethod.Post, "/founder/legend-connect/composition-mode")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["ContextualCompositionMode"] = "Active"
                })
            };
            nonFounder.Headers.Add(FounderHeader, Guid.NewGuid().ToString());
            nonFounder.Headers.Add("RequestVerificationToken", token!.RequestToken);
            if (!string.IsNullOrWhiteSpace(cookie))
                nonFounder.Headers.Add("Cookie", cookie);
            using var nonFounderResponse = await client.SendAsync(nonFounder);
            Assert.Equal(HttpStatusCode.Forbidden, nonFounderResponse.StatusCode);

            using var unauthenticated = new HttpRequestMessage(HttpMethod.Post, "/founder/legend-connect/composition-mode")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["ContextualCompositionMode"] = "Active"
                })
            };
            unauthenticated.Headers.Add("RequestVerificationToken", token.RequestToken);
            if (!string.IsNullOrWhiteSpace(cookie))
                unauthenticated.Headers.Add("Cookie", cookie);
            using var unauthenticatedResponse = await client.SendAsync(unauthenticated);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticatedResponse.StatusCode);
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
            await SeedFounderAgentAsync(db, founderId);
            var service = Service(db, operations);
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
    public async Task FounderCorrection_RetiresOnlyItsDerivedActiveEvidenceAndReplaysWithoutResurrection()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var operations = Operations(db, registry, configuration);
        var first = await operations.SubmitFounderKnowledgeAsync("founder", new LegendConnectKnowledgeSubmission(
            "en", "A controlled source one.", "ht", "Yon sib kontwole youn.", "Training", null, null, "FounderApproved"));
        var unrelated = await operations.SubmitFounderKnowledgeAsync("founder", new LegendConnectKnowledgeSubmission(
            "en", "A controlled source two.", "ht", "Yon sib kontwole de.", "Training", null, null, "FounderApproved"));
        Assert.True(first.Succeeded, first.Message);
        Assert.True(unrelated.Succeeded, unrelated.Message);

        var firstSource = await db.LegendLanguageTextUnits.SingleAsync(item => item.Id == first.SourceTextUnitId);
        var firstTarget = await db.LegendLanguageTextUnits.SingleAsync(item => item.Id == first.TargetTextUnitId);
        var unrelatedSource = await db.LegendLanguageTextUnits.SingleAsync(item => item.Id == unrelated.SourceTextUnitId);
        var unrelatedTarget = await db.LegendLanguageTextUnits.SingleAsync(item => item.Id == unrelated.TargetTextUnitId);
        var family = new LegendCurriculumFamily { Id = Guid.NewGuid(), FamilyKey = "correction.lineage", Provenance = "FounderApproved" };
        var firstSourceExample = new LegendCurriculumExample
        {
            Id = Guid.NewGuid(), CurriculumFamilyId = family.Id, TextUnitId = firstSource.Id,
            LanguageCode = "en", Provenance = "FounderApproved"
        };
        var firstTargetExample = new LegendCurriculumExample
        {
            Id = Guid.NewGuid(), CurriculumFamilyId = family.Id, TextUnitId = firstTarget.Id,
            LanguageCode = "ht", Provenance = "FounderApproved", DerivedFromCurriculumExampleId = firstSourceExample.Id
        };
        var unrelatedSourceExample = new LegendCurriculumExample
        {
            Id = Guid.NewGuid(), CurriculumFamilyId = family.Id, TextUnitId = unrelatedSource.Id,
            LanguageCode = "en", Provenance = "FounderApproved"
        };
        var unrelatedTargetExample = new LegendCurriculumExample
        {
            Id = Guid.NewGuid(), CurriculumFamilyId = family.Id, TextUnitId = unrelatedTarget.Id,
            LanguageCode = "ht", Provenance = "FounderApproved", DerivedFromCurriculumExampleId = unrelatedSourceExample.Id
        };
        var pattern = new LegendLanguageStructuralPattern
        {
            Id = Guid.NewGuid(), CurriculumFamilyId = family.Id, PairKey = "en:ht", LanguageCode = "ht",
            PropositionSignature = "correction-proposition", VariationDimension = "controlled", RealizationSignature = "controlled",
            SupportCount = 2, IndependentSourceCount = 2, HumanVerifiedSupportCount = 2, Confidence = 1m,
            MaturityState = "Candidate", Provenance = "FounderApproved"
        };
        var relationship = new LegendLanguageStructuralRelationship
        {
            Id = Guid.NewGuid(), PairKey = "en:ht", LanguageCode = "ht", VariationDimension = "controlled",
            RelationshipSignature = "correction-relationship", AnchorLayoutSignature = "controlled:0:1",
            SupportCount = 2, IndependentSourceCount = 2, HumanVerifiedSupportCount = 2, Confidence = 1m,
            MaturityState = "Candidate", Provenance = "FounderApproved"
        };
        var retiredEvidence = StructuralEvidence(pattern, relationship, family, firstTargetExample, "first-source");
        var activeUnrelatedEvidence = StructuralEvidence(pattern, relationship, family, unrelatedTargetExample, "unrelated-source");
        var retiredAnchor = new LegendLanguageCompositionalAnchor
        {
            Id = Guid.NewGuid(), LanguageCode = "ht", TextUnitId = firstTarget.Id, CurriculumFamilyId = family.Id,
            CurriculumExampleId = firstTargetExample.Id, Dimension = "controlled", Value = "before",
            SemanticSignature = "correction-semantic", AnchorSignature = "correction-anchor", Provenance = "FounderApproved"
        };
        var controlledContext = new LegendLanguageContextRelationship
        {
            Id = Guid.NewGuid(), PairKey = "en:ht", SourceTextUnitId = firstTarget.Id, RelatedTextUnitId = unrelatedTarget.Id,
            RelationshipKind = "ControlledVariation", ContextSignature = "correction-controlled", SourcePatternSignature = "correction-controlled",
            Confidence = 1m, QualityState = "Verified", Provenance = "FounderApproved", ObservationCount = 1
        };
        db.AddRange(family, firstSourceExample, firstTargetExample, unrelatedSourceExample, unrelatedTargetExample,
            pattern, relationship, retiredEvidence, activeUnrelatedEvidence, retiredAnchor, controlledContext);
        await db.SaveChangesAsync();
        var directContext = await db.LegendLanguageContextRelationships.SingleAsync(item =>
            item.PairKey == "en:ht" && item.SourceTextUnitId == firstSource.Id && item.RelatedTextUnitId == firstTarget.Id);

        var correction = await operations.CorrectFounderKnowledgeAsync("founder", first.AlignmentId!.Value,
            new LegendConnectKnowledgeSubmission(
                "en", firstSource.Text, "ht", "Yon sib kontwole korije.", "Training", null, null, "FounderApproved"));

        Assert.True(correction.Succeeded, correction.Message);
        Assert.NotNull((await db.LegendTranslationAlignments.SingleAsync(item => item.Id == first.AlignmentId)).SupersededUtc);
        Assert.NotNull((await db.LegendCurriculumExamples.SingleAsync(item => item.Id == firstTargetExample.Id)).SupersededUtc);
        Assert.NotNull((await db.LegendLanguageStructuralEvidence.SingleAsync(item => item.Id == retiredEvidence.Id)).SupersededUtc);
        Assert.NotNull((await db.LegendLanguageCompositionalAnchors.SingleAsync(item => item.Id == retiredAnchor.Id)).SupersededUtc);
        Assert.Null((await db.LegendLanguageStructuralEvidence.SingleAsync(item => item.Id == activeUnrelatedEvidence.Id)).SupersededUtc);
        Assert.NotNull((await db.LegendLanguageContextRelationships.SingleAsync(item => item.Id == directContext.Id)).SupersededUtc);
        Assert.NotNull((await db.LegendLanguageContextRelationships.SingleAsync(item => item.Id == controlledContext.Id)).SupersededUtc);
        Assert.Null((await db.LegendLanguageContextRelationships.SingleAsync(item => item.PairKey == "en:ht" &&
            item.SourceTextUnitId == unrelatedSource.Id && item.RelatedTextUnitId == unrelatedTarget.Id)).SupersededUtc);

        var refreshedPattern = await db.LegendLanguageStructuralPatterns.SingleAsync(item => item.Id == pattern.Id);
        var refreshedRelationship = await db.LegendLanguageStructuralRelationships.SingleAsync(item => item.Id == relationship.Id);
        Assert.Equal(1, refreshedPattern.SupportCount);
        Assert.Equal("Observation", refreshedPattern.MaturityState);
        Assert.Equal(1, refreshedRelationship.SupportCount);
        Assert.Equal("Observation", refreshedRelationship.MaturityState);
        Assert.Equal(2, (await db.LegendLanguagePairs.SingleAsync(item => item.PairKey == "en:ht")).CorpusCoverage);

        var beforeReplay = new
        {
            Examples = await db.LegendCurriculumExamples.CountAsync(),
            Evidence = await db.LegendLanguageStructuralEvidence.CountAsync(),
            ActiveEvidence = await db.LegendLanguageStructuralEvidence.CountAsync(item => item.SupersededUtc == null),
            Contexts = await db.LegendLanguageContextRelationships.CountAsync()
        };
        var curriculum = new LegendConnectCurriculumService(
            db, registry, new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance));
        await curriculum.ReevaluateHistoricalAlignmentsAsync(100);
        await curriculum.ReevaluateHistoricalAlignmentsAsync(100);
        var afterReplay = new
        {
            Examples = await db.LegendCurriculumExamples.CountAsync(),
            Evidence = await db.LegendLanguageStructuralEvidence.CountAsync(),
            ActiveEvidence = await db.LegendLanguageStructuralEvidence.CountAsync(item => item.SupersededUtc == null),
            Contexts = await db.LegendLanguageContextRelationships.CountAsync()
        };
        Assert.Equal(beforeReplay, afterReplay);
        Assert.NotNull((await db.LegendLanguageStructuralEvidence.SingleAsync(item => item.Id == retiredEvidence.Id)).SupersededUtc);
        Assert.Null(await curriculum.TryComposeAsync("en", "ht", "A future uncontrolled sentence."));
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
            await SeedFounderAgentAsync(db, founderId);
            var controller = Controller(Service(db, Operations(db, registry, configuration)),
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

    private static LegendLanguageStructuralEvidence StructuralEvidence(
        LegendLanguageStructuralPattern pattern,
        LegendLanguageStructuralRelationship relationship,
        LegendCurriculumFamily family,
        LegendCurriculumExample example,
        string independentSourceIdentity) => new()
    {
        Id = Guid.NewGuid(), StructuralPatternId = pattern.Id, StructuralRelationshipId = relationship.Id,
        StructuralRelationshipContributionState = "Supported", CurriculumFamilyId = family.Id, PairKey = "en:ht",
        LanguageCode = "ht", VariationDimension = "controlled", BaselineCurriculumExampleId = example.Id,
        ComparedCurriculumExampleId = example.Id, BaselineVariationValue = "before", ComparedVariationValue = "after",
        EvidenceSignature = "evidence:" + independentSourceIdentity, BaselineComponentSignature = "component:before",
        ComparedComponentSignature = "component:after", IndependentSourceIdentity = independentSourceIdentity,
        ContributionState = "Supported", IsHumanVerifiedSupport = true, Provenance = "FounderApproved"
    };

    private static FounderLegendConnectService Service(
        MasterAppDbContext db,
        ILegendConnectOperations operations) =>
        new(operations, new AgentProfileAccessResolver(db));

    private static async Task SeedFounderAgentAsync(MasterAppDbContext db, string founderId)
    {
        db.AgentProfiles.Add(new AgentProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = founderId,
            AgentUpn = "founder@example.test",
            NormalizedEmail = "founder@example.test",
            IsActive = true
        });
        await db.SaveChangesAsync();
    }

    private static async Task AssertFounderRedirectAsync(
        HttpClient client,
        string founderId,
        string requestToken,
        string antiforgeryCookie,
        string route,
        IReadOnlyDictionary<string, string> fields)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        request.Headers.Add(FounderHeader, founderId);
        request.Headers.Add("RequestVerificationToken", requestToken);
        if (!string.IsNullOrWhiteSpace(antiforgeryCookie))
            request.Headers.Add("Cookie", antiforgeryCookie);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/founder/legend-connect", response.Headers.Location?.ToString());
    }

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

    [Fact]
    public async Task MetricDetails_ReturnsTheActualUsageLedgerRowsInsteadOfDashboardCards()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var operations = new LegendConnectOperations(db, registry, corpus, configuration);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        db.LegendTranslationSystemUsages.Add(new LegendTranslationSystemUsage
        {
            Id = Guid.NewGuid(),
            UsageDate = today,
            ProviderOperationCount = 2,
            ProviderBillableCharacters = 42,
            QuotaDeniedRequestCount = 1,
            UpdatedUtc = DateTime.UtcNow
        });
        db.LegendTranslationUsageLedgers.AddRange(
            new LegendTranslationUsageLedger
            {
                Id = Guid.NewGuid(), RequestReference = "safe-billable-reference", UserId = "client-a", ParticipantType = "Client",
                PeriodStart = new DateOnly(today.Year, today.Month, 1), SourceLanguageCode = "en", TargetLanguageCode = "ht",
                Provider = "AzureTranslator", BillableCharacters = 42, ProviderExecuted = true, Succeeded = true,
                State = "Completed", CreatedUtc = DateTime.UtcNow, CompletedUtc = DateTime.UtcNow
            },
            new LegendTranslationUsageLedger
            {
                Id = Guid.NewGuid(), RequestReference = "safe-denied-reference", UserId = "client-b", ParticipantType = "Client",
                PeriodStart = new DateOnly(today.Year, today.Month, 1), SourceLanguageCode = "en", TargetLanguageCode = "fr",
                Provider = "AzureTranslator", BillableCharacters = 18, ProviderExecuted = false, Succeeded = false,
                State = "QuotaDenied", FailureCode = "translation_quota_exhausted", CreatedUtc = DateTime.UtcNow, CompletedUtc = DateTime.UtcNow
            });
        await db.SaveChangesAsync();

        var billable = await operations.GetMetricDetailAsync("provider-billable-characters");
        var denied = await operations.GetMetricDetailAsync("quota-denied");

        Assert.Equal("Provider-billable characters", billable.Title);
        Assert.Contains(billable.Sections.SelectMany(section => section.Rows), row => row.Contains("safe-billable-reference") && row.Contains("42"));
        Assert.Equal("Quota denied", denied.Title);
        Assert.Contains(denied.Sections.SelectMany(section => section.Rows), row => row.Contains("safe-denied-reference") && row.Contains("translation_quota_exhausted"));
        Assert.DoesNotContain(denied.Sections, section => section.Title == "Current section summary");
    }

    [Fact]
    public async Task MetricDetails_ResolvesEveryDashboardMetricKeyThroughTheOneOperationsFacade()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var operations = new LegendConnectOperations(
            db,
            registry,
            new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance),
            configuration);
        var keys = new[]
        {
            "active-languages", "directional-pairs", "learning-failures", "duplicate-prevention",
            "approved-candidates", "eligible-pending", "rejected-ineligible", "readiness-duplicates-prevented", "pairs-awaiting-knowledge",
            "same-language-bypasses", "translation-memory-hits", "provider-fallback-required", "trusted-contextual-served", "provider-avoidance", "provider-dependency",
            "azure-characters-used", "consumed-live-characters", "consumed-corpus-characters", "provider-characters-reserved", "pending-learning-jobs",
            "quality-needs-review", "quality-provider-observations", "quality-supported-observations", "quality-contradictions", "quality-human-verified",
            "provider-operations", "provider-billable-characters", "same-language-avoided", "memory-avoided", "context-avoided", "quota-denied", "provider-failures", "group-target-reuse", "high-consumption-accounts",
            "consented-accounts", "eligible-live-translations", "promoted-to-learning", "canonical-reuse-prevented-duplicates", "awaiting-corpus-processing",
            "raw-submissions-retained", "atomic-learning-units", "active-directional-alignments", "legacy-multi-unit-assets-retired",
            "capacity-monthly-limit", "capacity-monthly-consumed", "capacity-monthly-reserved", "capacity-monthly-remaining", "capacity-monthly-reserve", "capacity-monthly-corpus",
            "capacity-hourly-limit", "capacity-hourly-consumed", "capacity-hourly-remaining", "capacity-safe"
        };

        foreach (var key in keys)
        {
            var detail = await operations.GetMetricDetailAsync(key);
            Assert.Equal(key, detail.MetricKey);
            Assert.NotEmpty(detail.Sections);
        }
    }

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

    private static async Task<IHost> BuildFounderHttpHostAsync(string founderId)
    {
        var databaseName = "legend-connect-founder-http-" + Guid.NewGuid().ToString("N");
        var connection = new SqliteConnection($"Data Source=file:{databaseName}?mode=memory&cache=shared");
        await connection.OpenAsync();
        var host = await new HostBuilder()
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
                    services.AddSingleton(connection);
                    services.AddDbContext<MasterAppDbContext>(options => options.UseSqlite(connection));
                    services.AddSingleton<IConfiguration>(Configuration());
                    services.AddScoped<ILegendLanguageRegistry, LegendLanguageRegistry>();
                    services.AddScoped<LegendConnectCorpusService>();
                    services.AddScoped<ILegendConnectOperations, LegendConnectOperations>();
                    services.AddScoped<AgentProfileAccessResolver>();
                    services.AddScoped<IControlledResourceAccessService, ControlledResourceAccessService>();
                    services.AddScoped<ITranslationEntitlementAuthority, TranslationEntitlementAuthority>();
                    services.AddScoped<ILegendConnectRuntimePolicyAuthority, LegendConnectRuntimePolicyAuthority>();
                    services.AddScoped<ICommunityTextModerationService, CommunityTextModerationService>();
                    services.AddScoped<IMessagingProfileImageResolver, MessagingProfileImageResolver>();
                    services.AddSingleton<INotificationRealtimePublisher, NoopNotificationRealtimePublisher>();
                    services.AddSingleton<IApplePushDeliverySignal, ApplePushDeliverySignal>();
                    services.AddScoped<INotificationEngine>(serviceProvider => new NotificationEngine(
                        serviceProvider.GetRequiredService<MasterAppDbContext>(),
                        serviceProvider.GetRequiredService<IMessagingProfileImageResolver>(),
                        serviceProvider.GetRequiredService<INotificationRealtimePublisher>(),
                        serviceProvider.GetRequiredService<IApplePushDeliverySignal>(),
                        NullLogger<NotificationEngine>.Instance));
                    services.AddScoped<ITranslationService, TestTranslationService>();
                    services.AddScoped<IMessagingService, MessagingService>();
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

        host.Services.GetRequiredService<IHostApplicationLifetime>()
            .ApplicationStopping.Register(connection.Dispose);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        await db.Database.EnsureCreatedAsync();
        await SeedFounderAgentAsync(db, founderId);
        var activeClient = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "active-paid-client",
            ExternalIdentityObjectId = "active-paid-client",
            FirstName = "Active",
            LastName = "Client",
            Email = "active-paid-client@example.test",
            NormalizedEmail = "active-paid-client@example.test",
            CrmStatus = "Active",
            CrmNotes = "{\"recordType\":\"Client\",\"pipelineStage\":\"Client\"}"
        };
        db.ClientProfiles.Add(activeClient);
        var acceptedOffer = new ClientSubscriptionOffer
        {
            Id = Guid.NewGuid(),
            ClientProfileId = activeClient.Id,
            OwnerAgentUserId = founderId,
            MonthlyAmountCents = 1,
            Currency = "USD",
            Status = ClientSubscriptionOfferStatus.Accepted
        };
        db.ClientSubscriptionOffers.Add(acceptedOffer);
        db.ClientSubscriptions.Add(new ClientSubscription
        {
            Id = Guid.NewGuid(),
            ClientProfileId = activeClient.Id,
            AcceptedOfferId = acceptedOffer.Id,
            OwnerAgentUserId = founderId,
            Status = ClientSubscriptionStatus.Active,
            PaymentStanding = ClientSubscriptionPaymentStanding.Current,
            MonthlyAmountCents = 1,
            Currency = "USD"
        });
        db.ClientEntitlements.Add(new ClientEntitlement
        {
            Id = Guid.NewGuid(),
            ClientProfileId = activeClient.Id,
            EntitlementKey = BillingEntitlementKeys.ClientAppFullAccess,
            Status = ClientEntitlementStatus.Active,
            SourceType = ClientEntitlementSourceType.Subscription,
            SourceId = "legend-connect-founder-proof",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return host;
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

    private sealed class TestTranslationService : ITranslationService
    {
        public Task<TranslationDetectionResult> DetectLanguageAsync(
            string text,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranslationDetectionResult(true, "en"));

        public Task<TranslationProviderResult> TranslateAsync(
            string text,
            string targetLanguage,
            string? sourceLanguage = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranslationProviderResult(true, text, sourceLanguage, "TestTranslator"));
    }

    private sealed class NoopNotificationRealtimePublisher : INotificationRealtimePublisher
    {
        public Task PublishAsync(
            MessagingActor recipient,
            NotificationRealtimeEvent notification,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
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
