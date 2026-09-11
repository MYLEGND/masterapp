using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentPortal.Tests;

// Locks the one-authority Founder Teacher diagnostic contract against regression.
public sealed class LegendFounderAiComprehensiveDiagnosticContractTests
{
    [ResourceDiagnosticFact]
    public Task ResourceEnabled_AzureBoundary_ReportsActualProviderOutcomeWithoutLearning() =>
        ObserveResourceAsync("azure");

    [ResourceDiagnosticFact]
    public Task ResourceEnabled_ResearchBoundary_ReportsActualGovernedOutcomeWithoutPromotion() =>
        ObserveResourceAsync("research");

    [Theory]
    [InlineData("ExplicitResourceDiagnosticOptIn", false, "research_public_authorization_invalid")]
    [InlineData(LegendConnectResearchContracts.LockedEvaluationAuthorizationProvenance, true, "research_request_governed")]
    public void ResourceResearchAuthorization_UsesExistingLockedEvaluatorContract(
        string provenance, bool expectedValid, string expectedReason)
    {
        const string question = "What title is published at https://www.rfc-editor.org/rfc/rfc9110?";
        var decision = LegendConnectOperations.DecideResearchNeeded(
            question, "en", null, new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc));
        var request = LegendConnectResearchRequestFactory.Create(question, decision,
            new(true, provenance, null, decision.AccessClass, true, true), null, null, 0);

        Assert.True(decision.ResearchRequired);
        Assert.Equal(LegendConnectResearchAccessClass.PublicReadOnly, decision.AccessClass);
        Assert.Equal(expectedValid, LegendConnectOperations.TryValidateResearchRequest(request, out var reason));
        Assert.Equal(expectedReason, reason);
        Assert.True(request.Authorization.IsReadOnly && request.Authorization.ZeroWrite);
    }

    private static async Task ObserveResourceAsync(string resource)
    {
        var startedUtc = DateTime.UtcNow;
        var clock = Stopwatch.StartNew();
        var stages = new List<object>();
        var http = new ConcurrentQueue<object>();
        var writes = new ResourceWriteGuard();
        var candidateSha = Environment.GetEnvironmentVariable("LEGEND_VALIDATION_CANDIDATE_SHA");
        var runIdentity = Environment.GetEnvironmentVariable("LEGEND_VALIDATION_RUN_IDENTITY");
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var credentialConfigured = resource == "azure"
            ? !string.IsNullOrWhiteSpace(configuration["AzureTranslator:Key"] ??
                Environment.GetEnvironmentVariable("AZURE_TRANSLATOR_KEY"))
            : !string.IsNullOrWhiteSpace(configuration["LegendConnect:InternetResearch:ApiKey"] ??
                configuration["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ??
                Environment.GetEnvironmentVariable("OpenAI__ApiKey"));
        var endpointValue = resource == "azure" ? configuration["AzureTranslator:Endpoint"] :
            configuration["LegendConnect:InternetResearch:Endpoint"] ?? "https://api.openai.com/v1/responses";
        var endpointConfigured = Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint) &&
            endpoint.Scheme == Uri.UriSchemeHttps;
        var status = "NOT_CONFIGURED";
        var reason = "resource_configuration_missing";
        var stage = "configuration";
        object? observed = null;
        try
        {
            Assert.True(credentialConfigured && endpointConfigured, "NOT_CONFIGURED: resource credentials or HTTPS endpoint absent.");
            reason = "candidate_identity_missing";
            Assert.True(candidateSha is { Length: 40 } && candidateSha.All(Uri.IsHexDigit),
                "NOT_CONFIGURED: exact candidate SHA absent.");
            reason = "run_identity_missing";
            Assert.True(runIdentity is { Length: > 0 and <= 100 } &&
                runIdentity.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':'),
                "NOT_CONFIGURED: bounded run identity absent.");
            status = "FAILED";
            reason = "resource_probe_failed";
            stage = "candidate_assembly";
            foreach (var assembly in new[] { typeof(LegendFounderAiComprehensiveDiagnosticContractTests).Assembly,
                         typeof(LegendConnectOperations).Assembly, typeof(FounderLegendConnectService).Assembly })
                Assert.True(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?.EndsWith("+" + candidateSha, StringComparison.Ordinal) == true,
                    "Executed assembly is not bound to the requested candidate.");

            stage = "registered_authorities";
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSignalR();
            services.AddDbContext<MasterAppDbContext>(options => options
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .AddInterceptors(writes));
            services.AddMasterAppMessaging(configuration);
            foreach (var client in new[] { "AzureTranslator", "LegendInternetResearchSearch",
                         LegendConnectResearchPageRetriever.ClientName })
            {
                // Append observation to the existing named pipeline. Keep its
                // production timeout, transport, and public-network policy.
                var clientName = client;
                services.AddHttpClient(clientName).AddHttpMessageHandler(() => new ResourceHttpObserver(clientName, http));
            }
            await using var provider = services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
            Assert.False(db.Database.IsRelational());
            ControllerTestHelpers.SeedGovernedLanguageBaseline(db);
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<ILegendLanguageRegistry>()
                .NormalizeEnabledTranslationLanguageAsync("en");
            writes.Armed = true;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(resource == "azure" ? 30 : 100));

            async Task<T> StageAsync<T>(string name, Func<Task<T>> action)
            {
                stage = name;
                var started = Stopwatch.GetTimestamp();
                var terminal = "FAILED";
                string? terminalReason = null;
                string? failureType = null;
                try
                {
                    var result = await action();
                    (terminal, terminalReason) = result switch
                    {
                        TranslationProviderResult translation =>
                            (translation.Succeeded ? "SUCCEEDED" : "UNAVAILABLE", translation.ErrorCode),
                        LegendConnectResearchNeededDecision decision =>
                            (decision.ResearchRequired ? "RESEARCH_REQUIRED" : "RESEARCH_NOT_AUTHORIZED", decision.ReasonCode),
                        LegendConnectResearchOutcome research =>
                            (research.State.ToString(), ResourceResearchReason(research)),
                        _ => ("COMPLETED", null)
                    };
                    return result;
                }
                catch (Exception exception)
                {
                    failureType = exception.GetType().Name;
                    terminalReason = ResourceFailureReason(exception);
                    throw;
                }
                finally
                {
                    stages.Add(new { Stage = name, Outcome = terminal, Reason = terminalReason, FailureType = failureType,
                        ElapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
                }
            }

            if (resource == "azure")
            {
                var translator = scope.ServiceProvider.GetRequiredService<ITranslationProvider>();
                var sentence = "The public test parcel contains " + Random.Shared.Next(20, 90) + " blue cards.";
                var forbidden = await StageAsync("native_only_policy", () => translator.TranslateAsync(
                    sentence, "es", "en", deadline.Token, LegendConnectExternalProviderPolicy.NativeOnly));
                observed = new { Policy = "NativeOnly", forbidden.Succeeded, forbidden.ErrorCode };
                Assert.False(forbidden.Succeeded);
                Assert.Equal("external_provider_forbidden_by_native_only_policy", forbidden.ErrorCode);
                Assert.Empty(http);
                var result = await StageAsync("provider_translation", () => translator.TranslateAsync(
                    sentence, "es", "en", deadline.Token, LegendConnectExternalProviderPolicy.ProviderEnabled));
                observed = new { Policy = "ProviderEnabled", NativeOnlyReason = forbidden.ErrorCode,
                    result.Succeeded, result.Provider, result.ErrorCode,
                    OutputPresent = !string.IsNullOrWhiteSpace(result.TranslatedText), Provenance = "ProviderDerived",
                    Serving = "NonServing", Canonical = "NonCanonical" };
                Assert.True(result.Succeeded, result.ErrorCode);
                Assert.Equal("AzureTranslator", result.Provider);
                Assert.False(string.IsNullOrWhiteSpace(result.TranslatedText));
                Assert.NotEmpty(http);
            }
            else
            {
                var operations = scope.ServiceProvider.GetRequiredService<ILegendConnectOperations>();
                const string question = "What title is published at https://www.rfc-editor.org/rfc/rfc9110?";
                var forbidden = await StageAsync("native_only_policy", () => operations.DecideResearchNeededAsync(
                    question, "en", null, deadline.Token, LegendConnectExternalProviderPolicy.NativeOnly));
                observed = new { Policy = "NativeOnly", forbidden.ResearchRequired, forbidden.ReasonCode };
                Assert.False(forbidden.ResearchRequired);
                Assert.Equal("native_only_external_research_forbidden", forbidden.ReasonCode);
                Assert.Empty(http);
                var decision = await StageAsync("research_policy", () => operations.DecideResearchNeededAsync(
                    question, "en", null, deadline.Token, LegendConnectExternalProviderPolicy.ProviderEnabled));
                observed = new { Policy = "ProviderEnabled", NativeOnlyReason = forbidden.ReasonCode,
                    DecisionReason = decision.ReasonCode, decision.ResearchRequired, Need = decision.Need.ToString() };
                Assert.True(decision.ResearchRequired, decision.ReasonCode);
                Assert.Equal(LegendConnectResearchAccessClass.PublicReadOnly, decision.AccessClass);
                var request = LegendConnectResearchRequestFactory.Create(question, decision,
                    new(true, LegendConnectResearchContracts.LockedEvaluationAuthorizationProvenance, null, decision.AccessClass, true, true), null, null, 0);
                var blocked = await StageAsync("native_only_execution", () => operations.ExecuteResearchAsync(
                    request, deadline.Token, LegendConnectExternalProviderPolicy.NativeOnly));
                Assert.Equal("native_only_external_research_forbidden", blocked.Failure?.ReasonCode);
                Assert.Empty(http);
                var outcome = await StageAsync("governed_research", () => operations.ExecuteResearchAsync(
                    request, deadline.Token, LegendConnectExternalProviderPolicy.ProviderEnabled));
                observed = new { Policy = "ProviderEnabled", NativeOnlyReason = forbidden.ReasonCode,
                    DecisionReason = decision.ReasonCode, Need = decision.Need.ToString(),
                    Outcome = outcome.State.ToString(), EvidenceOrigin = outcome.EvidenceOrigin.ToString(),
                    FailureReason = ResourceResearchReason(outcome),
                    EvidenceDiagnostics = ResourceResearchEvidence(outcome),
                    outcome.Provenance.SearchProvider, outcome.Provenance.Provenance,
                    outcome.Provenance.IsReadOnly, outcome.Provenance.ZeroWrite,
                    QueryReceipts = outcome.Session.SearchQueryReceipts?.Count ?? 0,
                    PageReceipts = outcome.Session.PageReceipts?.Count ?? 0,
                    Documents = outcome.Session.Documents.Count, Citations = outcome.Session.Citations.Count,
                    outcome.Session.SearchLatencyMilliseconds, outcome.Session.RetrievalLatencyMilliseconds,
                    outcome.Session.ReasoningLatencyMilliseconds,
                    Serving = "NonServing", Canonical = "NonCanonical" };
                await StageAsync("local_observability", async () =>
                {
                    await operations.RecordResearchObservabilityAsync(outcome, deadline.Token);
                    return true;
                });
                Assert.Equal(LegendConnectResearchContracts.LockedEvaluationAuthorizationProvenance,
                    outcome.Provenance.AuthorizationProvenance);
                Assert.Null(outcome.Retention);
                Assert.Null(LegendConnectResearchRetentionContracts.CreateExternalObservation(outcome));
                Assert.NotEqual(LegendConnectResearchOutcomeState.Failure, outcome.State);
                Assert.True(outcome.Provenance.IsReadOnly && outcome.Provenance.ZeroWrite);
                Assert.NotEmpty(outcome.Session.SearchQueryReceipts ?? []);
                Assert.NotEmpty(http);
                Assert.True(writes.ObservabilityWrites > 0);
                Assert.True(await db.Set<LegendConnectOperationalEvent>().AnyAsync());
            }
            Assert.Equal(0, writes.BlockedWrites);
            Assert.Empty(await db.LegendLanguageTextUnits.ToArrayAsync());
            Assert.Empty(await db.LegendCurriculumExamples.ToArrayAsync());
            Assert.Empty(await db.LegendLanguageTeacherProposals.ToArrayAsync());
            status = "OBSERVED";
            reason = "resource_boundary_observed_not_production_data_proof";
        }
        catch (Exception exception)
        {
            if (status != "NOT_CONFIGURED") reason = ResourceFailureReason(exception);
            stages.Add(new { Stage = stage, Outcome = status, Reason = reason, FailureType = exception.GetType().Name,
                ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds });
        }
        finally
        {
            var report = JsonSerializer.Serialize(new
            {
                CandidateSha = candidateSha is { Length: 40 } && candidateSha.All(Uri.IsHexDigit) ? candidateSha : null,
                RunIdentity = runIdentity is { Length: > 0 and <= 100 } && runIdentity.All(character =>
                    char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':') ? runIdentity : null,
                Authority = "NonAuthoritativeResourceBoundaryDiagnostic", Environment = "LocalInMemoryObservabilityWithLiveProvider",
                Resource = resource, Status = status, Reason = reason, CredentialConfigured = credentialConfigured,
                EndpointConfigured = endpointConfigured, Stage = stage, Stages = stages,
                HttpCallCount = http.Count, HttpCalls = http.Take(64).ToArray(),
                HttpCallsDropped = Math.Max(0, http.Count - 64), Outcome = observed,
                CanonicalWriteAttempts = writes.BlockedWrites, LocalObservabilityWrites = writes.ObservabilityWrites,
                SaveChangesAttempts = writes.SaveChangesAttempts,
                StartedUtc = startedUtc, CompletedUtc = DateTime.UtcNow,
                ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds
            }, new JsonSerializerOptions { WriteIndented = true });
            var directory = Path.Combine(FindRepositoryRoot(), "diagnostics", "legend-shadow");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "resource-" + resource + ".json"), report);
        }
        Assert.True(status == "OBSERVED", status + ": " + reason + " at " + stage);
    }

    private static string ResourceResearchReason(LegendConnectResearchOutcome outcome) =>
        LegendConnectTelemetry.NormalizeDiagnosticReason(outcome.Failure?.ReasonCode ??
            outcome.InsufficientEvidence?.ReasonCode ?? outcome.UnresolvedConflict?.ReasonCode ??
            outcome.Session.FailureReason);

    // Project only bounded counts, enum states and allowlisted reasons. Never
    // serialize source identities, URLs, questions, passages or claim text.
    private static object ResourceResearchEvidence(LegendConnectResearchOutcome outcome) => new
    {
        AdmissibleClaimCount = outcome.InsufficientEvidence?.AdmissibleClaimCount,
        IndependentSourceCount = outcome.InsufficientEvidence?.IndependentSourceCount,
        RequiredIndependentSourceCount = outcome.InsufficientEvidence?.RequiredIndependentSourceCount,
        CandidateCounts = outcome.Session.CandidateCounts,
        ClaimCount = outcome.Session.ClaimEvidence.Count,
        MaterialClaimCount = outcome.Session.MaterialClaimEvidence?.Count ?? 0,
        ContradictionCount = outcome.Session.ContradictingEvidence.Count,
        Admissibility = outcome.Session.EvidenceAdmissibility?.Take(32).Select(item => new
        {
            Subject = item.Subject.ToString(), SourceClass = item.SourceClass.ToString(),
            Disposition = item.Disposition.ToString(),
            Reason = LegendConnectTelemetry.NormalizeDiagnosticReason(item.ReasonCode)
        }).ToArray(),
        ClaimResolutions = outcome.Session.ClaimResolutions?.Take(12).Select(item => new
        {
            State = item.State.ToString(),
            Reason = LegendConnectTelemetry.NormalizeDiagnosticReason(item.ReasonCode),
            MaterialEvidenceCount = item.MaterialEvidenceIdentities.Count,
            IndependentSourceCount = item.IndependentSourceLineages.Count,
            item.RequiresDiscriminatingEvidence
        }).ToArray(),
        Pages = outcome.Session.PageReceipts?.Take(8).Select(item => new
        {
            item.Succeeded, item.StatusCode, item.RequestCount, item.RedirectCount,
            item.ReturnedBytes,
            Reason = LegendConnectTelemetry.NormalizeDiagnosticReason(item.FailureReason)
        }).ToArray(),
        CitationValidation = outcome.Session.CitationValidation is { } citation ? new
        {
            citation.Succeeded, citation.MaterialClaimCount, citation.InlineCitationCount,
            RejectionReasons = citation.RejectionReasons.Take(16)
                .Select(LegendConnectTelemetry.NormalizeDiagnosticReason).ToArray()
        } : null
    };

    private sealed class ResourceDiagnosticFactAttribute : FactAttribute
    {
        public ResourceDiagnosticFactAttribute()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("LEGEND_RESOURCE_DIAGNOSTICS_REQUIRED"),
                    "true", StringComparison.OrdinalIgnoreCase))
                Skip = "NOT_CONFIGURED: live resource diagnostics require explicit resource opt-in.";
        }
    }

    private static string ResourceFailureReason(Exception exception) => exception switch
    {
        OperationCanceledException => "resource_cancelled_or_deadline_exceeded",
        HttpRequestException => "resource_transport_failed",
        Xunit.Sdk.XunitException => "resource_contract_assertion_failed",
        _ => "resource_execution_failed"
    };

    private sealed class ResourceHttpObserver(string client, ConcurrentQueue<object> observations) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var started = Stopwatch.GetTimestamp();
            int? status = null;
            try
            {
                var response = await base.SendAsync(request, cancellationToken);
                status = (int)response.StatusCode;
                return response;
            }
            finally
            {
                observations.Enqueue(new { Client = client, StatusCode = status,
                    ElapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
            }
        }
    }

    internal sealed class ResourceWriteGuard : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public int BlockedWrites { get; private set; }
        public int ObservabilityWrites { get; private set; }
        public int SaveChangesAttempts { get; private set; }
        private void Observe(DbContext? context)
        {
            if (!Armed || context is null) return;
            SaveChangesAttempts++;
            var changes = context.ChangeTracker.Entries().Where(entry =>
                entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToArray();
            if (changes.Any(entry => entry.Entity is not LegendConnectOperationalEvent))
            {
                BlockedWrites++;
                throw new InvalidOperationException("Resource diagnostics forbid canonical or proposal mutation.");
            }
            ObservabilityWrites += changes.Length;
        }
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        { Observe(eventData.Context); return result; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Observe(eventData.Context); return ValueTask.FromResult(result); }
    }

    // Mandatory inspection now depends on admitted meaning and the exact
    // governed result-frame scope, rather than keywords or an arbitrary tool
    // count. ModeIsolation tests execute optional discovery, exact scoped
    // reads, unrelated-read rejection and partial-read disclosure end to end.

    [Theory]
    [InlineData(false, "connectivity_failure", "unknown")]
    [InlineData(true, "permission_denied", "denied")]
    public void ReadToolFailure_PreservesStructuredAuthorityAndCorrelation(
        bool permissionDenied, string expectedCategory, string expectedAuthorization)
    {
        var method = typeof(LegendFounderAiConversationService).GetMethod(
            "BuildReadOnlyToolFailureOutput", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        Exception failure = permissionDenied
            ? new UnauthorizedAccessException("controlled access denial")
            : new HttpRequestException("controlled transport failure");
        var serialized = Assert.IsType<string>(method!.Invoke(null,
            new object[] { "legend_search_retained_knowledge", failure }));
        Assert.DoesNotContain(failure.Message, serialized);
        using var document = JsonDocument.Parse(serialized);
        var root = document.RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal("tool_read_failed", root.GetProperty("error").GetString());
        Assert.Equal(expectedCategory, root.GetProperty("failureCategory").GetString());
        Assert.Equal(expectedAuthorization, root.GetProperty("authorizationDecision").GetString());
        Assert.Equal("legend_search_retained_knowledge", root.GetProperty("requestedResource").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("correlationId").GetString()));
        Assert.Contains("Continue any independent governed reads", root.GetProperty("instruction").GetString());
    }

    [Fact]
    public void FounderProviderWindows_AreLargeAndAutomaticallyContinueIncompleteAnswers()
    {
        var source = ReadService();
        Assert.Contains("MaximumProviderConversationCharacters = 600_000", source, StringComparison.Ordinal);
        Assert.Contains("MaximumConversationCharacters = 2_000_000", source, StringComparison.Ordinal);
        Assert.Contains("MaximumToolOutputCharacters = 160_000", source, StringComparison.Ordinal);
        Assert.Contains("32_000", source, StringComparison.Ordinal);
        Assert.Contains("64_000", source, StringComparison.Ordinal);
        Assert.Contains("Continue the same answer exactly where it stopped", source, StringComparison.Ordinal);
        Assert.Contains("MergeProviderAnswerSegment", source, StringComparison.Ordinal);
        Assert.Contains("accumulatedProviderAnswer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Ask the OpenAI Teacher to continue if you want the remainder", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Teacher_IsToldExistingGovernedAccessIsReal_NotToRequestManualExports()
    {
        var source = ReadService();
        Assert.Contains("Those tools are real capabilities", source, StringComparison.Ordinal);
        Assert.Contains("never tell the Founder", source, StringComparison.Ordinal);
        Assert.Contains("Capability discovery alone is not evidence", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeMeaningGraph_QueryIsScopedToLexemesInTheCurrentInput()
    {
        var source = ReadRepositoryFile(
            "Infrastructure",
            "Messaging",
            "LegendConnectCurriculum.cs");

        Assert.Contains("inputLexemeHashes", source, StringComparison.Ordinal);
        Assert.Contains("join lexeme in _db.Set<LegendLanguageLexeme>()", source, StringComparison.Ordinal);
        // Hashes sharing a request multiplicity now share a SQL branch; each
        // hash must still be grouped separately before the unchanged span sum.
        Assert.Contains("requestHashes.Contains(lexeme.NormalizedHash)", source, StringComparison.Ordinal);
        Assert.Contains("requestLexemes.GroupBy(item => item.Count)", source, StringComparison.Ordinal);
        Assert.Matches(@"group occurrence by new\s*\{\s*AnchorId = anchor.Id,\s*lexeme.NormalizedHash,", source);
        Assert.Contains("candidate.Sum(item => item.MatchedOccurrenceCount)", source, StringComparison.Ordinal);
        Assert.Contains("anchor.ComponentStartTokenIndex != null", source, StringComparison.Ordinal);
        Assert.Contains("anchor.ComponentStartTokenIndex >= 0", source, StringComparison.Ordinal);
    }

    private static string ReadService() =>
        ReadRepositoryFile(
            "AgentPortal",
            "Services",
            "LegendFounderAiConversationService.cs");

    private static string ReadRepositoryFile(params string[] path)
    {
        var segments = new string[path.Length + 1];
        segments[0] = FindRepositoryRoot();
        Array.Copy(path, 0, segments, 1, path.Length);
        return File.ReadAllText(Path.Combine(segments));
    }

    private static string FindRepositoryRoot()
    {
        var githubWorkspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (IsRepositoryRoot(githubWorkspace))
            return Path.GetFullPath(githubWorkspace!);

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (IsRepositoryRoot(directory.FullName))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Repository root was not found from GITHUB_WORKSPACE, the working directory, or the test base directory.");
    }

    private static bool IsRepositoryRoot(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        File.Exists(Path.Combine(path, "MASTERAPP.sln")) &&
        Directory.Exists(Path.Combine(path, "AgentPortal")) &&
        Directory.Exists(Path.Combine(path, "AgentPortal.Tests"));
}
