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
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

[Collection("LegendConnectFounderEnvironment")]
public sealed class LegendFounderCloudActionApprovalTests
{
    private const string FounderId = "587d1166-e29b-41d4-a716-446655440099";
    private const string ToolName = "legend_release_approved_repair";
    private static readonly string Arguments = JsonSerializer.Serialize(new
    {
        pull_request_number = 42, head_sha = new string('a', 40)
    });

    [Fact]
    public void ApprovalDigestsMatchWorkerInteroperabilityVectors()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName,
                   "Legend-Cloudflare", "tests", "security", "action-digest-vectors.json"))) directory = directory.Parent;
        Assert.NotNull(directory);
        using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName,
            "Legend-Cloudflare", "tests", "security", "action-digest-vectors.json")));
        foreach (var vector in vectors.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var action = vector.GetProperty("action");
            var wire = action.GetProperty("scope");
            var scope = new FounderAiActionScope(wire.GetProperty("accountId").GetString()!, wire.GetProperty("tenantId").GetString()!,
                wire.GetProperty("userId").GetString()!, wire.GetProperty("sessionId").GetString()!,
                wire.GetProperty("conversationId").GetString()!, action.GetProperty("requestId").GetString()!,
                wire.GetProperty("roles").EnumerateArray().Select(role => role.GetString()!).ToArray(),
                wire.GetProperty("authorizationVersion").GetString()!, action.GetProperty("environment").GetString()!,
                DateTime.UtcNow.AddSeconds(90), new string('f', 64));
            Assert.Equal(vector.GetProperty("canonicalAction").GetString(), LegendFounderToolAuthority.CanonicalCloudJson(action));
            Assert.Equal(vector.GetProperty("sha256").GetString(), LegendFounderToolAuthority.ComputeCloudActionDigest(scope,
                action.GetProperty("name").GetString()!, action.GetProperty("arguments").GetRawText()));
        }
    }

    [Theory]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("{\"nested\":{\"a\":1,\"a\":2}}")]
    [InlineData("{\"value\":1e999}")]
    public void CanonicalActionsRejectAmbiguousOrNonfiniteValues(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.ThrowsAny<ArgumentException>(() => LegendFounderToolAuthority.CanonicalCloudJson(document.RootElement));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelConfirmationAndLegacyCorrelationCannotCreateCloudApproval(bool extraConfirmation)
    {
        await using var fixture = await Fixture.CreateAsync();
        var call = fixture.Call() with { MutationAuthorization = new FounderAiMutationAuthorization(Guid.NewGuid().ToString("N")) };
        if (extraConfirmation) call = call with { Arguments = Arguments[..^1] + ",\"confirmed\":true}" };
        var output = await fixture.ExecuteAsync(call);
        Assert.Contains(extraConfirmation ? "cloud_action_arguments_invalid" : "cloud_action_approval_required", output);
        Assert.Empty(await fixture.Db.FounderAiActionAuthorizations.ToListAsync());
        fixture.Remediation.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApprovalPersistsAndExactReplayAcrossAuthoritiesReturnsOneTerminalReceipt(bool relational)
    {
        await using var fixture = await Fixture.CreateAsync(relational);
        var approved = await fixture.ApproveAsync();
        Assert.True(approved.Succeeded, approved.Error);
        var first = await fixture.ExecuteAsync(fixture.Call());
        var replay = await fixture.ExecuteAsync(fixture.Call());
        Assert.Equal(first, replay);
        Assert.Contains("publicationRequested", first);
        fixture.Remediation.Verify(service => service.ReleaseApprovedAsync(42, new string('a', 40), It.IsAny<CancellationToken>()), Times.Once);
        var row = await fixture.Db.FounderAiActionAuthorizations.AsNoTracking().SingleAsync();
        Assert.Equal("Completed", row.State);
        Assert.Equal(first, row.ResultJson);
        Assert.Equal(approved.ActionDigest, row.ActionDigest);
        Assert.Equal(fixture.Call().IdempotencyKey, row.IdempotencyKey);
        var anotherCall = fixture.Call("second-call");
        Assert.Contains("cloud_action_approval_consumed", await fixture.ExecuteAsync(anotherCall));
        fixture.Remediation.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("arguments")]
    [InlineData("account")]
    [InlineData("tenant")]
    [InlineData("user")]
    [InlineData("session")]
    [InlineData("conversation")]
    [InlineData("request")]
    [InlineData("roles")]
    [InlineData("version")]
    [InlineData("environment")]
    [InlineData("fingerprint")]
    [InlineData("expiry")]
    [InlineData("idempotency")]
    [InlineData("mode")]
    public async Task ChangedActionOrDelegatedScopeNeverConsumesOriginalApproval(string changed)
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.True((await fixture.ApproveAsync()).Succeeded);
        var scope = changed switch
        {
            "account" => fixture.Scope with { AccountId = "other-account" },
            "tenant" => fixture.Scope with { TenantId = "other-tenant" },
            "user" => fixture.Scope with { UserId = Guid.NewGuid().ToString("D") },
            "session" => fixture.Scope with { SessionId = "other-session" },
            "conversation" => fixture.Scope with { ConversationId = Guid.NewGuid().ToString("D") },
            "request" => fixture.Scope with { RequestId = Guid.NewGuid().ToString("D") },
            "roles" => fixture.Scope with { Roles = new[] { "Founder", "Administrator" } },
            "version" => fixture.Scope with { AuthorizationVersion = "revision-2" },
            "environment" => fixture.Scope with { Environment = "other-environment" },
            "fingerprint" => fixture.Scope with { RequestFingerprint = new string('b', 64) },
            "expiry" => fixture.Scope with { ExpiresUtc = fixture.Scope.ExpiresUtc.AddSeconds(1) },
            _ => fixture.Scope
        };
        var call = fixture.Call();
        if (changed == "arguments") call = call with { Arguments = Arguments.Replace(new string('a', 40), new string('b', 40), StringComparison.Ordinal) };
        if (changed == "idempotency") call = call with { IdempotencyKey = new string('0', 64) };
        var result = await fixture.NewAuthority().ExecuteAsync(fixture.Principal, call, changed == "mode" ? "teacher" : "legend",
            CancellationToken.None, serverDerivedScope: scope);
        Assert.Contains("cloud_action_", result);
        Assert.Equal("Approved", (await fixture.Db.FounderAiActionAuthorizations.AsNoTracking().SingleAsync()).State);
        fixture.Remediation.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("missing-session")]
    [InlineData("wrong-session")]
    [InlineData("inactive-profile")]
    [InlineData("closed-conversation")]
    [InlineData("terminal-operation")]
    [InlineData("expired-approval")]
    [InlineData("no-store")]
    public async Task CurrentAuthorityAndExpiryAreRecheckedBeforeDispatch(string revoked)
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.True((await fixture.ApproveAsync()).Succeeded);
        var principal = fixture.Principal;
        if (revoked == "missing-session") principal = ControllerTestHelpers.BuildUser(FounderId);
        if (revoked == "wrong-session") principal = WithSession("another-session");
        if (revoked == "inactive-profile") (await fixture.Db.AgentProfiles.SingleAsync()).IsActive = false;
        if (revoked == "closed-conversation") (await fixture.Db.MessageConversations.SingleAsync()).IsClosed = true;
        if (revoked == "expired-approval") (await fixture.Db.FounderAiActionAuthorizations.SingleAsync()).ExpiresUtc = DateTime.UtcNow.AddSeconds(-1);
        if (revoked == "terminal-operation")
        {
            using var services = fixture.Scopes.CreateScope();
            var completed = await services.ServiceProvider.GetRequiredService<IMessagingService>().CompleteFounderAiTurnAsync(new(
                new(FounderId, MessagingParticipantTypes.Agent), Guid.Parse(fixture.Scope.ConversationId), Guid.Parse(fixture.Scope.RequestId),
                fixture.UserMessageId, "Completed before callback.", MessagingAuthorKinds.Assistant, new(true, "legend")));
            Assert.True(completed.Succeeded);
        }
        await fixture.Db.SaveChangesAsync();
        var result = await fixture.NewAuthority(includeStore: revoked != "no-store").ExecuteAsync(principal, fixture.Call(), "legend",
            CancellationToken.None, serverDerivedScope: fixture.Scope);
        Assert.Contains("cloud_action_", result);
        fixture.Remediation.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UnknownExecutionCannotBeRetriedAfterAuthorityRestart()
    {
        await using var fixture = await Fixture.CreateAsync(true);
        Assert.True((await fixture.ApproveAsync()).Succeeded);
        fixture.Remediation.Setup(service => service.ReleaseApprovedAsync(42, new string('a', 40), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Synthetic lost acknowledgement after possible side effect."));
        Assert.Contains("cloud_action_outcome_unknown", await fixture.ExecuteAsync(fixture.Call()));
        Assert.Contains("cloud_action_approval_consumed", await fixture.ExecuteAsync(fixture.Call()));
        Assert.Equal("OutcomeUnknown", (await fixture.Db.FounderAiActionAuthorizations.AsNoTracking().SingleAsync()).State);
        fixture.Remediation.Verify(service => service.ReleaseApprovedAsync(42, new string('a', 40), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RelationalConcurrentClaimsDispatchExactlyOnce()
    {
        var barrier = new ClaimBarrier();
        await using var fixture = await Fixture.CreateAsync(true, barrier);
        Assert.True((await fixture.ApproveAsync()).Succeeded);
        barrier.Enabled = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var left = fixture.NewAuthority().ExecuteAsync(fixture.Principal, fixture.Call(), "legend", timeout.Token, serverDerivedScope: fixture.Scope);
        var right = fixture.NewAuthority().ExecuteAsync(fixture.Principal, fixture.Call(), "legend", timeout.Token, serverDerivedScope: fixture.Scope);
        var receipts = await Task.WhenAll(left, right);
        Assert.Single(receipts.Where(value => value.Contains("publicationRequested", StringComparison.Ordinal)));
        Assert.Single(receipts.Where(value => value.Contains("cloud_action_approval_claim_failed", StringComparison.Ordinal)));
        fixture.Remediation.Verify(service => service.ReleaseApprovedAsync(42, new string('a', 40), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("Completed", (await fixture.Db.FounderAiActionAuthorizations.AsNoTracking().SingleAsync()).State);
    }

    [Fact]
    public async Task RelationalUniqueDigestPreventsDuplicateIssuance()
    {
        await using var fixture = await Fixture.CreateAsync(true);
        var approved = await fixture.ApproveAsync();
        Assert.True(approved.Succeeded);
        var replay = await fixture.ApproveAsync();
        Assert.Equal(approved, replay);
        var duplicate = await fixture.Db.FounderAiActionAuthorizations.AsNoTracking().SingleAsync();
        duplicate.Id = Guid.NewGuid();
        fixture.Db.FounderAiActionAuthorizations.Add(duplicate);
        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Db.SaveChangesAsync());
        fixture.Remediation.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ApprovalWritesNeverSaveUnrelatedTrackedOperationalChanges()
    {
        await using var fixture = await Fixture.CreateAsync();
        var profile = await fixture.Db.AgentProfiles.SingleAsync();
        profile.ShortBio = "An unrelated unsaved operation.";
        Assert.True((await fixture.ApproveAsync()).Succeeded);
        await fixture.ExecuteAsync(fixture.Call());
        Assert.Null((await fixture.Db.AgentProfiles.AsNoTracking().SingleAsync()).ShortBio);
        Assert.Equal(EntityState.Modified, fixture.Db.Entry(profile).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApprovalIssuanceRequiresCurrentPendingScopeAndBoundedExpiry(bool longExpiry)
    {
        await using var fixture = await Fixture.CreateAsync();
        var receipt = await fixture.NewAuthority().IssueCloudActionApprovalAsync(
            longExpiry ? fixture.Principal : WithSession("foreign-session"), fixture.Scope, ToolName, Arguments,
            longExpiry ? fixture.Scope.ExpiresUtc.AddSeconds(1) : fixture.Scope.ExpiresUtc, CancellationToken.None);
        Assert.False(receipt.Succeeded);
        Assert.Empty(await fixture.Db.FounderAiActionAuthorizations.ToListAsync());
    }

    private static ClaimsPrincipal WithSession(string session)
    {
        var principal = ControllerTestHelpers.BuildUser(FounderId);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim("sid", session));
        return principal;
    }

    private sealed class ClaimBarrier : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _bothArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        public bool Enabled { get; set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<FounderAiActionAuthorization>()
                    .Any(entry => entry.State == EntityState.Modified && entry.Entity.State == "Executing"))
            {
                if (Interlocked.Increment(ref _arrivals) == 2) _bothArrived.TrySetResult();
                await _bothArrived.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string? _priorFounder;
        private readonly SqliteConnection? _keeper;
        private readonly DbContextOptions<MasterAppDbContext> _options;
        private readonly List<MasterAppDbContext> _authorityContexts = new();
        public MasterAppDbContext Db { get; }
        public IServiceScopeFactory Scopes { get; }
        public FounderAiActionScope Scope { get; }
        public ClaimsPrincipal Principal { get; } = WithSession("session-fixture");
        public Mock<IFounderSoftwareRemediationService> Remediation { get; } = new(MockBehavior.Strict);
        public Guid UserMessageId { get; private set; }

        private Fixture(MasterAppDbContext db, DbContextOptions<MasterAppDbContext> options, SqliteConnection? keeper)
        {
            _priorFounder = Environment.GetEnvironmentVariable("FOUNDER_OID");
            Environment.SetEnvironmentVariable("FOUNDER_OID", FounderId);
            Db = db; _options = options; _keeper = keeper;
            Scopes = ControllerTestHelpers.BuildFounderHistoryScopes(db);
            Scope = new("account-fixture", "tenant-fixture", FounderId, "session-fixture", Guid.NewGuid().ToString("D"),
                Guid.NewGuid().ToString("D"), new[] { "Founder", "Agent" }, "revision-1", "qualification", DateTime.UtcNow.AddSeconds(90), new string('a', 64));
            Remediation.Setup(service => service.ReleaseApprovedAsync(42, new string('a', 40), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new { publicationRequested = true, released = false, authority = "SyntheticExistingRemediationAuthority" });
        }

        public static async Task<Fixture> CreateAsync(bool relational = false, IInterceptor? interceptor = null)
        {
            SqliteConnection? keeper = null;
            var builder = new DbContextOptionsBuilder<MasterAppDbContext>();
            if (relational)
            {
                var connectionString = $"Data Source=cloud-approval-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=5";
                keeper = new SqliteConnection(connectionString);
                await keeper.OpenAsync();
                builder.UseSqlite(connectionString);
            }
            else builder.UseInMemoryDatabase(Guid.NewGuid().ToString()).ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning));
            if (interceptor is not null) builder.AddInterceptors(interceptor);
            var db = new MasterAppDbContext(builder.Options);
            var fixture = new Fixture(db, builder.Options, keeper);
            try
            {
                await db.Database.EnsureCreatedAsync();
                db.AgentProfiles.Add(new AgentProfile { AgentUserId = FounderId, AgentUpn = "fixture@example.invalid", IsActive = true });
                await db.SaveChangesAsync();
                using var services = fixture.Scopes.CreateScope();
                var scope = fixture.Scope;
                var begun = await services.ServiceProvider.GetRequiredService<IMessagingService>().BeginFounderAiTurnAsync(new(
                    new(FounderId, MessagingParticipantTypes.Agent), Guid.Parse(scope.ConversationId), Guid.Parse(scope.RequestId),
                    null, "Review the exact synthetic action.", "legend", scope.RequestFingerprint, scope.ExpiresUtc,
                    new(scope.AccountId, scope.TenantId, scope.UserId, scope.SessionId, scope.Roles, scope.AuthorizationVersion, scope.Environment, scope.ExpiresUtc)));
                Assert.True(begun.Succeeded, begun.ErrorMessage);
                fixture.UserMessageId = begun.UserMessage!.Id;
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public LegendFounderToolAuthority NewAuthority(bool includeStore = true)
        {
            var identityDb = new MasterAppDbContext(_options);
            _authorityContexts.Add(identityDb);
            return new LegendFounderToolAuthority(new FounderLegendConnectService(Mock.Of<ILegendConnectOperations>(),
                new AgentProfileAccessResolver(identityDb)), Remediation.Object, authorizationScopes: includeStore ? Scopes : null);
        }

        public FounderAiToolCall Call(string id = "call-fixture") => new(id, ToolName, Arguments,
            IdempotencyKey: LegendFounderToolAuthority.ComputeCloudToolIdempotencyKey(Scope.RequestId, id));
        public Task<FounderAiActionApprovalReceipt> ApproveAsync() => NewAuthority().IssueCloudActionApprovalAsync(
            Principal, Scope, ToolName, Arguments, Scope.ExpiresUtc, CancellationToken.None);
        public Task<string> ExecuteAsync(FounderAiToolCall call) => NewAuthority().ExecuteAsync(
            Principal, call, "legend", CancellationToken.None, serverDerivedScope: Scope);

        public async ValueTask DisposeAsync()
        {
            foreach (var context in _authorityContexts) await context.DisposeAsync();
            await Db.DisposeAsync();
            if (_keeper is not null) await _keeper.DisposeAsync();
            Environment.SetEnvironmentVariable("FOUNDER_OID", _priorFounder);
        }
    }
}
