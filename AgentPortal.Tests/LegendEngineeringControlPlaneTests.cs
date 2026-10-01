using AgentPortal.Services.Engineering;
using Domain.Engineering;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendEngineeringControlPlaneTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MasterAppDbContext _db;
    private readonly LegendEngineeringStateStore _store;

    public LegendEngineeringControlPlaneTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new MasterAppDbContext(options);
        CreateControlPlaneTables();
        _store = new LegendEngineeringStateStore(_db);
    }

    [Fact]
    public void PriorityPolicy_UsesDocumentedFixedWeights()
    {
        Assert.Equal(35, LegendEngineeringPolicies.WeightedPriority(100, 0, 0, 0, 0, 0));
        Assert.Equal(25, LegendEngineeringPolicies.WeightedPriority(0, 100, 0, 0, 0, 0));
        Assert.Equal(15, LegendEngineeringPolicies.WeightedPriority(0, 0, 100, 0, 0, 0));
        Assert.Equal(10, LegendEngineeringPolicies.WeightedPriority(0, 0, 0, 100, 0, 0));
        Assert.Equal(10, LegendEngineeringPolicies.WeightedPriority(0, 0, 0, 0, 100, 0));
        Assert.Equal(5, LegendEngineeringPolicies.WeightedPriority(0, 0, 0, 0, 0, 100));
        Assert.Equal(100, LegendEngineeringPolicies.WeightedPriority(100, 100, 100, 100, 100, 100));
    }

    [Fact]
    public void P1_BypassesBatchingDelay()
    {
        Assert.Equal("IMMEDIATE", LegendEngineeringPolicies.ReleaseCohort("P1"));
        Assert.Equal("FOUR_HOUR_MAX", LegendEngineeringPolicies.ReleaseCohort("P2"));
        Assert.Equal("DAILY", LegendEngineeringPolicies.ReleaseCohort("P3"));
        Assert.Equal("WEEKLY", LegendEngineeringPolicies.ReleaseCohort("P4"));
    }

    [Fact]
    public void ConfigurationFailure_DoesNotCreateCodeRepair()
    {
        var decision = LegendEngineeringPolicies.Classify(Incident(
            category: "Configuration", error: "MissingSetting", source: "AgentPortal/Controllers/HomeController.cs"));
        Assert.Equal(EngineeringFailureClass.ConfigurationDefect, decision.FailureClass);
        Assert.False(decision.CodeRepairEligible);
        Assert.Equal(EngineeringRole.Sentinel, decision.AssignedRole);
    }

    [Fact]
    public void ProviderFailure_DoesNotCreateCodeRepair()
    {
        var decision = LegendEngineeringPolicies.Classify(Incident(
            category: "Network", error: "ProviderTimeout", source: "AgentPortal/Controllers/HomeController.cs"));
        Assert.Equal(EngineeringFailureClass.NetworkProviderFailure, decision.FailureClass);
        Assert.False(decision.CodeRepairEligible);
    }

    [Fact]
    public void ProtectedSource_IsTierC_AndNeverCodeEligible()
    {
        var decision = LegendEngineeringPolicies.Classify(Incident(
            category: "Runtime", error: "Exception", source: "AgentPortal/Program.cs"));
        Assert.Equal(EngineeringRiskClass.TierC, decision.RiskClass);
        Assert.False(decision.CodeRepairEligible);
    }

    [Fact]
    public void Difficulty_DoesNotIncreaseAuthorization()
    {
        var protectedDecision = LegendEngineeringPolicies.Classify(Incident(
            category: "Runtime", error: "Exception", source: "AgentPortal/Program.cs", occurrences: 500));
        Assert.True(protectedDecision.ComplexityScore >= 70);
        Assert.Equal(EngineeringRiskClass.TierC, protectedDecision.RiskClass);
        Assert.False(protectedDecision.CodeRepairEligible);
    }

    [Fact]
    public void DisjointAppImpactSets_MayRunConcurrently()
    {
        var a = LegendEngineeringPolicies.Classify(Incident(
            app: "AgentPortal", source: "AgentPortal/Controllers/HomeController.cs"));
        var b = LegendEngineeringPolicies.Classify(Incident(
            app: "ClientApp", source: "ClientApp/Controllers/HomeController.cs"));
        Assert.False(LegendEngineeringPolicies.ImpactSetsOverlap(a.ImpactSet, b.ImpactSet));
    }

    [Fact]
    public void SharedAuthority_SerializesWithApplicationWork()
    {
        var shared = LegendEngineeringPolicies.Classify(Incident(
            app: "AgentPortal", source: "SHARED/Diagnostics/LegendSiteToolDisclosureAuthority.cs"));
        var app = LegendEngineeringPolicies.Classify(Incident(
            app: "ClientApp", source: "ClientApp/Controllers/HomeController.cs"));
        Assert.True(LegendEngineeringPolicies.ImpactSetsOverlap(shared.ImpactSet, app.ImpactSet));
    }

    [Fact]
    public async Task MatchingIncidents_MergeIntoOneDurableWorkItem()
    {
        var one = Incident(source: "AgentPortal/Controllers/HomeController.cs");
        var two = Incident(source: "AgentPortal/Controllers/HomeController.cs");
        var first = await _store.AttachIncidentAsync(one, LegendEngineeringPolicies.Classify(one), default);
        var second = await _store.AttachIncidentAsync(two, LegendEngineeringPolicies.Classify(two), default);
        Assert.Equal(first.WorkItemId, second.WorkItemId);
        Assert.Equal(2, second.IncidentIds.Count);
    }

    [Fact]
    public async Task SecondOwner_CannotLeaseSameWorkItem()
    {
        var incident = Incident(source: "AgentPortal/Controllers/HomeController.cs");
        var item = await _store.AttachIncidentAsync(incident, LegendEngineeringPolicies.Classify(incident), default);
        var first = await _store.TryAcquireLeaseAsync(item.WorkItemId, "owner-a", TimeSpan.FromMinutes(10), default);
        var second = await _store.TryAcquireLeaseAsync(item.WorkItemId, "owner-b", TimeSpan.FromMinutes(10), default);
        Assert.True(first.Acquired);
        Assert.False(second.Acquired);
        Assert.Equal("work_item_already_leased", second.Code);
    }

    [Fact]
    public async Task SameOwner_LeaseReplay_IsIdempotent()
    {
        var incident = Incident(source: "AgentPortal/Controllers/HomeController.cs");
        var item = await _store.AttachIncidentAsync(incident, LegendEngineeringPolicies.Classify(incident), default);
        var first = await _store.TryAcquireLeaseAsync(item.WorkItemId, "owner-a", TimeSpan.FromMinutes(10), default);
        var replay = await _store.TryAcquireLeaseAsync(item.WorkItemId, "owner-a", TimeSpan.FromMinutes(10), default);
        Assert.True(replay.Acquired);
        Assert.Equal("lease_replayed", replay.Code);
        Assert.Equal(first.LeaseIdentity, replay.LeaseIdentity);
    }

    [Fact]
    public async Task OverlappingImpactSet_CannotReceiveSecondLease()
    {
        var sharedIncident = Incident(app: "AgentPortal", source: "SHARED/Renderer/LegendRenderer.cs");
        var appIncident = Incident(app: "ClientApp", source: "ClientApp/Controllers/HomeController.cs");
        var shared = await _store.AttachIncidentAsync(sharedIncident, LegendEngineeringPolicies.Classify(sharedIncident), default);
        var app = await _store.AttachIncidentAsync(appIncident, LegendEngineeringPolicies.Classify(appIncident), default);
        Assert.True((await _store.TryAcquireLeaseAsync(shared.WorkItemId, "shared-owner", TimeSpan.FromMinutes(10), default)).Acquired);
        var blocked = await _store.TryAcquireLeaseAsync(app.WorkItemId, "app-owner", TimeSpan.FromMinutes(10), default);
        Assert.False(blocked.Acquired);
        Assert.Equal("overlapping_impact_set_leased", blocked.Code);
        Assert.Equal(shared.WorkItemId, blocked.ConflictingWorkItemId);
    }

    [Fact]
    public async Task DisjointImpactSets_CanReceiveIndependentLeases()
    {
        var agentIncident = Incident(app: "AgentPortal", source: "AgentPortal/Controllers/HomeController.cs");
        var clientIncident = Incident(app: "ClientApp", source: "ClientApp/Controllers/HomeController.cs");
        var agent = await _store.AttachIncidentAsync(agentIncident, LegendEngineeringPolicies.Classify(agentIncident), default);
        var client = await _store.AttachIncidentAsync(clientIncident, LegendEngineeringPolicies.Classify(clientIncident), default);
        Assert.True((await _store.TryAcquireLeaseAsync(agent.WorkItemId, "agent-owner", TimeSpan.FromMinutes(10), default)).Acquired);
        Assert.True((await _store.TryAcquireLeaseAsync(client.WorkItemId, "client-owner", TimeSpan.FromMinutes(10), default)).Acquired);
    }

    [Fact]
    public async Task EvidenceRevisionChange_InvalidatesExistingContext()
    {
        var incident = Incident(source: "AgentPortal/Controllers/HomeController.cs");
        var decision = LegendEngineeringPolicies.Classify(incident);
        var item = await _store.AttachIncidentAsync(incident, decision, default);
        var lease = await _store.TryAcquireLeaseAsync(item.WorkItemId, "owner", TimeSpan.FromMinutes(10), default);
        var context = Context(item, lease);
        await _store.SaveContextAsync(context, default);

        var newer = incident with { Id = Guid.NewGuid(), LastSeenUtc = incident.LastSeenUtc.AddMinutes(1), Occurrences = 2 };
        await _store.AttachIncidentAsync(newer, decision, default);

        var validation = await _store.ValidateContextAsync(context.EngineeringContextId, default);
        Assert.False(validation.Valid);
        Assert.Equal("evidence_revision_changed", validation.Code);
    }

    [Fact]
    public async Task ExpiredContext_IsDenied()
    {
        var incident = Incident(source: "AgentPortal/Controllers/HomeController.cs");
        var decision = LegendEngineeringPolicies.Classify(incident);
        var item = await _store.AttachIncidentAsync(incident, decision, default);
        var lease = await _store.TryAcquireLeaseAsync(item.WorkItemId, "owner", TimeSpan.FromMinutes(10), default);
        var context = Context(item, lease) with { ExpiresUtc = DateTime.UtcNow.AddSeconds(-1) };
        await _store.SaveContextAsync(context, default);
        var validation = await _store.ValidateContextAsync(context.EngineeringContextId, default);
        Assert.False(validation.Valid);
        Assert.Equal("engineering_context_expired", validation.Code);
    }

    [Fact]
    public void WorkItemSnapshot_ContainsNoRawProductionPayloadFields()
    {
        var properties = typeof(EngineeringWorkItemSnapshot).GetProperties().Select(property => property.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var forbidden in new[] { "Password", "Token", "Cookie", "AuthorizationHeader", "RequestBody", "ResponseBody", "CustomerName", "Email", "Phone", "Message", "ConnectionString", "PrivateKey" })
            Assert.DoesNotContain(forbidden, properties);
    }

    [Fact]
    public void EngineeringContext_CannotGrantProtectedSourceClasses()
    {
        var properties = new[] { "SAFE_SOURCE" };
        Assert.DoesNotContain("PRIVACY_PROTECTED", properties);
        Assert.DoesNotContain("INTEGRITY_PROTECTED", properties);
        Assert.DoesNotContain("EXISTENCE_ONLY", properties);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private EngineeringContextSnapshot Context(EngineeringWorkItemSnapshot item, EngineeringLeaseReceipt lease)
        => new(
            Guid.NewGuid(),
            LegendEngineeringContract.ContractRevision,
            LegendEngineeringContract.PolicyRevision,
            item.WorkItemId,
            item.AssignedRole,
            item.FailureClass,
            item.RiskClass,
            item.ComplexityScore,
            item.LiveSha,
            item.EvidenceRevision,
            ["legend_inspect_repository"],
            ["SAFE_SOURCE"],
            ["secret_values", "private_customer_data"],
            new EngineeringBudgetEnvelope(true, "NORMAL", 10_000, 100_000, 0, 0, 3, 2, true),
            3,
            ["stale_live_sha", "lease_conflict"],
            lease.LeaseIdentity!,
            DateTime.UtcNow,
            DateTime.UtcNow.AddMinutes(5));

    private static RuntimeDiagnosticIncident Incident(
        string app = "AgentPortal",
        string category = "Runtime",
        string error = "InvalidOperationException",
        string source = "AgentPortal/Controllers/HomeController.cs",
        long occurrences = 1)
    {
        var now = DateTime.UtcNow;
        return new RuntimeDiagnosticIncident
        {
            Id = Guid.NewGuid(),
            DeduplicationKey = Guid.NewGuid().ToString("N").PadRight(64, '0')[..64],
            AppIdentifier = app,
            Platform = "Server",
            Route = "/dashboard",
            ErrorName = error,
            Category = category,
            StatusCode = 500,
            GitCommitHash = new string('a', 40),
            ReleaseVerified = true,
            SourceFilePath = source,
            Disposition = "Observed",
            ReviewVersion = 1,
            FirstSeenUtc = now,
            LastSeenUtc = now,
            ExpiresUtc = now.AddDays(14),
            Occurrences = occurrences
        };
    }

    private void CreateControlPlaneTables()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE LegendEngineeringControlLocks (LockKey TEXT PRIMARY KEY, Revision INTEGER NOT NULL);
            INSERT INTO LegendEngineeringControlLocks (LockKey,Revision) VALUES ('lease-authority',0);
            CREATE TABLE LegendEngineeringWorkItems (
              WorkItemId TEXT PRIMARY KEY, WorkKey TEXT NOT NULL UNIQUE, CanonicalAuthorityKey TEXT NOT NULL,
              ImpactSetJson TEXT NOT NULL, LiveSha TEXT NOT NULL, EvidenceRevision TEXT NOT NULL,
              FailureClass TEXT NOT NULL, RiskClass TEXT NOT NULL, ComplexityScore INTEGER NOT NULL,
              PriorityScore INTEGER NOT NULL, State TEXT NOT NULL, AssignedRole TEXT NOT NULL,
              LeaseOwner TEXT NULL, LeaseIdentity TEXT NULL, LeaseExpiresUtc TEXT NULL, AttemptCount INTEGER NOT NULL,
              SnapshotJson TEXT NOT NULL, CreatedUtc TEXT NOT NULL, UpdatedUtc TEXT NOT NULL);
            CREATE TABLE LegendEngineeringContexts (
              EngineeringContextId TEXT PRIMARY KEY, WorkItemId TEXT NOT NULL, ContractRevision TEXT NOT NULL,
              PolicyRevision TEXT NOT NULL, Role TEXT NOT NULL, LiveSha TEXT NOT NULL, EvidenceRevision TEXT NOT NULL,
              LeaseIdentity TEXT NOT NULL, SnapshotJson TEXT NOT NULL, CreatedUtc TEXT NOT NULL, ExpiresUtc TEXT NOT NULL);
            CREATE TABLE LegendEngineeringUsage (
              UsageId TEXT PRIMARY KEY, WorkItemId TEXT NOT NULL, ModelTier TEXT NOT NULL, Role TEXT NOT NULL,
              Provider TEXT NOT NULL, SessionId TEXT NULL, InputTokens INTEGER NULL, OutputTokens INTEGER NULL,
              TotalTokens INTEGER NULL, CostMicrousd INTEGER NULL, UsageObserved INTEGER NOT NULL, CreatedUtc TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }
}
