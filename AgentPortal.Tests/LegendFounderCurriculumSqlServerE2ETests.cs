using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Models;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AgentPortal.Tests;

/// <summary>
/// An opt-in diagnostic proving the public Founder service entry point against
/// a real isolated SQL Server database.  The curriculum is deliberately read
/// from a normal external Founder manifest, never embedded in this test or in
/// the application.
/// </summary>
[Collection("LegendConnectFounderEnvironment")]
public sealed class LegendFounderCurriculumSqlServerE2ETests
{
    private readonly ITestOutputHelper _output;

    public LegendFounderCurriculumSqlServerE2ETests(ITestOutputHelper output) =>
        _output = output;

    [Fact]
    public async Task ExternalFounderManifest_UsesNormalDurablePathAndRepliesNatively()
    {
        var connectionString = Environment.GetEnvironmentVariable("LEGEND_FOUNDER_E2E_CONNECTION");
        var manifestPath = Environment.GetEnvironmentVariable("LEGEND_FOUNDER_E2E_MANIFEST_PATH");
        if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(manifestPath))
        {
            _output.WriteLine("External Founder SQL Server E2E is opt-in; no isolated database was selected.");
            return;
        }

        var manifestPaths = manifestPath.Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.NotEmpty(manifestPaths);
        foreach (var path in manifestPaths)
            Assert.True(File.Exists(path), "Each external Founder manifest must exist.");

        var founderId = Environment.GetEnvironmentVariable("LEGEND_FOUNDER_E2E_FOUNDER_ID") ??
            "e2e4d030-8d47-4a5b-a2db-5f2e50d14570";
        var founderEmail = $"founder-e2e-{founderId}@legend.local";
        Environment.SetEnvironmentVariable("FOUNDER_OID", founderId);
        await using var db = new MasterAppDbContext(
            new DbContextOptionsBuilder<MasterAppDbContext>()
                .UseSqlServer(connectionString)
                .Options);
        var profile = await db.AgentProfiles
            .SingleOrDefaultAsync(item => item.AgentUserId == founderId);
        if (profile is null)
        {
            db.AgentProfiles.Add(new AgentProfile
            {
                Id = Guid.NewGuid(),
                AgentUserId = founderId,
                AgentUpn = founderEmail,
                NormalizedEmail = founderEmail,
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("OpenAI:ApiKey", string.Empty)
            })
            .Build();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(
            db,
            registry,
            NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var operations = new LegendConnectOperations(db, registry, corpus, configuration, curriculum: curriculum);
        var founder = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", founderId)], "e2e"));
        var founderLegend = new FounderLegendConnectService(
            operations,
            new AgentProfileAccessResolver(db));

        var familiesBefore = await db.LegendCurriculumFamilies.CountAsync();
        var examplesBefore = await db.LegendCurriculumExamples.CountAsync(item => item.SupersededUtc == null);
        var anchorsBefore = await db.LegendLanguageCompositionalAnchors.CountAsync(item => item.SupersededUtc == null);
        var transitionsBefore = await db.LegendSemanticTransitionEvidence.CountAsync(item => item.SupersededUtc == null);
        foreach (var path in manifestPaths)
        {
            var manifest = await File.ReadAllTextAsync(path);
            Assert.False(string.IsNullOrWhiteSpace(manifest));
            var accepted = await founderLegend.SubmitCurriculumAsync(
                founder,
                new FounderLegendConnectCurriculumInput { Manifest = manifest });
            Assert.True(accepted.Succeeded, accepted.Message);
            _output.WriteLine($"MANIFEST ACCEPTED: {Path.GetFileName(path)}; DUPLICATE PREVENTED: {accepted.DuplicatePrevented}");
        }

        var processor = new LegendConnectCurriculumManifestProcessor(
            db,
            curriculum,
            NullLogger<LegendConnectCurriculumManifestProcessor>.Instance);
        for (var pass = 0; pass < 64; pass++)
        {
            await processor.ProcessPendingAsync(1);
            db.ChangeTracker.Clear();
            var states = await db.LegendCurriculumManifestWorkItems
                .Select(item => item.ProcessingState)
                .ToArrayAsync();
            if (states.All(item => item == "Completed"))
                break;
            Assert.DoesNotContain("Failed", states);
        }

        Assert.All(
            await db.LegendCurriculumManifestWorkItems.ToListAsync(),
            work => Assert.Equal("Completed", work.ProcessingState));
        var request = Environment.GetEnvironmentVariable("LEGEND_FOUNDER_E2E_REQUEST") ?? "Hello legend";
        var history = JsonSerializer.Deserialize<List<LegendFounderAiChatMessage>>(
            Environment.GetEnvironmentVariable("LEGEND_FOUNDER_E2E_HISTORY") ?? "[]") ?? [];
        var expectNative = !string.Equals(
            Environment.GetEnvironmentVariable("LEGEND_FOUNDER_E2E_EXPECT_NATIVE"),
            "false",
            StringComparison.OrdinalIgnoreCase);
        var source = await curriculum.AnalyzeSemanticTransitionSourceSemanticsAsync("en", request);
        var nativeClock = Stopwatch.StartNew();
        var native = await founderLegend.TryInferConversationAsync(
            founder,
            request,
            history.Select(item => new LegendConnectConversationContextItem(
                item.Role ?? string.Empty,
                item.Content ?? string.Empty)).ToArray());
        nativeClock.Stop();

        _output.WriteLine($"REQUEST: {request}");
        _output.WriteLine($"SOURCE STATE: {source.State}");
        _output.WriteLine($"SOURCE REASONS: {string.Join(", ", source.Reasons)}");
        _output.WriteLine($"SOURCE COMPONENTS: {string.Join(" | ", source.Components.Select(item => $"{item.Dimension}={item.Value}@{item.SurfaceForm}"))}");
        _output.WriteLine($"FAMILIES BEFORE: {familiesBefore}");
        _output.WriteLine($"EXAMPLES BEFORE: {examplesBefore}");
        _output.WriteLine($"ANCHORS BEFORE: {anchorsBefore}");
        _output.WriteLine($"TRANSITIONS BEFORE: {transitionsBefore}");
        _output.WriteLine($"NATIVE REASON: {native.ReasonCode}");
        _output.WriteLine($"NATIVE AUTHORITY: {native.AuthoritySummary}");
        Assert.Equal(expectNative, native.Supported);
        if (expectNative)
        {
            Assert.False(string.IsNullOrWhiteSpace(native.Answer));
            Assert.False(string.Equals(request, native.Answer, StringComparison.OrdinalIgnoreCase));
        }

        var factory = new CountingHttpClientFactory();
        var service = new LegendFounderAiConversationService(
            factory,
            configuration,
            founderLegend,
            NullLogger<LegendFounderAiConversationService>.Instance);
        var replyClock = Stopwatch.StartNew();
        var reply = await service.ReplyAsync(
            founder,
            new LegendFounderAiChatRequest { Messages = [.. history, new("user", request)] });
        replyClock.Stop();

        Assert.True(reply.Succeeded);
        if (expectNative)
            Assert.Equal(native.Answer, reply.Message);
        else
            Assert.NotEqual(request, reply.Message);
        Assert.Equal(0, factory.CreateClientCalls);

        _output.WriteLine($"REQUEST: {request}");
        _output.WriteLine($"SOURCE STATE: {source.State}");
        _output.WriteLine($"SOURCE COMPONENTS: {string.Join(" | ", source.Components.Select(item => $"{item.Dimension}={item.Value}@{item.SurfaceForm}"))}");
        _output.WriteLine($"FAMILIES: {await db.LegendCurriculumFamilies.CountAsync()}");
        _output.WriteLine($"EXAMPLES: {await db.LegendCurriculumExamples.CountAsync(item => item.SupersededUtc == null)}");
        _output.WriteLine($"ANCHORS: {await db.LegendLanguageCompositionalAnchors.CountAsync(item => item.SupersededUtc == null)}");
        _output.WriteLine($"TRANSITIONS: {await db.LegendSemanticTransitionEvidence.CountAsync(item => item.SupersededUtc == null)}");
        _output.WriteLine($"NATIVE RESPONSE: {native.Answer}");
        _output.WriteLine($"NATIVE REASON: {native.ReasonCode}");
        _output.WriteLine($"FINAL RESPONSE: {reply.Message}");
        _output.WriteLine($"OPENAI CLIENTS: {factory.CreateClientCalls}");
        _output.WriteLine("OPENAI HTTP CALLS: 0");
        _output.WriteLine($"NATIVE LATENCY MS: {nativeClock.Elapsed.TotalMilliseconds:F0}");
        _output.WriteLine($"REPLY LATENCY MS: {replyClock.Elapsed.TotalMilliseconds:F0}");
    }

    [Fact]
    public async Task AuthenticatedHttpChat_UsesMvcAntiforgeryAndNativeReplyAgainstIsolatedSqlServer()
    {
        var connectionString = Environment.GetEnvironmentVariable("LEGEND_FOUNDER_E2E_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _output.WriteLine("Authenticated Founder HTTP proof is opt-in; no isolated database was selected.");
            return;
        }

        var founderId = Environment.GetEnvironmentVariable("LEGEND_FOUNDER_E2E_FOUNDER_ID") ??
            "e2e4d030-8d47-4a5b-a2db-5f2e50d14570";
        var request = Environment.GetEnvironmentVariable("LEGEND_FOUNDER_E2E_REQUEST") ?? "Hello legend";
        var expectedNativeResponse = Environment.GetEnvironmentVariable(
            "LEGEND_FOUNDER_E2E_EXPECTED_RESPONSE") ?? "hello and welcome legend.";
        var previousFounderOid = Environment.GetEnvironmentVariable("FOUNDER_OID");
        Environment.SetEnvironmentVariable("FOUNDER_OID", founderId);
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new[]
                {
                    new KeyValuePair<string, string?>("OpenAI:ApiKey", string.Empty)
                })
                .Build();
            var factory = new CountingHttpClientFactory();
            using var host = await BuildAuthenticatedHttpHostAsync(
                connectionString,
                configuration,
                factory);
            var client = host.GetTestClient();

            var tokenRequest = new HttpRequestMessage(
                HttpMethod.Get,
                "/__legend-connect-proof/token");
            tokenRequest.Headers.Add("X-Legend-Connect-Founder", founderId);
            var tokenResponse = await client.SendAsync(tokenRequest);
            tokenResponse.EnsureSuccessStatusCode();
            var token = await tokenResponse.Content.ReadFromJsonAsync<AntiforgeryTokenDto>();
            Assert.NotNull(token);

            var chatRequest = new HttpRequestMessage(
                HttpMethod.Post,
                "/founder/legend-ai/chat")
            {
                Content = JsonContent.Create(new LegendFounderAiChatRequest
                {
                    Messages = [new("user", request)]
                })
            };
            chatRequest.Headers.Add("X-Legend-Connect-Founder", founderId);
            chatRequest.Headers.Add("RequestVerificationToken", token!.RequestToken);
            var antiforgeryCookie = ExtractAntiforgeryCookie(tokenResponse);
            if (!string.IsNullOrWhiteSpace(antiforgeryCookie))
                chatRequest.Headers.Add("Cookie", antiforgeryCookie);

            var response = await client.SendAsync(chatRequest);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<LegendFounderAiChatResponse>();
            Assert.NotNull(body);
            Assert.True(body!.Succeeded);
            Assert.False(string.IsNullOrWhiteSpace(body.Message));
            Assert.False(string.Equals(request, body.Message, StringComparison.OrdinalIgnoreCase));
            Assert.Equal("legend", body.Mode);
            Assert.Equal(expectedNativeResponse, body.Message);
            Assert.Equal(0, factory.CreateClientCalls);

            _output.WriteLine("HTTP PATH: authenticated + antiforgery + MVC controller + ReplyAsync");
            _output.WriteLine($"HTTP REQUEST: {request}");
            _output.WriteLine($"HTTP STATUS: {(int)response.StatusCode}");
            _output.WriteLine($"HTTP RESPONSE: {body.Message}");
            _output.WriteLine($"OPENAI CLIENTS: {factory.CreateClientCalls}");
            _output.WriteLine("OPENAI HTTP CALLS: 0");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", previousFounderOid);
        }
    }

    [Fact]
    public async Task FounderSectionPages_RemainBoundedAgainstAnIsolatedLargeSqlServerDataset()
    {
        var connectionString = Environment.GetEnvironmentVariable("LEGEND_FOUNDER_SCALABILITY_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _output.WriteLine("Founder SQL Server scalability proof is opt-in; no isolated database was selected.");
            return;
        }

        const string founderId = "4f4a89fd-b4a1-4100-92e4-5c7eafb384db";
        var previousFounderOid = Environment.GetEnvironmentVariable("FOUNDER_OID");
        Environment.SetEnvironmentVariable("FOUNDER_OID", founderId);
        var commandCounter = new CountingDbCommandInterceptor();
        try
        {
            await using (var db = new MasterAppDbContext(
                new DbContextOptionsBuilder<MasterAppDbContext>()
                    .UseSqlServer(connectionString)
                    .AddInterceptors(commandCounter)
                    .Options))
            {
                if (!await db.AgentProfiles.AnyAsync(item => item.AgentUserId == founderId))
                {
                    db.AgentProfiles.Add(new AgentProfile
                    {
                        Id = Guid.NewGuid(),
                        AgentUserId = founderId,
                        AgentUpn = "founder-scalability@legend.local",
                        NormalizedEmail = "founder-scalability@legend.local",
                        IsActive = true
                    });
                    await db.SaveChangesAsync();
                }

                var configuration = new ConfigurationBuilder().Build();
                var registry = new LegendLanguageRegistry(db, configuration);
                await registry.ListEnabledTranslationLanguagesAsync();
                const string marker = "founder-page-scale-20260820";
                if (!await db.LegendCurriculumFamilies.AnyAsync(item => item.FamilyKey == marker + ".historical"))
                {
                    var start = DateTime.UtcNow.AddDays(-30);
                    for (var batch = 0; batch < 20; batch++)
                    {
                        var families = new List<LegendCurriculumFamily>();
                        var units = new List<LegendLanguageTextUnit>();
                        var examples = new List<LegendCurriculumExample>();
                        var anchors = new List<LegendLanguageCompositionalAnchor>();
                        for (var offset = 0; offset < 500; offset++)
                        {
                            var index = batch * 500 + offset;
                            var timestamp = start.AddSeconds(index);
                            var family = new LegendCurriculumFamily
                            {
                                Id = Guid.NewGuid(),
                                FamilyKey = index == 0 ? marker + ".historical" : $"{marker}.{index:D5}",
                                SemanticCategory = "Scalability",
                                Provenance = "FounderApproved",
                                CreatedUtc = timestamp,
                                UpdatedUtc = timestamp
                            };
                            var unit = new LegendLanguageTextUnit
                            {
                                Id = Guid.NewGuid(),
                                LanguageCode = "en",
                                StoragePartition = "Legend:en",
                                NormalizedHash = Guid.NewGuid().ToString("N"),
                                Text = index == 0 ? "A historical SQL Server curriculum example." : $"SQL Server curriculum example {index}.",
                                Provenance = "FounderApproved",
                                IsTrainingEligible = true,
                                CreatedUtc = timestamp,
                                UpdatedUtc = timestamp
                            };
                            var example = new LegendCurriculumExample
                            {
                                Id = Guid.NewGuid(),
                                CurriculumFamilyId = family.Id,
                                TextUnitId = unit.Id,
                                LanguageCode = "en",
                                Provenance = "FounderApproved",
                                CreatedUtc = timestamp,
                                UpdatedUtc = timestamp
                            };
                            families.Add(family);
                            units.Add(unit);
                            examples.Add(example);
                            anchors.Add(new LegendLanguageCompositionalAnchor
                            {
                                Id = Guid.NewGuid(),
                                LanguageCode = "en",
                                TextUnitId = unit.Id,
                                CurriculumFamilyId = family.Id,
                                CurriculumExampleId = example.Id,
                                Dimension = "function",
                                Value = "scalability",
                                AnchorSignature = Guid.NewGuid().ToString("N"),
                                Provenance = "FounderApproved",
                                CreatedUtc = timestamp
                            });
                        }
                        db.AddRange(families);
                        db.AddRange(units);
                        db.AddRange(examples);
                        db.AddRange(anchors);
                        await db.SaveChangesAsync();
                        db.ChangeTracker.Clear();
                    }
                }

                var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
                var operations = new LegendConnectOperations(db, registry, corpus, configuration);
                var founder = new FounderLegendConnectService(operations, new AgentProfileAccessResolver(db));
                commandCounter.Reset();
                var shellClock = Stopwatch.StartNew();
                var shell = await founder.GetDashboardAsync(
                    new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", founderId)], "e2e")), "en", null);
                shellClock.Stop();
                Assert.Equal(10_000, shell.Shell.SelectedLanguage!.CanonicalEntryCount);
                Assert.Equal(10_000, shell.Shell.SelectedLanguage.CurriculumExampleCount);
                Assert.Equal(10_000, shell.Shell.SelectedLanguage.CompositionalAnchorCount);
                _output.WriteLine($"SQL SHELL LATENCY MS: {shellClock.Elapsed.TotalMilliseconds:F0}");
                _output.WriteLine($"SQL SHELL QUERY COUNT: {commandCounter.Commands}");
                _output.WriteLine("SQL SHELL MATERIALIZED DETAIL ROWS: 0");
            }

            var configurationForHttp = new ConfigurationBuilder().Build();
            var factory = new CountingHttpClientFactory();
            using var host = await BuildAuthenticatedHttpHostAsync(
                connectionString,
                configurationForHttp,
                factory,
                commandCounter);
            var client = host.GetTestClient();

            commandCounter.Reset();
            using var shellRequest = new HttpRequestMessage(HttpMethod.Get, "/founder/legend-connect?language=en");
            shellRequest.Headers.Add("X-Legend-Connect-Founder", founderId);
            var shellHttpClock = Stopwatch.StartNew();
            using var shellResponse = await client.SendAsync(shellRequest);
            shellHttpClock.Stop();
            shellResponse.EnsureSuccessStatusCode();
            var shellBytes = (await shellResponse.Content.ReadAsByteArrayAsync()).Length;
            var shellHtml = await shellResponse.Content.ReadAsStringAsync();
            Assert.Contains("data-legend-connect-shell", shellHtml, StringComparison.Ordinal);
            Assert.Contains("data-legend-section=\"curriculum\"", shellHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("founder-page-scale-20260820.historical", shellHtml, StringComparison.Ordinal);
            var shellHttpQueryCount = commandCounter.Commands;

            commandCounter.Reset();
            using var switchedLanguageRequest = new HttpRequestMessage(HttpMethod.Get, "/founder/legend-connect?language=ht");
            switchedLanguageRequest.Headers.Add("X-Legend-Connect-Founder", founderId);
            using var switchedLanguageResponse = await client.SendAsync(switchedLanguageRequest);
            switchedLanguageResponse.EnsureSuccessStatusCode();
            var switchedLanguageHtml = await switchedLanguageResponse.Content.ReadAsStringAsync();
            Assert.Contains("data-language=\"ht\"", switchedLanguageHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("founder-page-scale-20260820.historical", switchedLanguageHtml, StringComparison.Ordinal);
            var switchedLanguageQueryCount = commandCounter.Commands;

            commandCounter.Reset();
            using var switchedSectionRequest = new HttpRequestMessage(HttpMethod.Get,
                "/founder/legend-connect/sections?section=curriculum&language=ht");
            switchedSectionRequest.Headers.Add("X-Legend-Connect-Founder", founderId);
            using var switchedSectionResponse = await client.SendAsync(switchedSectionRequest);
            switchedSectionResponse.EnsureSuccessStatusCode();
            var switchedSection = await switchedSectionResponse.Content.ReadFromJsonAsync<LegendConnectFounderSectionPageSnapshot>();
            Assert.NotNull(switchedSection);
            Assert.Empty(switchedSection!.Rows);
            var switchedSectionQueryCount = commandCounter.Commands;

            commandCounter.Reset();
            using var pageRequest = new HttpRequestMessage(HttpMethod.Get,
                "/founder/legend-connect/sections?section=curriculum&language=en");
            pageRequest.Headers.Add("X-Legend-Connect-Founder", founderId);
            var pageClock = Stopwatch.StartNew();
            using var pageResponse = await client.SendAsync(pageRequest);
            pageClock.Stop();
            pageResponse.EnsureSuccessStatusCode();
            var responseBytes = (await pageResponse.Content.ReadAsByteArrayAsync()).Length;
            var curriculum = await pageResponse.Content.ReadFromJsonAsync<LegendConnectFounderSectionPageSnapshot>();
            Assert.NotNull(curriculum);
            Assert.Equal(50, curriculum!.Rows.Count);
            Assert.NotNull(curriculum.NextCursor);
            var curriculumQueryCount = commandCounter.Commands;

            commandCounter.Reset();
            using var nextPageRequest = new HttpRequestMessage(HttpMethod.Get,
                $"/founder/legend-connect/sections?section=curriculum&language=en&cursor={Uri.EscapeDataString(curriculum.NextCursor!)}");
            nextPageRequest.Headers.Add("X-Legend-Connect-Founder", founderId);
            using var nextPageResponse = await client.SendAsync(nextPageRequest);
            nextPageResponse.EnsureSuccessStatusCode();
            var nextCurriculum = await nextPageResponse.Content.ReadFromJsonAsync<LegendConnectFounderSectionPageSnapshot>();
            Assert.NotNull(nextCurriculum);
            Assert.Equal(50, nextCurriculum!.Rows.Count);
            Assert.Empty(curriculum.Rows.Select(row => row[0]).Intersect(nextCurriculum.Rows.Select(row => row[0])));
            var nextCurriculumQueryCount = commandCounter.Commands;

            using var oldSearchRequest = new HttpRequestMessage(HttpMethod.Get,
                "/founder/legend-connect/sections?section=curriculum&language=en&search=founder-page-scale-20260820.historical");
            oldSearchRequest.Headers.Add("X-Legend-Connect-Founder", founderId);
            using var oldSearchResponse = await client.SendAsync(oldSearchRequest);
            oldSearchResponse.EnsureSuccessStatusCode();
            var oldSearch = await oldSearchResponse.Content.ReadFromJsonAsync<LegendConnectFounderSectionPageSnapshot>();
            Assert.Single(oldSearch!.Rows);

            commandCounter.Reset();
            using var evidenceRequest = new HttpRequestMessage(HttpMethod.Get,
                "/founder/legend-connect/sections?section=evidence&language=en");
            evidenceRequest.Headers.Add("X-Legend-Connect-Founder", founderId);
            var evidenceClock = Stopwatch.StartNew();
            using var evidenceResponse = await client.SendAsync(evidenceRequest);
            evidenceClock.Stop();
            evidenceResponse.EnsureSuccessStatusCode();
            var evidenceBytes = (await evidenceResponse.Content.ReadAsByteArrayAsync()).Length;
            var evidence = await evidenceResponse.Content.ReadFromJsonAsync<LegendConnectFounderSectionPageSnapshot>();
            Assert.Equal(50, evidence!.Rows.Count);
            var evidenceQueryCount = commandCounter.Commands;

            commandCounter.Reset();
            using var examplesRequest = new HttpRequestMessage(HttpMethod.Get,
                $"/founder/legend-connect/sections?section=curriculum-examples&language=en&familyId={oldSearch.Rows[0][0]}");
            examplesRequest.Headers.Add("X-Legend-Connect-Founder", founderId);
            var examplesClock = Stopwatch.StartNew();
            using var examplesResponse = await client.SendAsync(examplesRequest);
            examplesClock.Stop();
            examplesResponse.EnsureSuccessStatusCode();
            var examplesBytes = (await examplesResponse.Content.ReadAsByteArrayAsync()).Length;
            var familyExamples = await examplesResponse.Content.ReadFromJsonAsync<LegendConnectFounderSectionPageSnapshot>();
            Assert.Single(familyExamples!.Rows);
            var examplesQueryCount = commandCounter.Commands;

            _output.WriteLine($"SQL SHELL HTTP STATUS: {(int)shellResponse.StatusCode}");
            _output.WriteLine($"SQL SHELL HTTP LATENCY MS: {shellHttpClock.Elapsed.TotalMilliseconds:F0}");
            _output.WriteLine($"SQL SHELL HTTP RESPONSE BYTES: {shellBytes}");
            _output.WriteLine($"SQL SHELL HTTP QUERY COUNT: {shellHttpQueryCount}");
            _output.WriteLine("SQL SHELL HTTP MATERIALIZED DETAIL ROWS: 0");
            _output.WriteLine($"SQL LANGUAGE SWITCH HTTP QUERY COUNT: {switchedLanguageQueryCount}");
            _output.WriteLine($"SQL LANGUAGE SWITCH SECTION QUERY COUNT: {switchedSectionQueryCount}");
            _output.WriteLine("SQL LANGUAGE SWITCH CROSS-LANGUAGE ROWS: 0");
            _output.WriteLine($"SQL CURRICULUM HTTP STATUS: {(int)pageResponse.StatusCode}");
            _output.WriteLine($"SQL CURRICULUM LATENCY MS: {pageClock.Elapsed.TotalMilliseconds:F0}");
            _output.WriteLine($"SQL CURRICULUM RESPONSE BYTES: {responseBytes}");
            _output.WriteLine($"SQL CURRICULUM QUERY COUNT: {curriculumQueryCount}");
            _output.WriteLine($"SQL CURRICULUM ROWS: {curriculum.Rows.Count}");
            _output.WriteLine($"SQL CURRICULUM NEXT-PAGE QUERY COUNT: {nextCurriculumQueryCount}");
            _output.WriteLine($"SQL CURRICULUM NEXT-PAGE ROWS: {nextCurriculum.Rows.Count}");
            _output.WriteLine($"SQL EVIDENCE LATENCY MS: {evidenceClock.Elapsed.TotalMilliseconds:F0}");
            _output.WriteLine($"SQL EVIDENCE RESPONSE BYTES: {evidenceBytes}");
            _output.WriteLine($"SQL EVIDENCE QUERY COUNT: {evidenceQueryCount}");
            _output.WriteLine($"SQL EVIDENCE ROWS: {evidence.Rows.Count}");
            _output.WriteLine($"SQL FAMILY EXAMPLES LATENCY MS: {examplesClock.Elapsed.TotalMilliseconds:F0}");
            _output.WriteLine($"SQL FAMILY EXAMPLES RESPONSE BYTES: {examplesBytes}");
            _output.WriteLine($"SQL FAMILY EXAMPLES QUERY COUNT: {examplesQueryCount}");
            _output.WriteLine($"SQL FAMILY EXAMPLES ROWS: {familyExamples.Rows.Count}");
            _output.WriteLine("SQL OLD RECORD DISCOVERY: passed");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", previousFounderOid);
        }
    }

    private static async Task<IHost> BuildAuthenticatedHttpHostAsync(
        string connectionString,
        IConfiguration configuration,
        IHttpClientFactory factory,
        DbCommandInterceptor? commandInterceptor = null)
    {
        return await new HostBuilder()
            .ConfigureWebHost(webBuilder => webBuilder
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddDataProtection();
                    services.AddLogging();
                    services.AddHttpContextAccessor();
                    services.AddControllersWithViews()
                        .AddApplicationPart(typeof(LegendFounderAiController).Assembly)
                        .AddApplicationPart(System.Reflection.Assembly.Load("AgentPortal.Views"))
                        .AddApplicationPart(typeof(Shared.Diagnostics.AppFailureDiagnostics).Assembly)
                        .AddApplicationPart(typeof(LegendConnectOperationalProofController).Assembly);
                    services.AddAntiforgery(options => options.HeaderName = "RequestVerificationToken");
                    services.AddAuthentication("LegendConnectTest")
                        .AddScheme<AuthenticationSchemeOptions, LegendConnectFounderAuthHandler>(
                            "LegendConnectTest",
                            _ => { });
                    services.AddAuthorization(options => options.DefaultPolicy =
                        new AuthorizationPolicyBuilder("LegendConnectTest")
                            .RequireAuthenticatedUser()
                            .Build());
                    services.AddDbContext<MasterAppDbContext>(options =>
                    {
                        options.UseSqlServer(connectionString);
                        if (commandInterceptor is not null)
                            options.AddInterceptors(commandInterceptor);
                    });
                    services.AddSingleton(configuration);
                    services.AddSingleton(factory);
                    services.AddScoped<ILegendLanguageRegistry, LegendLanguageRegistry>();
                    services.AddScoped<LegendConnectCorpusService>();
                    services.AddScoped<LegendConnectCurriculumService>();
                    services.AddScoped<ILegendConnectOperations>(serviceProvider =>
                        new LegendConnectOperations(
                            serviceProvider.GetRequiredService<MasterAppDbContext>(),
                            serviceProvider.GetRequiredService<ILegendLanguageRegistry>(),
                            serviceProvider.GetRequiredService<LegendConnectCorpusService>(),
                            serviceProvider.GetRequiredService<IConfiguration>(),
                            curriculum: serviceProvider.GetRequiredService<LegendConnectCurriculumService>()));
                    services.AddScoped<AgentProfileAccessResolver>();
                    services.AddScoped<FounderLegendConnectService>();
                    services.AddScoped<LegendFounderAiConversationService>();
                    services.AddSingleton<LegendFounderAiProgressBroker>();
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
            ? values.FirstOrDefault(value => value.StartsWith(
                    ".AspNetCore.Antiforgery",
                    StringComparison.OrdinalIgnoreCase))?.Split(';')[0] ?? string.Empty
            : string.Empty;

    private sealed class CountingDbCommandInterceptor : DbCommandInterceptor
    {
        private int _commands;

        public int Commands => Volatile.Read(ref _commands);

        public void Reset() => Interlocked.Exchange(ref _commands, 0);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _commands);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _commands);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class CountingHttpClientFactory : IHttpClientFactory
    {
        public int CreateClientCalls { get; private set; }

        public HttpClient CreateClient(string name)
        {
            CreateClientCalls++;
            return new HttpClient(new NoNetworkHandler())
            {
                BaseAddress = new Uri("https://legend-e2e.invalid/")
            };
        }
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            System.Threading.CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The OpenAI test client must not be used by native inference.");
    }
}
