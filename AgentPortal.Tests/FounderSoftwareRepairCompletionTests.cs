using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Services;
using Azure.Core;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class FounderSoftwareRepairCompletionTests
{
    private static readonly string Base = new('a', 40), Reviewed = new('c', 40), Head = new('f', 40),
        Merged = new('9', 40), Source = new('6', 40), Tree = new('e', 40);

    [Fact]
    public async Task ExactWebRollout_ArchivesAtomically_RetainsContext_AndStartsDistinctPreviewWithoutClosingIncident()
    {
        await using var fixture = await Fixture.CreateAsync();
        var revision = fixture.Active.Revision;
        var result = await fixture.CompleteAsync();
        Assert.True(result.GetProperty("archived").GetBoolean(), result.ToString());
        Assert.False(result.GetProperty("functionalFixVerified").GetBoolean());
        Assert.False(result.GetProperty("incidentsAutomaticallyClosed").GetBoolean());
        fixture.Db.ChangeTracker.Clear();
        var rows = await fixture.Db.FounderSoftwareRepairBatches.ToArrayAsync();
        Assert.Equal(2, rows.Length);
        var archive = Assert.Single(rows.Where(row => row.Id != "active"));
        var active = Assert.Single(rows.Where(row => row.Id == "active"));
        Assert.Equal("WebDeploymentVerified", archive.State);
        Assert.Equal(Merged, archive.MergedSha);
        Assert.Equal(Tree, archive.DeployedTreeSha);
        Assert.Equal(101L, archive.DeploymentRunId);
        Assert.Equal(Reviewed, archive.ReviewedHeadSha);
        Assert.Equal("hotfix/staging-batch", archive.PreviewBranch);
        Assert.Equal("Empty", active.State);
        Assert.Null(active.HeadSha);
        Assert.StartsWith("hotfix/staging-batch/", active.PreviewBranch);
        Assert.NotEqual(revision, active.Revision);
        using var evidence = JsonDocument.Parse(archive.DeploymentEvidenceJson!);
        Assert.Equal(2, evidence.RootElement.GetArrayLength());
        Assert.All(evidence.RootElement.EnumerateArray(), item => Assert.Equal(Source, item.GetProperty("sourceRevision").GetString()));
        var incident = await fixture.Db.RuntimeDiagnosticIncidents.SingleAsync();
        Assert.Equal("ConfirmedDefect", incident.Disposition);
        Assert.True(incident.Recurred);
        Assert.Empty(fixture.Handler.RepositoryWrites);
        var requestCount = fixture.Handler.TotalRequests;
        var replay = JsonSerializer.SerializeToElement(await fixture.Service.ArchiveDeployedBatchAsync(456, Head, revision, default));
        Assert.True(replay.GetProperty("replayed").GetBoolean());
        Assert.Equal(requestCount, fixture.Handler.TotalRequests);
        Assert.Equal(active.PreviewBranch, (await fixture.Db.FounderSoftwareRepairBatches.SingleAsync(row => row.Id == "active")).PreviewBranch);
    }

    [Theory]
    [InlineData("hosts", "deployment_verification_hosts_missing_or_unbounded")]
    [InlineData("merged", "publication_not_merged")]
    [InlineData("tree", "merged_tree_differs_from_reviewed_tree")]
    [InlineData("workflow", "protected_release_success_not_observed")]
    [InlineData("native", "changed_project_not_covered")]
    [InlineData("shared", "changed_project_not_covered")]
    [InlineData("runtime", "live_source_tree_mismatch")]
    [InlineData("identity", "live_application_identity_mismatch")]
    [InlineData("redirect", "provenance_redirect_rejected")]
    public async Task IncompleteOrMismatchedProof_CannotResetBatch(string fault, string expected)
    {
        await using var fixture = await Fixture.CreateAsync(fault);
        var revision = fixture.Active.Revision;
        var result = await fixture.CompleteAsync();
        Assert.Equal(expected, result.GetProperty("error").GetString());
        fixture.Db.ChangeTracker.Clear();
        var active = Assert.Single(await fixture.Db.FounderSoftwareRepairBatches.ToArrayAsync());
        Assert.Equal("active", active.Id);
        Assert.Equal("ValidationRequested", active.State);
        Assert.Equal(revision, active.Revision);
        Assert.Empty(fixture.Handler.RepositoryWrites);
        Assert.True((await fixture.Db.RuntimeDiagnosticIncidents.SingleAsync()).Recurred);
    }

    [Fact]
    public async Task InterruptedSqlReset_RollsBackArchiveInsert_AndLeavesNoDirtyCompletionToReplay()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Interceptor.FailReset = true;
        var result = await fixture.CompleteAsync();
        Assert.Equal("batch_completion_unverified", result.GetProperty("error").GetString());
        Assert.True(fixture.Interceptor.ResetAttempted);
        Assert.False(fixture.Db.ChangeTracker.HasChanges());
        var active = Assert.Single(await fixture.Db.FounderSoftwareRepairBatches.AsNoTracking().ToArrayAsync());
        Assert.Equal("ValidationRequested", active.State);
        Assert.Equal(Head, active.HeadSha);
        fixture.Interceptor.FailReset = false;
        await fixture.Db.SaveChangesAsync();
        Assert.Single(await fixture.Db.FounderSoftwareRepairBatches.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task StaleRevisionOrUnknownOutcome_CannotAttemptRemoteCompletion()
    {
        await using var fixture = await Fixture.CreateAsync();
        var stale = JsonSerializer.SerializeToElement(await fixture.Service.ArchiveDeployedBatchAsync(456, Head, Guid.NewGuid().ToString("N"), default));
        Assert.Equal("batch_identity_changed", stale.GetProperty("error").GetString());
        fixture.Active.State = "OutcomeUnknown";
        await fixture.Db.SaveChangesAsync();
        var unknown = await fixture.CompleteAsync();
        Assert.Equal("batch_identity_changed", unknown.GetProperty("error").GetString());
        Assert.Equal(0, fixture.Handler.TotalRequests);
    }

    [Fact]
    public async Task UnrelatedTrackedChanges_AreNotCommittedByArchive()
    {
        await using var fixture = await Fixture.CreateAsync();
        var incident = await fixture.Db.RuntimeDiagnosticIncidents.SingleAsync();
        incident.Disposition = "ManuallyClosed";
        var result = await fixture.CompleteAsync();
        Assert.Equal("batch_context_not_clean", result.GetProperty("error").GetString());
        Assert.Equal(EntityState.Modified, fixture.Db.Entry(incident).State);
        Assert.Equal(0, fixture.Handler.TotalRequests);
        fixture.Db.Entry(incident).State = EntityState.Detached;
        Assert.Equal("ConfirmedDefect", (await fixture.Db.RuntimeDiagnosticIncidents.SingleAsync()).Disposition);
    }

    private sealed class Fixture(SqliteConnection connection, MasterAppDbContext db, RSA key,
        Handler handler, ResetInterceptor interceptor, FounderSoftwareRemediationService service,
        FounderSoftwareRepairBatch active) : IAsyncDisposable
    {
        public MasterAppDbContext Db { get; } = db;
        public Handler Handler { get; } = handler;
        public ResetInterceptor Interceptor { get; } = interceptor;
        public FounderSoftwareRemediationService Service { get; } = service;
        public FounderSoftwareRepairBatch Active { get; } = active;
        public async Task<JsonElement> CompleteAsync() => JsonSerializer.SerializeToElement(
            await Service.ArchiveDeployedBatchAsync(456, Head, Active.Revision, default));
        public static async Task<Fixture> CreateAsync(string fault = "")
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var interceptor = new ResetInterceptor();
            var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).AddInterceptors(interceptor).Options);
            var script = db.Database.GenerateCreateScript();
            foreach (Match table in Regex.Matches(script,
                         "CREATE TABLE \"(?:FounderSoftwareRepairBatches|FounderSoftwareRemediationAuthorityStates|RuntimeDiagnosticIncidents)\"[\\s\\S]*?;"))
                await db.Database.ExecuteSqlRawAsync(table.Value);
            var active = new FounderSoftwareRepairBatch
            {
                BaseSha = Base, HeadSha = Head, ReviewedHeadSha = Reviewed, PullRequestNumber = 456,
                State = "ValidationRequested", UpdatedUtc = DateTime.UtcNow, OperationId = new string('b', 64)
            };
            db.FounderSoftwareRepairBatches.Add(active);
            db.RuntimeDiagnosticIncidents.Add(new() { Id = Guid.NewGuid(), DeduplicationKey = new string('1', 64), Disposition = "ConfirmedDefect", Recurred = true });
            await db.SaveChangesAsync();
            var key = RSA.Create(2048);
            var handler = new Handler(key.ExportPkcs8PrivateKeyPem(), fault);
            var values = new Dictionary<string, string?>
            {
                ["FounderSoftwareRemediation:Enabled"] = "true",
                ["FounderSoftwareRemediation:RepositoryOwner"] = "MYLEGND", ["FounderSoftwareRemediation:RepositoryName"] = "masterapp",
                ["FounderSoftwareRemediation:BaseBranch"] = "production", ["FounderSoftwareRemediation:GitHubAppId"] = "1",
                ["FounderSoftwareRemediation:GitHubInstallationId"] = "2",
                ["FounderSoftwareRemediation:GitHubAppPrivateKeySecretUri"] = "https://fixture.vault.azure.net/secrets/app",
                ["FounderSoftwareRemediation:GitHubApiBaseUri"] = "https://api.github.com/"
            };
            if (fault != "hosts")
            {
                values["FounderSoftwareRemediation:DeploymentVerificationHosts:AppOne"] = "https://one.example.test";
                values["FounderSoftwareRemediation:DeploymentVerificationHosts:AppTwo"] = "https://two.example.test";
            }
            var service = new FounderSoftwareRemediationService(new Factory(handler), new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
                NullLogger<FounderSoftwareRemediationService>.Instance, new Credential(), db);
            return new(connection, db, key, handler, interceptor, service, active);
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); Handler.Dispose(); key.Dispose(); }
    }

    private sealed class ResetInterceptor : DbCommandInterceptor
    {
        public bool FailReset { get; set; }
        public bool ResetAttempted { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (FailReset && command.CommandText.StartsWith("UPDATE \"FounderSoftwareRepairBatches\"", StringComparison.Ordinal))
            { ResetAttempted = true; throw new InvalidOperationException("Synthetic interrupted reset"); }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, disposeHandler: false); }
    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken token) => new("synthetic", DateTimeOffset.UtcNow.AddMinutes(5));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken token) => ValueTask.FromResult(GetToken(context, token));
    }
    private sealed class Handler(string key, string fault) : HttpMessageHandler
    {
        public int TotalRequests { get; private set; }
        public List<string> RepositoryWrites { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            TotalRequests++;
            object body = new { };
            var status = HttpStatusCode.OK;
            var path = request.RequestUri!.AbsolutePath;
            if (request.RequestUri.Host == "fixture.vault.azure.net") body = new { value = key };
            else if (path == "/app/installations/2/access_tokens") body = new { token = "synthetic-installation" };
            else if (request.RequestUri.Host is "one.example.test" or "two.example.test")
            {
                Assert.Null(request.Headers.Authorization);
                var app = request.RequestUri.Host == "one.example.test" ? "AppOne" : "AppTwo";
                body = new { schemaVersion = 1, appIdentifier = fault == "identity" ? "DifferentApp" : app,
                    sourceRevision = fault == "runtime" && app == "AppTwo" ? new string('8', 40) : Source };
            }
            else if (request.Method != HttpMethod.Get) { RepositoryWrites.Add(path); status = HttpStatusCode.BadRequest; }
            else if (path.EndsWith("/pulls/456")) body = new
            {
                state = "closed", merged = fault != "merged", draft = false, merge_commit_sha = Merged,
                head = new { sha = Head, @ref = "hotfix/publish-" + Reviewed, repo = new { full_name = "MYLEGND/masterapp" } },
                @base = new { @ref = "production", repo = new { full_name = "MYLEGND/masterapp" } }
            };
            else if (path.Contains("/git/commits/")) body = new { tree = new { sha = fault == "tree" && path.EndsWith(Merged) || path.EndsWith(new string('8', 40)) ? new string('7', 40) : Tree } };
            else if (path.Contains("/git/trees/")) body = new
            {
                truncated = false, tree = new[] { "AppOne/AppOne.csproj", "AppTwo/AppTwo.csproj", "Native/App.xcodeproj/project.pbxproj", "Shared/Shared.csproj" }
                    .Select(name => new { path = name, type = "blob", mode = "100644" }).ToArray()
            };
            else if (path.Contains("/compare/")) body = new
            {
                status = "ahead", merge_base_commit = new { sha = Base },
                files = new[] { new { filename = fault == "native" ? "Native/Example.swift" : fault == "shared" ? "Shared/Example.cs" : "AppOne/Controllers/Example.cs", status = "modified" } }
            };
            else if (path.EndsWith("/actions/workflows/agentportal-production-deploy.yml/runs")) body = new
            {
                workflow_runs = new[] { new { id = 101, head_sha = Head, status = "completed", conclusion = fault == "workflow" ? "failure" : "success",
                    @event = "pull_request", path = ".github/workflows/agentportal-production-deploy.yml" } }
            };
            else status = HttpStatusCode.NotFound;
            var response = new HttpResponseMessage(status) { RequestMessage = request,
                Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json") };
            if (fault == "redirect" && request.RequestUri.Host == "one.example.test") response.RequestMessage = new(HttpMethod.Get, "https://different.example.test/api/runtime-provenance");
            return Task.FromResult(response);
        }
    }
}
