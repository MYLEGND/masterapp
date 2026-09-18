using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

[Collection("LegendConnectFounderEnvironment")]
public sealed class LegendFounderCloudProposalTests
{
    private const string FounderId = "587d1166-e29b-41d4-a716-446655440099";
    private const string Tool = "legend_prepare_software_repair";
    private static readonly FounderSoftwareRepairProposal Patch = new(new string('a', 40), "Exact synthetic repair",
        "Review this fixed replacement.", [new("AgentPortal.Tests/Synthetic.cs", "// reviewed synthetic source\n")]);

    [Fact]
    public async Task UnapprovedCloudToolOnlyCreatesOneNonExecutableProposalAndReviewLink()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.ExecuteAsync(fixture.Source, Arguments(Patch));
        using var output = JsonDocument.Parse(first);
        Assert.True(output.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(output.RootElement.GetProperty("pendingApproval").GetBoolean());
        Assert.False(output.RootElement.GetProperty("executed").GetBoolean());
        Assert.False(output.RootElement.GetProperty("githubStaged").GetBoolean());
        Assert.StartsWith("/founder/legend-ai/actions/", output.RootElement.GetProperty("reviewUrl").GetString());
        Assert.Equal(first, await fixture.ExecuteAsync(fixture.Source, Arguments(Patch)));
        var row = await fixture.Db.FounderAiActionAuthorizations.AsNoTracking().SingleAsync();
        Assert.Equal("FounderProposal", row.AuthorizationKind);
        Assert.Equal("Proposed", row.State);
        Assert.Null(row.ApprovedUtc);
        Assert.Null(row.ParentProposalId);
        Assert.True(row.ExpiresUtc > fixture.Source.ExpiresUtc);
        Assert.True(row.ExpiresUtc <= DateTime.UtcNow.AddHours(24));
        fixture.Remediation.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosedSourceTurnCanBeReviewedThenApprovedInANewSessionAndFreshOperation(bool relational)
    {
        await using var fixture = await Fixture.CreateAsync(relational);
        var staged = await fixture.StageAsync();
        Assert.True(staged.Succeeded, staged.Error);
        var review = staged.Review!;
        await fixture.CompleteSourceAsync();
        using (var services = fixture.Scopes.CreateScope())
            Assert.Null(await services.ServiceProvider.GetRequiredService<IMessagingService>().GetFounderAiOperationDelegationAsync(
                new(FounderId, MessagingParticipantTypes.Agent), Guid.Parse(fixture.Source.ConversationId), Guid.Parse(fixture.Source.RequestId)));
        Assert.Equal(review, await fixture.Authority().GetCloudActionProposalAsync(fixture.Principal, review.ProposalId, CancellationToken.None));
        var fresh = await fixture.BeginApprovalAsync("fresh-session");
        Assert.NotEqual(fixture.Source.RequestId, fresh.RequestId);
        var approved = await fixture.ApproveAsync(review, fresh);
        Assert.True(approved.Succeeded, approved.Error);
        Assert.NotEqual(review.ReviewDigest, approved.ActionDigest);
        var output = await fixture.ExecuteAsync(fresh, review.CanonicalArgumentsJson);
        Assert.Contains("STAGED_UNPUBLISHED", output);
        Assert.Equal(output, await fixture.ExecuteAsync(fresh, review.CanonicalArgumentsJson));
        fixture.Remediation.Verify(service => service.PrepareAsync("founder",
            It.Is<FounderSoftwareRepairProposal>(proposal => proposal.BaseSha == Patch.BaseSha && proposal.Changes.Single().Content == Patch.Changes.Single().Content),
            It.IsAny<CancellationToken>()), Times.Once);
        fixture.Remediation.VerifyNoOtherCalls();
        var rows = await fixture.Db.FounderAiActionAuthorizations.AsNoTracking().ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal("Consumed", rows.Single(row => row.Id == review.ProposalId).State);
        var execution = rows.Single(row => row.ParentProposalId == review.ProposalId);
        Assert.Equal("Completed", execution.State);
        Assert.Equal("fresh-session", execution.SessionId);
        Assert.Equal(Guid.Parse(fresh.RequestId), execution.RequestId);
        Assert.Equal(review.CanonicalArgumentsJson, execution.CanonicalArgumentsJson);
        Assert.Equal(review.ReviewBindingJson, execution.ReviewBindingJson);
        Assert.False((await fixture.ApproveAsync(review, fresh)).Succeeded);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\t")]
    public async Task SourceWhitespaceIsPreservedExactlyInReviewedArguments(string whitespace)
    {
        await using var fixture = await Fixture.CreateAsync();
        var content = "// first" + whitespace + "// second";
        var staged = await fixture.StageAsync(Patch with { Changes = [new(Patch.Changes[0].Path, content)] });
        Assert.True(staged.Succeeded, staged.Error);
        using var arguments = JsonDocument.Parse(staged.Review!.CanonicalArgumentsJson);
        Assert.Equal(content, arguments.RootElement.GetProperty("changes")[0].GetProperty("content").GetString());
        Assert.NotNull(await fixture.Authority().GetCloudActionProposalAsync(fixture.Principal,
            staged.Review.ProposalId, CancellationToken.None));
        fixture.Remediation.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("\0")]
    [InlineData("\b")]
    [InlineData("\v")]
    [InlineData("\u0085")]
    public async Task OtherControlCharactersInSourceCannotCreateAProposal(string control)
    {
        await using var fixture = await Fixture.CreateAsync();
        var staged = await fixture.StageAsync(Patch with { Changes = [new(Patch.Changes[0].Path, "source" + control)] });
        Assert.False(staged.Succeeded);
        Assert.Empty(await fixture.Db.FounderAiActionAuthorizations.ToListAsync());
        fixture.Remediation.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SourceWhitespaceExceptionDoesNotApplyToProposalMetadata()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.False((await fixture.StageAsync(Patch with { Title = "Injected\ntitle" })).Succeeded);
        Assert.False((await fixture.StageAsync(Patch with { Summary = "Injected\tsummary" })).Succeeded);
        Assert.Empty(await fixture.Db.FounderAiActionAuthorizations.ToListAsync());
        fixture.Remediation.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(".github/workflows/untrusted.yml")]
    [InlineData("AgentPortal/Program.cs")]
    [InlineData("AgentPortal/appsettings.json")]
    [InlineData("AgentPortal/Security/Injected.cs")]
    [InlineData("AgentPortal/../outside.cs")]
    public async Task ExistingRepairPolicyRejectsPrivilegedOrEscapingPathsBeforeAProposalExists(string path)
    {
        await using var fixture = await Fixture.CreateAsync();
        var staged = await fixture.StageAsync(Patch with { Changes = [new(path, "untrusted")] });
        Assert.False(staged.Succeeded);
        Assert.Empty(await fixture.Db.FounderAiActionAuthorizations.ToListAsync());
        fixture.Remediation.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ModelConfirmationFieldsAndOriginalOperationCannotApprove()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Contains("cloud_action_arguments_invalid", await fixture.ExecuteAsync(fixture.Source, Arguments(Patch)[..^1] + ",\"confirmed\":true}"));
        var review = (await fixture.StageAsync()).Review!;
        Assert.False((await fixture.ApproveAsync(review, fixture.Source)).Succeeded);
        Assert.Equal("Proposed", (await fixture.Db.FounderAiActionAuthorizations.AsNoTracking().SingleAsync()).State);
        fixture.Remediation.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("FounderSoftwareRemediation:CandidateValidation:TrustedWorkflowSha")]
    [InlineData("FounderSoftwareRemediation:CandidateValidation:Profile")]
    [InlineData("FounderSoftwareRemediation:CandidateValidation:Enabled")]
    [InlineData("FounderSoftwareRemediation:RepositoryName")]
    public async Task MissingOperatorBindingCannotStageAProposal(string key)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Configuration[key] = null;
        var result = await fixture.StageAsync();
        Assert.False(result.Succeeded);
        Assert.Equal("cloud_proposal_policy_unavailable", result.Error);
        Assert.Empty(await fixture.Db.FounderAiActionAuthorizations.ToListAsync());
        fixture.Remediation.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UnlinkedGenericApprovalCannotInvokeFounderRepairAuthority()
    {
        await using var fixture = await Fixture.CreateAsync();
        var approval = await fixture.Authority().IssueCloudActionApprovalAsync(fixture.Principal, fixture.Source,
            Tool, Arguments(Patch), fixture.Source.ExpiresUtc, CancellationToken.None);
        Assert.True(approval.Succeeded, approval.Error);
        Assert.Contains("cloud_action_reviewed_proposal_required", await fixture.ExecuteAsync(fixture.Source, Arguments(Patch)));
        fixture.Remediation.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("digest")]
    [InlineData("arguments")]
    [InlineData("binding")]
    [InlineData("expiry")]
    [InlineData("workflow")]
    [InlineData("profile")]
    [InlineData("repository")]
    [InlineData("account")]
    [InlineData("environment")]
    [InlineData("disabled")]
    public async Task ChangedReviewOrOperatorPolicyCannotCreateApproval(string changed)
    {
        await using var fixture = await Fixture.CreateAsync();
        var review = (await fixture.StageAsync()).Review!;
        await fixture.CompleteSourceAsync();
        var fresh = await fixture.BeginApprovalAsync();
        var row = await fixture.Db.FounderAiActionAuthorizations.SingleAsync();
        if (changed == "arguments") row.CanonicalArgumentsJson = row.CanonicalArgumentsJson.Replace("reviewed synthetic", "substituted", StringComparison.Ordinal);
        if (changed == "binding") row.ReviewBindingJson = "{}";
        if (changed == "expiry") row.ExpiresUtc = DateTime.UtcNow.AddSeconds(-1);
        if (changed == "workflow") fixture.Configuration["FounderSoftwareRemediation:CandidateValidation:TrustedWorkflowSha"] = new string('b', 40);
        if (changed == "profile") fixture.Configuration["FounderSoftwareRemediation:CandidateValidation:Profile"] = "arbitrary-commands";
        if (changed == "repository") fixture.Configuration["FounderSoftwareRemediation:RepositoryName"] = "different-repository";
        if (changed == "account") fixture.Configuration["LegendConnect:Foundation:Cloudflare:AccountId"] = "another-account";
        if (changed == "environment") fixture.Configuration["LegendConnect:Foundation:Cloudflare:Environment"] = "production";
        if (changed == "disabled") fixture.Configuration["FounderSoftwareRemediation:CandidateValidation:Enabled"] = "false";
        await fixture.Db.SaveChangesAsync();
        var approval = await fixture.Authority().ApproveCloudActionProposalAsync(fixture.Principal, fresh, review.ProposalId,
            changed == "revision" ? new string('0', 32) : review.Revision,
            changed == "digest" ? new string('0', 64) : review.ReviewDigest, CancellationToken.None);
        Assert.False(approval.Succeeded);
        Assert.Single(await fixture.Db.FounderAiActionAuthorizations.ToListAsync());
        fixture.Remediation.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("user")]
    [InlineData("tenant")]
    [InlineData("closed")]
    [InlineData("inactive")]
    public async Task ReviewCannotDiscloseAnotherOwnerOrRevokedConversation(string changed)
    {
        await using var fixture = await Fixture.CreateAsync();
        var review = (await fixture.StageAsync()).Review!;
        if (changed == "closed") (await fixture.Db.MessageConversations.SingleAsync()).IsClosed = true;
        if (changed == "inactive") (await fixture.Db.AgentProfiles.SingleAsync()).IsActive = false;
        await fixture.Db.SaveChangesAsync();
        var principal = changed == "user" ? PrincipalFor("foreign-user", "tenant-fixture", "session-fixture") :
            changed == "tenant" ? PrincipalFor(FounderId, "other-tenant", "session-fixture") : fixture.Principal;
        Assert.Null(await fixture.Authority().GetCloudActionProposalAsync(principal, review.ProposalId, CancellationToken.None));
    }

    [Fact]
    public async Task MappedTenantClaimUsesTheExistingCanonicalIdentityResolver()
    {
        await using var fixture = await Fixture.CreateAsync();
        var review = (await fixture.StageAsync()).Review!;
        var identity = (ClaimsIdentity)fixture.Principal.Identity!;
        identity.RemoveClaim(identity.FindFirst("tid")!);
        identity.AddClaim(new("http://schemas.microsoft.com/identity/claims/tenantid", "TENANT-FIXTURE"));
        Assert.Equal(review, await fixture.Authority().GetCloudActionProposalAsync(fixture.Principal, review.ProposalId, CancellationToken.None));
        Assert.True((await fixture.StageAsync()).Succeeded);
    }

    [Fact]
    public async Task CompletedApprovalRechecksPinnedWorkflowBeforeAnyGitHubDispatch()
    {
        await using var fixture = await Fixture.CreateAsync();
        var review = (await fixture.StageAsync()).Review!;
        await fixture.CompleteSourceAsync();
        var fresh = await fixture.BeginApprovalAsync();
        Assert.True((await fixture.ApproveAsync(review, fresh)).Succeeded);
        fixture.Configuration["FounderSoftwareRemediation:CandidateValidation:TrustedWorkflowSha"] = new string('b', 40);
        Assert.Contains("cloud_action_reviewed_proposal_required", await fixture.ExecuteAsync(fresh, review.CanonicalArgumentsJson));
        fixture.Remediation.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UnknownGitHubOutcomeCannotCreateAnotherApprovalOrRetryExecution()
    {
        await using var fixture = await Fixture.CreateAsync(true);
        var review = (await fixture.StageAsync()).Review!;
        await fixture.CompleteSourceAsync();
        var fresh = await fixture.BeginApprovalAsync();
        Assert.True((await fixture.ApproveAsync(review, fresh)).Succeeded);
        fixture.Remediation.Setup(service => service.PrepareAsync("founder", It.IsAny<FounderSoftwareRepairProposal>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Synthetic unknown GitHub outcome."));
        Assert.Contains("cloud_action_outcome_unknown", await fixture.ExecuteAsync(fresh, review.CanonicalArgumentsJson));
        Assert.Contains("cloud_action_approval_consumed", await fixture.ExecuteAsync(fresh, review.CanonicalArgumentsJson));
        Assert.False((await fixture.ApproveAsync(review, fresh)).Succeeded);
        fixture.Remediation.Verify(service => service.PrepareAsync("founder", It.IsAny<FounderSoftwareRepairProposal>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RelationalConcurrentReviewConsumptionCreatesExactlyOneLinkedApproval()
    {
        var barrier = new ProposalBarrier();
        await using var fixture = await Fixture.CreateAsync(true, barrier);
        var review = (await fixture.StageAsync()).Review!;
        await fixture.CompleteSourceAsync();
        var fresh = await fixture.BeginApprovalAsync();
        barrier.Enabled = true;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var results = await Task.WhenAll(
            fixture.Authority().ApproveCloudActionProposalAsync(fixture.Principal, fresh, review.ProposalId, review.Revision, review.ReviewDigest, deadline.Token),
            fixture.Authority().ApproveCloudActionProposalAsync(fixture.Principal, fresh, review.ProposalId, review.Revision, review.ReviewDigest, deadline.Token));
        Assert.Single(results.Where(result => result.Succeeded));
        Assert.Single(results.Where(result => !result.Succeeded));
        Assert.Equal(2, await fixture.Db.FounderAiActionAuthorizations.CountAsync());
        Assert.Single(await fixture.Db.FounderAiActionAuthorizations.Where(row => row.ParentProposalId == review.ProposalId).ToListAsync());
        fixture.Remediation.VerifyNoOtherCalls();
    }

    private static string Arguments(FounderSoftwareRepairProposal patch) => JsonSerializer.Serialize(new
    {
        base_sha = patch.BaseSha, title = patch.Title, summary = patch.Summary,
        changes = patch.Changes.Select(change => new { path = change.Path, content = change.Content })
    });

    private static ClaimsPrincipal PrincipalFor(string user, string tenant, string session)
    {
        var principal = ControllerTestHelpers.BuildUser(user);
        var identity = (ClaimsIdentity)principal.Identity!;
        identity.AddClaim(new("tid", tenant)); identity.AddClaim(new("sid", session));
        return principal;
    }

    private sealed class ProposalBarrier : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        public bool Enabled { get; set; }
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<FounderAiActionAuthorization>()
                    .Any(entry => entry.State == EntityState.Modified && entry.Entity.State == "Consumed"))
            {
                if (Interlocked.Increment(ref _arrivals) == 2) _ready.TrySetResult();
                await _ready.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string? _priorFounder = Environment.GetEnvironmentVariable("FOUNDER_OID");
        private readonly SqliteConnection? _keeper;
        private readonly DbContextOptions<MasterAppDbContext> _options;
        private readonly ServiceProvider _services;
        private readonly List<MasterAppDbContext> _identityContexts = [];
        public MasterAppDbContext Db { get; }
        public IConfigurationRoot Configuration { get; }
        public IServiceScopeFactory Scopes { get; }
        public FounderAiActionScope Source { get; }
        public ClaimsPrincipal Principal { get; private set; } = PrincipalFor(FounderId, "tenant-fixture", "session-fixture");
        public Mock<IFounderSoftwareRemediationService> Remediation { get; } = new(MockBehavior.Strict);
        private Guid _sourceMessage;

        private Fixture(DbContextOptions<MasterAppDbContext> options, SqliteConnection? keeper)
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", FounderId);
            _keeper = keeper; _options = options; Db = new(options);
            Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FounderSoftwareRemediation:RepositoryOwner"] = "fixture",
                ["FounderSoftwareRemediation:RepositoryName"] = "source",
                ["FounderSoftwareRemediation:CandidateValidation:Enabled"] = "true",
                ["FounderSoftwareRemediation:CandidateValidation:TrustedWorkflowSha"] = new string('c', 40),
                ["FounderSoftwareRemediation:CandidateValidation:Profile"] = "cloudflare-contracts",
                ["LegendConnect:Foundation:Cloudflare:AccountId"] = "account-fixture",
                ["LegendConnect:Foundation:Cloudflare:Environment"] = "qualification"
            }).Build();
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(Configuration);
            services.AddScoped(_ => new MasterAppDbContext(options));
            ControllerTestHelpers.AddFounderHistoryServices(services);
            _services = services.BuildServiceProvider();
            Scopes = _services.GetRequiredService<IServiceScopeFactory>();
            Source = new("account-fixture", "tenant-fixture", FounderId, "session-fixture", Guid.NewGuid().ToString("D"),
                Guid.NewGuid().ToString("D"), ["Founder", "Agent"], "revision-1", "qualification", DateTime.UtcNow.AddSeconds(90), new string('a', 64));
            Remediation.Setup(service => service.PrepareAsync("founder", It.IsAny<FounderSoftwareRepairProposal>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new
                {
                    capability = "prepare_software_repair", prepared = true, state = "STAGED_UNPUBLISHED",
                    baseSha = Patch.BaseSha, repairCommitSha = new string('d', 40), branch = "hotfix/staging-batch",
                    pullRequestNumber = 42, replayed = false,
                    ci = "Draft batch does not authorize production execution.", deployment = "not_requested"
                });
        }

        public static async Task<Fixture> CreateAsync(bool relational = false, IInterceptor? interceptor = null)
        {
            var options = new DbContextOptionsBuilder<MasterAppDbContext>();
            SqliteConnection? keeper = null;
            if (relational)
            {
                var connection = $"Data Source=cloud-proposal-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=5";
                keeper = new SqliteConnection(connection); await keeper.OpenAsync(); options.UseSqlite(connection);
            }
            else options.UseInMemoryDatabase(Guid.NewGuid().ToString()).ConfigureWarnings(value => value.Ignore(InMemoryEventId.TransactionIgnoredWarning));
            if (interceptor is not null) options.AddInterceptors(interceptor);
            var fixture = new Fixture(options.Options, keeper);
            try
            {
                await fixture.Db.Database.EnsureCreatedAsync();
                fixture.Db.AgentProfiles.Add(new AgentProfile { AgentUserId = FounderId, AgentUpn = "fixture@example.invalid", IsActive = true });
                await fixture.Db.SaveChangesAsync();
                fixture._sourceMessage = await fixture.BeginAsync(fixture.Source, null);
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public LegendFounderToolAuthority Authority()
        {
            var identity = new MasterAppDbContext(_options); _identityContexts.Add(identity);
            return new(new FounderLegendConnectService(Mock.Of<ILegendConnectOperations>(), new AgentProfileAccessResolver(identity)),
                Remediation.Object, authorizationScopes: Scopes);
        }
        public Task<FounderAiActionProposalReceipt> StageAsync(FounderSoftwareRepairProposal? patch = null) =>
            Authority().StageCloudRepairProposalAsync(Principal, Source, patch ?? Patch, CancellationToken.None);
        public Task<FounderAiActionApprovalReceipt> ApproveAsync(FounderAiActionProposalReview review, FounderAiActionScope scope) =>
            Authority().ApproveCloudActionProposalAsync(Principal, scope, review.ProposalId, review.Revision, review.ReviewDigest, CancellationToken.None);
        public Task<string> ExecuteAsync(FounderAiActionScope scope, string arguments) => Authority().ExecuteAsync(Principal,
            new("reviewed-call", Tool, arguments, IdempotencyKey: LegendFounderToolAuthority.ComputeCloudToolIdempotencyKey(scope.RequestId, "reviewed-call")),
            "legend", CancellationToken.None, LegendConnectExternalProviderPolicy.CloudflareFoundation, serverDerivedScope: scope);
        public async Task CompleteSourceAsync()
        {
            using var services = Scopes.CreateScope();
            var result = await services.ServiceProvider.GetRequiredService<IMessagingService>().CompleteFounderAiTurnAsync(new(
                new(FounderId, MessagingParticipantTypes.Agent), Guid.Parse(Source.ConversationId), Guid.Parse(Source.RequestId),
                _sourceMessage, "Awaiting human review; no GitHub work performed.", MessagingAuthorKinds.Assistant, new(true, "legend")));
            Assert.True(result.Succeeded, result.ErrorMessage);
        }
        public async Task<FounderAiActionScope> BeginApprovalAsync(string session = "session-fixture")
        {
            Principal = PrincipalFor(FounderId, "tenant-fixture", session);
            var scope = Source with { RequestId = Guid.NewGuid().ToString("D"), SessionId = session,
                ExpiresUtc = DateTime.UtcNow.AddSeconds(90), RequestFingerprint = new string('b', 64) };
            var last = await Db.InternalMessages.AsNoTracking().OrderByDescending(row => row.SentUtc).ThenByDescending(row => row.Id).FirstAsync();
            await BeginAsync(scope, last.Id);
            return scope;
        }
        private async Task<Guid> BeginAsync(FounderAiActionScope scope, Guid? last)
        {
            using var services = Scopes.CreateScope();
            var result = await services.ServiceProvider.GetRequiredService<IMessagingService>().BeginFounderAiTurnAsync(new(
                new(FounderId, MessagingParticipantTypes.Agent), Guid.Parse(scope.ConversationId), Guid.Parse(scope.RequestId), last,
                "Synthetic exact proposal operation.", "legend", scope.RequestFingerprint, scope.ExpiresUtc,
                new(scope.AccountId, scope.TenantId, scope.UserId, scope.SessionId, scope.Roles, scope.AuthorizationVersion, scope.Environment, scope.ExpiresUtc)));
            Assert.True(result.Succeeded, result.ErrorMessage);
            return result.UserMessage!.Id;
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var context in _identityContexts) await context.DisposeAsync();
            await _services.DisposeAsync(); await Db.DisposeAsync();
            if (_keeper is not null) await _keeper.DisposeAsync();
            Environment.SetEnvironmentVariable("FOUNDER_OID", _priorFounder);
        }
    }
}
