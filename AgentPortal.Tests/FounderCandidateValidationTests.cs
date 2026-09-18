using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Services;
using Azure.Core;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

// Entirely synthetic HTTP and in-memory persistence. No token provider, GitHub
// endpoint, hosted job, model, Docker container, or paid resource is contacted.
public sealed class FounderCandidateValidationTests
{
    private static readonly string BaseSha = new('a', 40);
    private static readonly string HeadSha = new('c', 40);
    private static readonly string PatchSha = new('d', 64);
    private const string Repository = "MYLEGND/masterapp";
    private const string Workflow = "legend-candidate-validation.yml";
    private const string Branch = "legend/approved-changes";

    [Theory]
    [InlineData("legend")]
    [InlineData("teacher")]
    [InlineData("Founder")]
    public async Task OnlyExplicitFounderModeMayDispatch(string actor)
    {
        using var fixture = new Fixture();
        var result = await fixture.RequestAsync(actor: actor);
        Assert.Equal("explicit_founder_action_required", result.GetProperty("error").GetString());
        Assert.Equal(0, fixture.Handler.Requests);
    }

    [Fact]
    public async Task DisabledAndRevokedAuthorityNeverReachCredentialOrDispatchEndpoints()
    {
        using var fixture = new Fixture();
        fixture.Config["FounderSoftwareRemediation:CandidateValidation:Enabled"] = "false";
        Assert.Equal("candidate_validation_disabled", (await fixture.ReviewAsync()).GetProperty("error").GetString());
        fixture.Config["FounderSoftwareRemediation:CandidateValidation:Enabled"] = "true";
        fixture.Db.FounderSoftwareRemediationAuthorityStates.Add(new() { IsRevoked = true });
        await fixture.Db.SaveChangesAsync();
        Assert.Equal("software_remediation_not_configured", (await fixture.RequestAsync()).GetProperty("error").GetString());
        Assert.Equal(0, fixture.Handler.Requests);
    }

    [Fact]
    public async Task ReviewBindsCurrentRevisionAndClearlyLabelsUnverifiedPatch()
    {
        using var fixture = new Fixture();
        var review = await fixture.ReviewAsync();
        Assert.True(review.GetProperty("canRequest").GetBoolean());
        Assert.False(review.GetProperty("patchDigestVerified").GetBoolean());
        Assert.False(review.GetProperty("deploymentAuthorized").GetBoolean());
        var digest = review.GetProperty("approvalActionDigest").GetString()!;
        fixture.Batch.Revision = Guid.NewGuid().ToString("N");
        await fixture.Db.SaveChangesAsync();
        var result = await fixture.RequestAsync(digest: digest);
        Assert.Equal("candidate_review_changed", result.GetProperty("error").GetString());
        Assert.Equal(0, fixture.Handler.Requests);
    }

    [Fact]
    public async Task CallerCannotReplaceConfiguredTrustedWorkflow()
    {
        using var fixture = new Fixture();
        var result = JsonSerializer.SerializeToElement(await fixture.Service.GetCandidateValidationReviewAsync(
            123, HeadSha, BaseSha, PatchSha, new string('f', 40), default));
        Assert.Equal("candidate_trusted_workflow_mismatch", result.GetProperty("error").GetString());
        Assert.Equal(0, fixture.Handler.Requests);
    }

    [Theory]
    [InlineData("disable")]
    [InlineData("rotate")]
    public async Task OperatorPolicyChangeDuringPreflightPreventsDispatch(string mode)
    {
        using var fixture = new Fixture();
        fixture.Handler.AfterTokenRequest = count =>
        {
            if (count != 2) return;
            if (mode == "disable") fixture.Config["FounderSoftwareRemediation:CandidateValidation:Enabled"] = "false";
            else fixture.Config["FounderSoftwareRemediation:CandidateValidation:TrustedWorkflowSha"] = new string('f', 40);
        };
        var result = await fixture.RequestAsync();
        Assert.Equal("candidate_validation_configuration_changed", result.GetProperty("error").GetString());
        Assert.Equal(0, fixture.Handler.Dispatches);
        Assert.Equal("Staged", fixture.Batch.State);
        Assert.Null(fixture.Batch.CandidateValidationEvidenceJson);
    }

    [Fact]
    public async Task DisabledInspectionCannotIssueCredentials()
    {
        using var fixture = new Fixture();
        fixture.Config["FounderSoftwareRemediation:CandidateValidation:Enabled"] = "false";
        var result = JsonSerializer.SerializeToElement(await fixture.Service.GetCandidateValidationAsync(42, HeadSha, default));
        Assert.Equal("candidate_validation_disabled", result.GetProperty("error").GetString());
        Assert.Equal(0, fixture.Handler.Requests);
    }

    [Fact]
    public async Task DispatchPersistsIntentFirstAndReplaysWithoutSecondRemoteWrite()
    {
        using var fixture = new Fixture();
        fixture.Handler.BeforeDispatch = () =>
        {
            using var observer = new MasterAppDbContext(fixture.Options);
            var saved = observer.FounderSoftwareRepairBatches.Single();
            Assert.Equal("CandidateValidationDispatching", saved.State);
            Assert.NotNull(saved.CandidateValidationEvidenceJson);
        };
        var result = await fixture.RequestAsync();
        Assert.Equal(42, result.GetProperty("runId").GetInt64());
        Assert.Equal("Requested", result.GetProperty("state").GetString());
        Assert.False(result.GetProperty("mergeAuthorized").GetBoolean());
        Assert.False(result.GetProperty("deploymentAuthorized").GetBoolean());
        var replay = await fixture.RequestAsync();
        Assert.True(replay.GetProperty("replayed").GetBoolean());
        Assert.Equal(1, fixture.Handler.Dispatches);
        Assert.Equal("CandidateValidationRequested", fixture.Batch.State);
        var dispatch = fixture.Handler.LastDispatch;
        Assert.Equal(Branch, dispatch.GetProperty("ref").GetString());
        Assert.True(dispatch.GetProperty("return_run_details").GetBoolean());
        using var request = JsonDocument.Parse(dispatch.GetProperty("inputs").GetProperty("request").GetString()!);
        Assert.Equal(HeadSha, request.RootElement.GetProperty("candidateSha").GetString());
        Assert.Equal(PatchSha, request.RootElement.GetProperty("patchSha256").GetString());
        Assert.Equal(BaseSha, request.RootElement.GetProperty("trustedWorkflowSha").GetString());
        Assert.Equal(2, fixture.Handler.TokenRequests.Count);
        var preflight = fixture.Handler.TokenRequests[0];
        var write = fixture.Handler.TokenRequests[1];
        Assert.Equal("read", preflight.GetProperty("permissions").GetProperty("pull_requests").GetString());
        Assert.Equal("read", preflight.GetProperty("permissions").GetProperty("actions").GetString());
        Assert.Equal("write", write.GetProperty("permissions").GetProperty("actions").GetString());
        Assert.Equal("read", write.GetProperty("permissions").GetProperty("contents").GetString());
        Assert.False(write.GetProperty("permissions").TryGetProperty("pull_requests", out _));
        Assert.Equal("masterapp", Assert.Single(write.GetProperty("repositories").EnumerateArray()).GetString());
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("empty")]
    public async Task UnknownDispatchNeverRetriesOrAcceptsGuessedRun(string scenario)
    {
        using var fixture = new Fixture();
        fixture.Handler.DispatchScenario = scenario;
        var result = await fixture.RequestAsync();
        Assert.Equal("candidate_dispatch_outcome_unknown", result.GetProperty("error").GetString());
        Assert.Equal("CandidateValidationUnknown", fixture.Batch.State);
        var retry = await fixture.RequestAsync();
        Assert.Equal("OutcomeUnknown", retry.GetProperty("state").GetString());
        Assert.Equal(1, fixture.Handler.Dispatches);
        var requests = fixture.Handler.Requests;
        var observation = JsonSerializer.SerializeToElement(await fixture.Service.GetCandidateValidationAsync(42, HeadSha, default));
        Assert.Equal("candidate_run_not_bound", observation.GetProperty("error").GetString());
        Assert.Equal(requests, fixture.Handler.Requests);
        Assert.Equal("CandidateValidationUnknown", fixture.Batch.State);
    }

    [Theory]
    [InlineData("extra_permission", "candidate_github_token_scope_unverified")]
    [InlineData("foreign_repository", "candidate_github_token_scope_unverified")]
    [InlineData("missing_grant", "candidate_github_capability_missing")]
    public async Task TokenScopeFailuresStopBeforeDispatch(string mode, string code)
    {
        using var fixture = new Fixture();
        fixture.Handler.TokenScenario = mode;
        Assert.Equal(code, (await fixture.RequestAsync()).GetProperty("error").GetString());
        Assert.Equal(0, fixture.Handler.Dispatches);
        Assert.Equal("Staged", fixture.Batch.State);
        Assert.Null(fixture.Batch.CandidateValidationEvidenceJson);
    }

    [Theory]
    [InlineData(".github/workflows/unsafe.yml")]
    [InlineData("Legend-Cloudflare/tests/runtime/runtime.test.mjs")]
    [InlineData("Legend-Cloudflare/src/security/governance.mjs")]
    [InlineData("Legend-Cloudflare/src/runtime/registry.mjs")]
    [InlineData("Directory.Build.props")]
    public async Task PrivilegedTreeChangesCannotDispatch(string path)
    {
        using var fixture = new Fixture();
        fixture.Handler.ChangedPath = path;
        Assert.Equal("candidate_privileged_review_required", (await fixture.RequestAsync()).GetProperty("error").GetString());
        Assert.Equal(0, fixture.Handler.Dispatches);
    }

    [Theory]
    [InlineData("base", "candidate_base_mismatch")]
    [InlineData("approved_ref", "candidate_trusted_branch_moved")]
    [InlineData("symlink", "candidate_file_mode_not_allowed")]
    [InlineData("truncated", "candidate_tree_incomplete")]
    public async Task PreflightRequiresExactImmutableIdentity(string mode, string code)
    {
        using var fixture = new Fixture();
        fixture.Handler.PreflightScenario = mode;
        Assert.Equal(code, (await fixture.RequestAsync()).GetProperty("error").GetString());
        Assert.Equal(0, fixture.Handler.Dispatches);
        Assert.Equal("Staged", fixture.Batch.State);
    }

    [Fact]
    public async Task ForeignPreviewNeverDispatches()
    {
        using var fixture = new Fixture();
        fixture.Handler.PreflightScenario = "foreign_pr";
        Assert.Equal("candidate_validation_precondition_failed", (await fixture.RequestAsync()).GetProperty("error").GetString());
        Assert.Equal(0, fixture.Handler.Dispatches);
    }

    [Theory]
    [InlineData("success", true)]
    [InlineData("failure", false)]
    public async Task ExactTerminalRunUnlocksFurtherEditingWithoutReleaseAuthority(string conclusion, bool passed)
    {
        using var fixture = new Fixture();
        await fixture.RequestAsync();
        fixture.Handler.Conclusion = conclusion;
        var result = JsonSerializer.SerializeToElement(await fixture.Service.GetCandidateValidationAsync(42, HeadSha, default));
        Assert.Equal("Completed", result.GetProperty("state").GetString());
        Assert.Equal(passed, result.GetProperty("observedChecksPassed").GetBoolean());
        Assert.False(result.GetProperty("artifactVerified").GetBoolean());
        Assert.False(result.GetProperty("patchDigestVerified").GetBoolean());
        Assert.False(result.GetProperty("mergeAuthorized").GetBoolean());
        Assert.True(result.GetProperty("canEdit").GetBoolean());
        Assert.Equal("Staged", fixture.Batch.State);
        var revision = fixture.Batch.Revision;
        await fixture.Service.GetCandidateValidationAsync(42, HeadSha, default);
        Assert.Equal(revision, fixture.Batch.Revision);
        await fixture.RequestAsync();
        Assert.Equal(1, fixture.Handler.Dispatches);
    }

    [Theory]
    [InlineData("wrong_sha")]
    [InlineData("wrong_workflow")]
    [InlineData("rerun")]
    [InlineData("foreign_run")]
    [InlineData("wrong_job")]
    public async Task UntrustedRunOrJobCannotUnlockCandidate(string mode)
    {
        using var fixture = new Fixture();
        await fixture.RequestAsync();
        fixture.Handler.RunScenario = mode;
        var result = JsonSerializer.SerializeToElement(await fixture.Service.GetCandidateValidationAsync(42, HeadSha, default));
        Assert.StartsWith("candidate_", result.GetProperty("error").GetString());
        Assert.Equal("CandidateValidationRequested", fixture.Batch.State);
        Assert.Equal(1, fixture.Handler.Dispatches);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly RSA _key = RSA.Create(2048);
        public DbContextOptions<MasterAppDbContext> Options { get; } = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        public MasterAppDbContext Db { get; }
        public FounderSoftwareRepairBatch Batch { get; }
        public ScenarioHandler Handler { get; }
        public IConfigurationRoot Config { get; }
        public FounderSoftwareRemediationService Service { get; }
        public Fixture()
        {
            Db = new(Options);
            Batch = new() { BaseSha = BaseSha, HeadSha = HeadSha, PullRequestNumber = 123, State = "Staged", UpdatedUtc = DateTime.UtcNow };
            Db.FounderSoftwareRepairBatches.Add(Batch);
            Db.SaveChanges();
            Handler = new(_key.ExportPkcs8PrivateKeyPem());
            Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FounderSoftwareRemediation:Enabled"] = "true",
                ["FounderSoftwareRemediation:RepositoryOwner"] = "MYLEGND",
                ["FounderSoftwareRemediation:RepositoryName"] = "masterapp",
                ["FounderSoftwareRemediation:BaseBranch"] = "production",
                ["FounderSoftwareRemediation:GitHubAppId"] = "1",
                ["FounderSoftwareRemediation:GitHubInstallationId"] = "2",
                ["FounderSoftwareRemediation:GitHubAppPrivateKeySecretUri"] = "https://fixture.vault.azure.net/secrets/app",
                ["FounderSoftwareRemediation:GitHubApiBaseUri"] = "https://api.github.com/",
                ["FounderSoftwareRemediation:CandidateValidation:Enabled"] = "true",
                ["FounderSoftwareRemediation:CandidateValidation:TrustedWorkflowSha"] = BaseSha
            }).Build();
            Service = new(new ClientFactory(Handler), Config, NullLogger<FounderSoftwareRemediationService>.Instance, new Credential(), Db);
        }
        public async Task<JsonElement> ReviewAsync() => JsonSerializer.SerializeToElement(await Service.GetCandidateValidationReviewAsync(123, HeadSha, BaseSha, PatchSha, BaseSha, default));
        public async Task<JsonElement> RequestAsync(string? digest = null, string actor = "founder")
        {
            digest ??= JsonSerializer.Deserialize<JsonElement>(Batch.CandidateValidationEvidenceJson ?? "{}").TryGetProperty("approvalActionDigest", out var stored)
                ? stored.GetString() : null;
            if (digest is null && actor == "founder")
            {
                var review = await ReviewAsync();
                if (review.TryGetProperty("approvalActionDigest", out var value)) digest = value.GetString();
            }
            return JsonSerializer.SerializeToElement(await Service.RequestCandidateValidationAsync(actor, 123, HeadSha, BaseSha, PatchSha, BaseSha, digest ?? new string('a', 64), default));
        }
        public void Dispose() { Handler.Dispose(); Db.Dispose(); _key.Dispose(); }
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, disposeHandler: false); }
    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken token) => new("synthetic-not-a-credential", DateTimeOffset.UtcNow.AddMinutes(5));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken token) => ValueTask.FromResult(GetToken(context, token));
    }
    private sealed class ScenarioHandler(string key) : HttpMessageHandler
    {
        private const string Repo = "/repos/" + Repository + "/";
        private static readonly string BaseTree = new('b', 40);
        private static readonly string HeadTree = new('e', 40);
        public int Requests { get; private set; }
        public int Dispatches { get; private set; }
        public List<JsonElement> TokenRequests { get; } = [];
        public JsonElement LastDispatch { get; private set; }
        public Action? BeforeDispatch { get; set; }
        public Action<int>? AfterTokenRequest { get; set; }
        public string? DispatchScenario { get; set; }
        public string? TokenScenario { get; set; }
        public string? PreflightScenario { get; set; }
        public string? RunScenario { get; set; }
        public string Conclusion { get; set; } = "success";
        public string ChangedPath { get; set; } = "Legend-Cloudflare/src/runtime/adapter.mjs";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            var path = request.RequestUri!.AbsolutePath;
            if (request.RequestUri.Host == "fixture.vault.azure.net") return Json(new { value = key });
            if (path == "/app/installations/2/access_tokens")
            {
                var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(token));
                TokenRequests.Add(body);
                AfterTokenRequest?.Invoke(TokenRequests.Count);
                if (TokenScenario == "missing_grant") return Json(new { }, HttpStatusCode.Forbidden);
                var permissions = body.GetProperty("permissions").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
                if (TokenScenario == "extra_permission") permissions["administration"] = "write";
                return Json(new { token = "synthetic-installation", permissions,
                    repositories = new[] { new { full_name = TokenScenario == "foreign_repository" ? "foreign/repo" : Repository } } });
            }
            if (path == Repo + "git/ref/heads/hotfix/staging-batch") return Json(new { @object = new { sha = HeadSha } });
            if (path == Repo + "git/ref/heads/" + Branch)
                return Json(new { @object = new { sha = PreflightScenario == "approved_ref" ? HeadSha : BaseSha } });
            if (path == Repo + "pulls/123") return Json(new
            {
                number = 123, draft = true, state = "open",
                head = new { sha = HeadSha, @ref = "hotfix/staging-batch", repo = new { full_name = PreflightScenario == "foreign_pr" ? "foreign/repo" : Repository } },
                @base = new { @ref = "production", repo = new { full_name = Repository } }
            });
            if (path.StartsWith(Repo + "git/commits/", StringComparison.Ordinal))
            {
                var sha = path[(Repo + "git/commits/").Length..];
                return Json(new { sha, tree = new { sha = sha == BaseSha ? BaseTree : HeadTree },
                    parents = new[] { new { sha = PreflightScenario == "base" ? new string('f', 40) : BaseSha } } });
            }
            if (path.StartsWith(Repo + "git/trees/", StringComparison.Ordinal))
                return Json(new { truncated = PreflightScenario == "truncated", tree = new[] { new
                {
                    path = ChangedPath, type = "blob", mode = PreflightScenario == "symlink" ? "120000" : "100644",
                    sha = path.EndsWith(BaseTree, StringComparison.Ordinal) ? new string('f', 40) : new string('d', 40)
                } } });
            if (path == Repo + "actions/workflows/" + Workflow) return Json(new { path = ".github/workflows/" + Workflow, state = "active" });
            if (path == Repo + "actions/workflows/" + Workflow + "/dispatches")
            {
                BeforeDispatch?.Invoke();
                Dispatches++;
                LastDispatch = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(token));
                if (DispatchScenario == "timeout") throw new HttpRequestException("Synthetic lost dispatch acknowledgment");
                if (DispatchScenario == "empty") return new(HttpStatusCode.NoContent);
                return Json(new { workflow_run_id = 42 });
            }
            if (path == Repo + "actions/runs/42") return Json(new
            {
                id = 42, repository = new { full_name = Repository },
                head_repository = new { full_name = RunScenario == "foreign_run" ? "foreign/repo" : Repository },
                head_sha = RunScenario == "wrong_sha" ? HeadSha : BaseSha, head_branch = Branch,
                path = ".github/workflows/" + (RunScenario == "wrong_workflow" ? "other.yml" : Workflow),
                @event = "workflow_dispatch", run_attempt = RunScenario == "rerun" ? 2 : 1,
                status = "completed", conclusion = Conclusion
            });
            if (path == Repo + "actions/runs/42/attempts/1/jobs") return Json(new { total_count = 1, jobs = new[] { new
            {
                name = RunScenario == "wrong_job" ? "other" : "validate", head_sha = BaseSha,
                run_id = 42, status = "completed", conclusion = Conclusion
            } } });
            throw new InvalidOperationException("Unexpected synthetic HTTP request: " + path);
        }
        private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}
