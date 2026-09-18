using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Services;
using AgentPortal.Controllers;
using AgentPortal.Security;
using Azure.Core;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class FounderSoftwareRepairBatchTests
{
    private static readonly string BaseSha = new('a', 40);
    private static readonly string HeadSha = new('c', 40);
    private static readonly string MarkerSha = new('f', 40);
    private const string PreviewBranch = "hotfix/staging-batch";

    [Fact]
    public async Task Publish_CreatesSeparateExactHeadRefAndReadyPr_ThenSynchronizesSameTreeOnly()
    {
        using var fixture = new Fixture();
        await fixture.StageAsync();
        fixture.Handler.Writes.Clear();
        var result = JsonSerializer.SerializeToElement(await fixture.Service.ReleaseApprovedAsync(123, HeadSha, default));
        Assert.True(result.GetProperty("publicationRequested").GetBoolean());
        Assert.False(result.GetProperty("released").GetBoolean());
        Assert.Equal(456, result.GetProperty("pullRequestNumber").GetInt32());
        Assert.Equal(HeadSha, result.GetProperty("approvedHeadSha").GetString());
        Assert.Equal(MarkerSha, result.GetProperty("publishedHeadSha").GetString());
        var writes = fixture.Handler.Writes;
        Assert.Equal(4, writes.Count);
        Assert.EndsWith("/git/refs", writes[0].Path);
        Assert.Equal("refs/heads/hotfix/publish-" + HeadSha, writes[0].Body.GetProperty("ref").GetString());
        Assert.Equal(HeadSha, writes[0].Body.GetProperty("sha").GetString());
        Assert.EndsWith("/pulls", writes[1].Path);
        Assert.False(writes[1].Body.GetProperty("draft").GetBoolean());
        Assert.Equal("hotfix/publish-" + HeadSha, writes[1].Body.GetProperty("head").GetString());
        Assert.Equal("production", writes[1].Body.GetProperty("base").GetString());
        Assert.EndsWith("/git/commits", writes[2].Path);
        Assert.Equal(new string('e', 40), writes[2].Body.GetProperty("tree").GetString());
        Assert.Equal(HeadSha, Assert.Single(writes[2].Body.GetProperty("parents").EnumerateArray()).GetString());
        Assert.EndsWith("/git/refs/heads/hotfix/publish-" + HeadSha, writes[3].Path);
        Assert.False(writes[3].Body.GetProperty("force").GetBoolean());
        Assert.Equal(HeadSha, fixture.Handler.Refs[PreviewBranch]);
        Assert.True(fixture.Handler.PreviewDraft);
        Assert.DoesNotContain(writes, write => write.Path.Contains("graphql") || write.Path.Contains("/merge") || write.Path.EndsWith(PreviewBranch));
        var batch = await fixture.Db.FounderSoftwareRepairBatches.SingleAsync();
        Assert.Equal("ValidationRequested", batch.State);
        Assert.Equal(456, batch.PullRequestNumber);
        Assert.Equal(MarkerSha, batch.HeadSha);
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("moved")]
    [InlineData("foreign")]
    public async Task Replay_RequiresCurrentExactDraftPreview_AndDoesNotWriteWhenStale(string change)
    {
        using var fixture = new Fixture();
        await fixture.StageAsync();
        fixture.Handler.Writes.Clear();
        if (change == "ready") fixture.Handler.PreviewDraft = false;
        if (change == "moved") fixture.Handler.Refs[PreviewBranch] = MarkerSha;
        if (change == "foreign") fixture.Handler.PreviewRepository = "other/repository";
        var result = JsonSerializer.SerializeToElement(await fixture.Service.PrepareAsync("teacher", fixture.Proposal, default));
        Assert.Equal("batch_replay_unverified", result.GetProperty("error").GetString());
        Assert.False(result.TryGetProperty("prepared", out _));
        Assert.Empty(fixture.Handler.Writes);
    }

    [Fact]
    public async Task ValidReplay_ReadsRemoteIdentity_WithoutRepeatingGitWrites()
    {
        using var fixture = new Fixture();
        await fixture.StageAsync();
        fixture.Handler.Writes.Clear();
        fixture.Handler.Reads.Clear();
        var result = JsonSerializer.SerializeToElement(await fixture.Service.PrepareAsync("teacher", fixture.Proposal, default));
        Assert.True(result.GetProperty("prepared").GetBoolean());
        Assert.True(result.GetProperty("replayed").GetBoolean());
        Assert.Contains(fixture.Handler.Reads, path => path.EndsWith("/git/ref/heads/" + PreviewBranch));
        Assert.Contains(fixture.Handler.Reads, path => path.EndsWith("/pulls/123"));
        Assert.Empty(fixture.Handler.Writes);
    }

    [Fact]
    public async Task ReadyPreview_CannotBeExtendedOrUsedForPublication()
    {
        using var fixture = new Fixture();
        await fixture.StageAsync();
        var originalOperation = (await fixture.Db.FounderSoftwareRepairBatches.SingleAsync()).OperationId;
        fixture.Handler.PreviewDraft = false;
        fixture.Handler.Writes.Clear();
        var next = fixture.Proposal with { BaseSha = HeadSha, Title = "Next bounded repair" };
        var stage = JsonSerializer.SerializeToElement(await fixture.Service.PrepareAsync("teacher", next, default));
        Assert.Equal("batch_precondition_changed", stage.GetProperty("error").GetString());
        Assert.Equal(originalOperation, (await fixture.Db.FounderSoftwareRepairBatches.SingleAsync()).OperationId);
        var retry = JsonSerializer.SerializeToElement(await fixture.Service.PrepareAsync("teacher", next, default));
        Assert.Equal("batch_precondition_changed", retry.GetProperty("error").GetString());
        var publish = JsonSerializer.SerializeToElement(await fixture.Service.ReleaseApprovedAsync(123, HeadSha, default));
        Assert.Equal("batch_publication_outcome_unknown", publish.GetProperty("error").GetString());
        Assert.Empty(fixture.Handler.Writes);
        Assert.Equal(HeadSha, fixture.Handler.Refs[PreviewBranch]);
    }

    [Fact]
    public async Task AmbiguousPublication_RemainsLocked_AndCannotRepeatRemoteWrites()
    {
        using var fixture = new Fixture();
        await fixture.StageAsync();
        fixture.Handler.FailPublicationUpdate = true;
        var result = JsonSerializer.SerializeToElement(await fixture.Service.ReleaseApprovedAsync(123, HeadSha, default));
        Assert.Equal("batch_publication_outcome_unknown", result.GetProperty("error").GetString());
        Assert.Equal("OutcomeUnknown", (await fixture.Db.FounderSoftwareRepairBatches.SingleAsync()).State);
        fixture.Handler.Writes.Clear();
        var retry = JsonSerializer.SerializeToElement(await fixture.Service.ReleaseApprovedAsync(123, HeadSha, default));
        Assert.Equal("batch_identity_changed", retry.GetProperty("error").GetString());
        Assert.Empty(fixture.Handler.Writes);
        Assert.Equal(HeadSha, fixture.Handler.Refs[PreviewBranch]);
    }

    [Fact]
    public async Task ExistingPublicationRef_IsNeverReusedOrOverwritten()
    {
        using var fixture = new Fixture();
        await fixture.StageAsync();
        fixture.Handler.Refs["hotfix/publish-" + HeadSha] = MarkerSha;
        fixture.Handler.Writes.Clear();
        var result = JsonSerializer.SerializeToElement(await fixture.Service.ReleaseApprovedAsync(123, HeadSha, default));
        Assert.Equal("batch_publication_outcome_unknown", result.GetProperty("error").GetString());
        Assert.Empty(fixture.Handler.Writes);
        Assert.Equal(MarkerSha, fixture.Handler.Refs["hotfix/publish-" + HeadSha]);
    }

    [Fact]
    public async Task MismatchedPublicationReceipt_CannotTriggerSynchronization()
    {
        using var fixture = new Fixture();
        await fixture.StageAsync();
        fixture.Handler.InvalidPublicationReceipt = true;
        fixture.Handler.Writes.Clear();
        var result = JsonSerializer.SerializeToElement(await fixture.Service.ReleaseApprovedAsync(123, HeadSha, default));
        Assert.Equal("batch_publication_outcome_unknown", result.GetProperty("error").GetString());
        Assert.Equal(2, fixture.Handler.Writes.Count);
        Assert.DoesNotContain(fixture.Handler.Writes, write => write.Path.Contains("/git/commits") || write.Path.Contains("/git/refs/heads/"));
        Assert.Equal("OutcomeUnknown", (await fixture.Db.FounderSoftwareRepairBatches.SingleAsync()).State);
    }

    [Theory]
    [InlineData("Root.csproj", "src/Example.cs", "100755")]
    [InlineData("App.xcodeproj/project.pbxproj", "Sources/Example.swift", "100644")]
    [InlineData("Mobile/App.xcodeproj/project.pbxproj", "Mobile/Sources/Example.swift", "100755")]
    public async Task Discovery_HandlesRootAndXcodeProjects_AndPreservesRegularFileMode(string project, string path, string mode)
    {
        using var fixture = new Fixture(project, path, mode);
        await fixture.StageAsync();
        var tree = Assert.Single(fixture.Handler.Writes.Where(write => write.Path.EndsWith("/git/trees"))).Body;
        var entry = Assert.Single(tree.GetProperty("tree").EnumerateArray());
        Assert.Equal(path, entry.GetProperty("path").GetString());
        Assert.Equal(mode, entry.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Discovery_RejectsSymlinkBeforeAnyRepositoryWrite()
    {
        using var fixture = new Fixture("Root.csproj", "src/Example.cs", "120000");
        var result = JsonSerializer.SerializeToElement(await fixture.Service.PrepareAsync("teacher", fixture.Proposal, default));
        Assert.Equal("batch_precondition_changed", result.GetProperty("error").GetString());
        Assert.Empty(fixture.Handler.Writes);
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("OutcomeUnknown")]
    public async Task Reconcile_ObservesMatchingPriorPreview_ButCannotUnlockExpiredOrUnknownOperation(string state)
    {
        using var fixture = new Fixture();
        await fixture.StageAsync();
        var batch = await fixture.Db.FounderSoftwareRepairBatches.SingleAsync();
        batch.State = state;
        batch.LeaseUntilUtc = DateTime.UtcNow.AddMinutes(-1);
        await fixture.Db.SaveChangesAsync();
        var revision = batch.Revision;
        fixture.Handler.Writes.Clear();
        var result = JsonSerializer.SerializeToElement(await fixture.Service.ReconcileBatchAsync(default));
        Assert.True(result.GetProperty("remoteIdentityMatches").GetBoolean());
        Assert.True(result.GetProperty("readOnly").GetBoolean());
        Assert.False(result.GetProperty("verified").GetBoolean());
        Assert.False(result.GetProperty("writesUnlocked").GetBoolean());
        Assert.Empty(fixture.Handler.Writes);
        Assert.Equal(state, batch.State);
        Assert.Equal(revision, batch.Revision);
        Assert.False(fixture.Db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task Reconcile_MissingIdentityRemainsLockedWithoutRemoteRequests()
    {
        using var fixture = new Fixture();
        fixture.Db.FounderSoftwareRepairBatches.Add(new() { State = "OutcomeUnknown" });
        await fixture.Db.SaveChangesAsync();
        var result = JsonSerializer.SerializeToElement(await fixture.Service.ReconcileBatchAsync(default));
        Assert.Equal("OutcomeUnknown", result.GetProperty("state").GetString());
        Assert.False(result.GetProperty("writesUnlocked").GetBoolean());
        Assert.Equal(0, fixture.Handler.TotalRequests);
    }

    [Fact]
    public async Task Reconcile_PublicationShowsExistingWorkflowObservation_WithoutClaimingLiveDeployment()
    {
        using var fixture = new Fixture();
        await fixture.StageAsync();
        await fixture.Service.ReleaseApprovedAsync(123, HeadSha, default);
        fixture.Handler.Writes.Clear();
        var result = JsonSerializer.SerializeToElement(await fixture.Service.ReconcileBatchAsync(default));
        Assert.True(result.GetProperty("remoteIdentityMatches").GetBoolean());
        Assert.False(result.GetProperty("verified").GetBoolean());
        Assert.False(result.GetProperty("workflowObservations").GetProperty("liveDeploymentVerified").GetBoolean());
        Assert.Empty(fixture.Handler.Writes);
        Assert.Equal("ValidationRequested", (await fixture.Db.FounderSoftwareRepairBatches.SingleAsync()).State);
    }

    [Theory]
    [InlineData("closed", true, false)]
    [InlineData("closed", true, true)]
    [InlineData("open", false, true)]
    public async Task Reconcile_ClosedOrMissingPublicationRefStillReadsWorkflowWithoutUnlocking(string state, bool merged, bool deleteRef)
    {
        using var fixture = new Fixture();
        await fixture.StageAsync();
        await fixture.Service.ReleaseApprovedAsync(123, HeadSha, default);
        fixture.Handler.PublicationState = state;
        fixture.Handler.PublicationMerged = merged;
        if (deleteRef) fixture.Handler.Refs.Remove("hotfix/publish-" + HeadSha);
        fixture.Handler.Writes.Clear();
        fixture.Handler.Reads.Clear();
        var result = JsonSerializer.SerializeToElement(await fixture.Service.ReconcileBatchAsync(default));
        Assert.True(result.GetProperty("publicationPullRequestIdentityMatches").GetBoolean());
        Assert.Equal(state, result.GetProperty("observedPullRequestState").GetString());
        Assert.Equal(merged, result.GetProperty("observedPullRequestMerged").GetBoolean());
        Assert.Equal(!deleteRef, result.GetProperty("remoteIdentityMatches").GetBoolean());
        Assert.False(result.GetProperty("verified").GetBoolean());
        Assert.False(result.GetProperty("writesUnlocked").GetBoolean());
        Assert.False(result.GetProperty("workflowObservations").GetProperty("liveDeploymentVerified").GetBoolean());
        Assert.Contains(fixture.Handler.Reads, path => path.EndsWith("/actions/workflows/agentportal-production-deploy.yml/runs"));
        Assert.Empty(fixture.Handler.Writes);
        Assert.Equal("ValidationRequested", (await fixture.Db.FounderSoftwareRepairBatches.SingleAsync()).State);
    }

    [Fact]
    public async Task ReconcileController_RequiresFounderAndAntiforgeryBeforeAnyRemoteRead()
    {
        using var fixture = new Fixture();
        var controller = new FounderDiagnosticsController(fixture.Db)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        await Assert.ThrowsAsync<ForbidResultException>(() => controller.RefreshBatchStatus(fixture.Service, default));
        Assert.NotNull(typeof(FounderDiagnosticsController).GetMethod(nameof(FounderDiagnosticsController.RefreshBatchStatus))!
            .GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        Assert.Equal(0, fixture.Handler.TotalRequests);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly RSA _key = RSA.Create(2048);
        public MasterAppDbContext Db { get; } = new(new DbContextOptionsBuilder<MasterAppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public ScenarioHandler Handler { get; }
        public FounderSoftwareRemediationService Service { get; }
        public FounderSoftwareRepairProposal Proposal { get; }
        public Fixture(string project = "AgentPortal/AgentPortal.csproj", string path = "AgentPortal/Services/Example.cs", string mode = "100644")
        {
            Handler = new ScenarioHandler(_key.ExportPkcs8PrivateKeyPem(), project, path, mode);
            Proposal = new(BaseSha, "Bounded repair", "Synthetic reviewed fixture", [new(path, "// corrected fixture")]);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FounderSoftwareRemediation:Enabled"] = "true",
                ["FounderSoftwareRemediation:RepositoryOwner"] = "MYLEGND",
                ["FounderSoftwareRemediation:RepositoryName"] = "masterapp",
                ["FounderSoftwareRemediation:BaseBranch"] = "production",
                ["FounderSoftwareRemediation:GitHubAppId"] = "1",
                ["FounderSoftwareRemediation:GitHubInstallationId"] = "2",
                ["FounderSoftwareRemediation:GitHubAppPrivateKeySecretUri"] = "https://fixture.vault.azure.net/secrets/app",
                ["FounderSoftwareRemediation:GitHubApiBaseUri"] = "https://api.github.com/"
            }).Build();
            Service = new(new ClientFactory(Handler), config, NullLogger<FounderSoftwareRemediationService>.Instance, new Credential(), Db);
        }
        public async Task StageAsync()
        {
            var result = JsonSerializer.SerializeToElement(await Service.PrepareAsync("teacher", Proposal, default));
            Assert.True(result.TryGetProperty("prepared", out var prepared) && prepared.GetBoolean(), result.ToString());
        }
        public void Dispose() { Handler.Dispose(); Db.Dispose(); _key.Dispose(); }
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, disposeHandler: false); }
    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken token) => new("synthetic", DateTimeOffset.UtcNow.AddMinutes(5));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken token) => ValueTask.FromResult(GetToken(context, token));
    }
    private sealed record Write(string Path, JsonElement Body);
    private sealed class ScenarioHandler(string key, string project, string source, string mode) : HttpMessageHandler
    {
        private const string Repo = "/repos/MYLEGND/masterapp/";
        public Dictionary<string, string> Refs { get; } = new() { ["production"] = BaseSha };
        public List<Write> Writes { get; } = [];
        public List<string> Reads { get; } = [];
        public bool PreviewDraft { get; set; } = true;
        public string PreviewRepository { get; set; } = "MYLEGND/masterapp";
        public bool FailPublicationUpdate { get; set; }
        public bool InvalidPublicationReceipt { get; set; }
        public string PublicationState { get; set; } = "open";
        public bool PublicationMerged { get; set; }
        public int TotalRequests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            TotalRequests++;
            var path = request.RequestUri!.AbsolutePath;
            if (request.RequestUri.Host == "fixture.vault.azure.net") return Json(new { value = key });
            if (path == "/app/installations/2/access_tokens") return Json(new { token = "synthetic-installation" });
            if (request.Method == HttpMethod.Get) Reads.Add(path);
            JsonElement body = default;
            if (request.Content is not null)
            {
                body = JsonSerializer.Deserialize<JsonElement>(await request.Content.ReadAsStringAsync(token));
                Writes.Add(new(path, body));
            }
            if (path.StartsWith(Repo + "git/ref/heads/") && request.Method == HttpMethod.Get)
                return Refs.TryGetValue(path[(Repo + "git/ref/heads/").Length..], out var sha)
                    ? Json(new { @object = new { sha } }) : Json(new { }, HttpStatusCode.NotFound);
            if (path.StartsWith(Repo + "git/commits/") && request.Method == HttpMethod.Get)
                return Json(new { tree = new { sha = path.EndsWith(BaseSha) ? new string('b', 40) : new string('e', 40) } });
            if (path.StartsWith(Repo + "git/trees/") && request.Method == HttpMethod.Get)
                return Json(new { truncated = false, tree = new[] { new { path = project, type = "blob", mode = "100644" }, new { path = source, type = "blob", mode } } });
            if (path == Repo + "git/blobs") return Json(new { sha = new string('d', 40) });
            if (path == Repo + "git/trees") return Json(new { sha = new string('e', 40) });
            if (path == Repo + "git/commits") return Json(new { sha = body.GetProperty("parents")[0].GetString() == BaseSha ? HeadSha : MarkerSha });
            if (path == Repo + "git/refs")
            {
                Refs.Add(body.GetProperty("ref").GetString()!["refs/heads/".Length..], body.GetProperty("sha").GetString()!);
                return Json(new { });
            }
            if (path.StartsWith(Repo + "git/refs/heads/") && request.Method == HttpMethod.Patch)
            {
                if (FailPublicationUpdate) throw new HttpRequestException("Synthetic ambiguous response");
                Refs[path[(Repo + "git/refs/heads/").Length..]] = body.GetProperty("sha").GetString()!;
                return Json(new { });
            }
            if (path == Repo + "pulls")
            {
                var draft = body.GetProperty("draft").GetBoolean();
                return Json(PullRequest(draft ? 123 : 456, body.GetProperty("head").GetString()!, draft));
            }
            if (path == Repo + "pulls/123") return Json(PullRequest(123, PreviewBranch, PreviewDraft));
            if (path == Repo + "pulls/456") return Json(PullRequest(456, "hotfix/publish-" + HeadSha, false));
            if (path == Repo + "actions/workflows/agentportal-production-deploy.yml/runs") return Json(new { workflow_runs = Array.Empty<object>() });
            if (path == Repo + "branches/production/protection")
                return Json(new { required_status_checks = new { strict = true, contexts = new[] { "security" } }, enforce_admins = new { enabled = true }, required_pull_request_reviews = new { } });
            return Json(new { }, HttpStatusCode.NotFound);
        }
        private object PullRequest(int number, string branch, bool draft) => new
        {
            number, draft, state = number == 456 ? PublicationState : "open", merged = number == 456 && PublicationMerged,
            head = new { sha = number == 456 && InvalidPublicationReceipt ? BaseSha : Refs.GetValueOrDefault(branch, MarkerSha), @ref = branch, repo = new { full_name = PreviewRepository } },
            @base = new { @ref = "production", repo = new { full_name = "MYLEGND/masterapp" } }
        };
        private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json") };
    }
}
