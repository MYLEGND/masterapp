using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Services;
using AgentPortal.Services.Engineering;
using Domain.Engineering;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shared.Diagnostics;
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

    [Theory]
    [InlineData("legend/approved-changes", true)]
    [InlineData(" production ", false)]
    [InlineData("legend/approved-changes-old", false)]
    [InlineData(null, false)]
    public void AutonomousRepair_RequiresExactApprovedIntegrationBranch(string? branchName, bool allowed)
    {
        Assert.Equal(allowed, LegendEngineeringPolicies.IsApprovedAutonomousBaseBranch(branchName));
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
    public void UnknownRoutineIncident_UsesFastTriageWithoutMutationAuthority()
    {
        var incident = Incident(category: "Observation", error: "UnclassifiedSignal",
            source: "AgentPortal/Views/Home/Index.cshtml");
        incident.StatusCode = null;
        var decision = LegendEngineeringPolicies.Classify(incident);
        Assert.Equal(EngineeringFailureClass.Unknown, decision.FailureClass);
        Assert.Equal(EngineeringRole.TriageWorker, decision.AssignedRole);
        Assert.Equal(EngineeringModelTier.FastTriage, decision.ModelTier);
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
    public async Task BrowserStructuralReproducer_IsPreservedOnDurableWorkItem()
    {
        var incident = Incident(app: "AgentPortal", source: "js/app.js");
        incident.Platform = "Web";
        incident.Route = "/Clients/Index";
        incident.ErrorName = "TypeError";
        incident.StructuralReproducerJson = JsonSerializer.Serialize(new RuntimeDiagnosticStructuralReproducer
        {
            ComponentIds = new[] { "website.modal" },
            ActionKeys = new[] { "contact.submit" },
            CompositionIds = new[] { "cms.hero.primary" },
            ModalIds = new[] { "website-modal" }
        });

        var item = await _store.AttachIncidentAsync(incident, LegendEngineeringPolicies.Classify(incident), default);

        Assert.Equal("/Clients/Index", item.ReproducerRoute);
        Assert.Equal(new[] { "website.modal" }, item.ReproducerComponentIds);
        Assert.Equal(new[] { "contact.submit" }, item.ReproducerActionKeys);
        Assert.Equal(new[] { "cms.hero.primary" }, item.ReproducerCompositionIds);
        Assert.Equal(new[] { "website-modal" }, item.ReproducerModalIds);
        Assert.Equal(new[] { "TypeError" }, item.ReproducerForbiddenErrorNames);
    }

    [Fact]
    public void BrowserTaskPacket_FailsClosedWithoutPreservedReproducer_AndBindsLiveProofToApplication()
    {
        var source = File.ReadAllText(Path.Combine(
            SourceRoot(), "AgentPortal", "Services", "Engineering", "LegendEngineeringOrchestrator.cs"));

        Assert.Contains("browser_reproducer_evidence_missing", source, StringComparison.Ordinal);
        Assert.Contains("item.ReproducerRoute ?? SafeRoute(primary.Route)", source, StringComparison.Ordinal);
        Assert.Contains("item.ReproducerComponentIds ?? Array.Empty<string>()", source, StringComparison.Ordinal);
        Assert.Contains("item.ReproducerActionKeys ?? Array.Empty<string>()", source, StringComparison.Ordinal);
        Assert.Contains("item.ReproducerCompositionIds ?? Array.Empty<string>()", source, StringComparison.Ordinal);
        Assert.Contains("item.ReproducerModalIds ?? Array.Empty<string>()", source, StringComparison.Ordinal);
        Assert.Contains("browser_live_proof_application_mismatch", source, StringComparison.Ordinal);
        Assert.Contains("item.AffectedApplications.Contains(application, StringComparer.Ordinal)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BackendQuietWindow_ClosesOnlyServerRepairWithoutRecurrence()
    {
        var deployed = DateTime.UtcNow.AddMinutes(-20);
        var baseIncident = Incident(app: "AgentPortal", source: "AgentPortal/Controllers/HomeController.cs");
        baseIncident.Platform = "Server";
        var decision = LegendEngineeringPolicies.Classify(baseIncident);
        var item = new EngineeringWorkItemSnapshot(
            Guid.NewGuid(),
            new[] { baseIncident.Id },
            "work-key",
            decision.CanonicalAuthorityKey,
            decision.AffectedProjects,
            decision.AffectedApplications,
            decision.ImpactSet,
            new string('a', 40),
            "evidence",
            decision.FailureClass,
            decision.Severity,
            decision.RevenueImpact,
            decision.UserImpact,
            decision.Frequency,
            decision.Confidence,
            25,
            decision.RiskClass,
            decision.ComplexityScore,
            decision.PriorityScore,
            decision.PriorityClass,
            "LIVE_FUNCTIONAL_PROOF_REQUIRED",
            EngineeringRole.LiveVerifier,
            EngineeringModelTier.StandardReasoning,
            null,
            null,
            null,
            1,
            "active",
            123,
            new string('b', 40),
            "DEPLOYMENT_VERIFIED_FUNCTIONAL_PROOF_PENDING",
            decision.ReleaseCohort,
            deployed.AddHours(-1),
            deployed,
            MergedSha: new string('c', 40),
            DeploymentVerifiedUtc: deployed);

        Assert.True(LegendEngineeringReleaseCohortPlanner.RuntimeQuietWindowProvesBackendRepair(
            item,
            new[] { baseIncident },
            Array.Empty<RuntimeDiagnosticIncident>(),
            DateTime.UtcNow,
            TimeSpan.FromMinutes(10)));

        var recurrence = Incident(app: "AgentPortal", source: "AgentPortal/Controllers/HomeController.cs");
        recurrence.Platform = "Server";
        recurrence.LastSeenUtc = deployed.AddMinutes(2);
        Assert.False(LegendEngineeringReleaseCohortPlanner.RuntimeQuietWindowProvesBackendRepair(
            item,
            new[] { baseIncident },
            new[] { recurrence },
            DateTime.UtcNow,
            TimeSpan.FromMinutes(10)));

        var browserIncident = Incident(app: "AgentPortal", source: "js/app.js");
        browserIncident.Platform = "Web";
        Assert.False(LegendEngineeringReleaseCohortPlanner.RuntimeQuietWindowProvesBackendRepair(
            item,
            new[] { browserIncident },
            Array.Empty<RuntimeDiagnosticIncident>(),
            DateTime.UtcNow,
            TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void SanitizedSourceHints_ResolveOnlyWithinApplicationAndSharedSafeRoots()
    {
        var resolved = FounderSoftwareRemediationService.ResolveSafeSourceHints(
            new[] { "HomeController.cs", "js/legend-site-tools.js", "_content/Shared/js/legend-site-tools.js" },
            "AgentPortal",
            new[]
            {
                "AgentPortal/Controllers/HomeController.cs",
                "ClientApp/Controllers/HomeController.cs",
                "AgentPortal/wwwroot/js/legend-site-tools.js",
                "SHARED/wwwroot/js/legend-site-tools.js",
                "AgentPortal/Security/Secrets.cs"
            });

        Assert.Contains("AgentPortal/Controllers/HomeController.cs", resolved);
        Assert.DoesNotContain("ClientApp/Controllers/HomeController.cs", resolved);
        Assert.Contains("AgentPortal/wwwroot/js/legend-site-tools.js", resolved);
        Assert.Contains("SHARED/wwwroot/js/legend-site-tools.js", resolved);
        Assert.DoesNotContain("AgentPortal/Security/Secrets.cs", resolved);
    }

    [Fact]
    public async Task SameSanitizedFilenameAcrossApplications_DoesNotMergeWorkItems()
    {
        var agentIncident = Incident(app: "AgentPortal", source: "HomeController.cs");
        var clientIncident = Incident(app: "ClientApp", source: "HomeController.cs");
        var agent = await _store.AttachIncidentAsync(agentIncident, LegendEngineeringPolicies.Classify(agentIncident), default);
        var client = await _store.AttachIncidentAsync(clientIncident, LegendEngineeringPolicies.Classify(clientIncident), default);

        Assert.NotEqual(agent.WorkItemId, client.WorkItemId);
        Assert.NotEqual(agent.CanonicalAuthorityKey, client.CanonicalAuthorityKey);
        Assert.Equal("AgentPortal", Assert.Single(agent.AffectedProjects));
        Assert.Equal("ClientApp", Assert.Single(client.AffectedProjects));
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
    public async Task ModelAttemptLedger_IsReplaySafe_AndPromotesObservedUsage()
    {
        var incident = Incident(source: "AgentPortal/Controllers/HomeController.cs");
        var item = await _store.AttachIncidentAsync(incident, LegendEngineeringPolicies.Classify(incident), default);
        var attemptId = Guid.NewGuid();

        await _store.RecordUsageAsync(new EngineeringUsageObservation(
            attemptId, item.WorkItemId, EngineeringModelTier.CodeImplementation, EngineeringRole.CodexImplementer,
            "ChatGPTPlanCodexAppServer", null, null, null, null, null, false, DateTime.UtcNow,
            ProviderAttempted: true, LogicalAttemptCompleted: false, ProviderOutcome: "OUTCOME_UNKNOWN"), default);
        await _store.RecordUsageAsync(new EngineeringUsageObservation(
            attemptId, item.WorkItemId, EngineeringModelTier.CodeImplementation, EngineeringRole.CodexImplementer,
            "ChatGPTPlanCodexAppServer", "thread-1", null, null, 123, null, true, DateTime.UtcNow,
            ProviderAttempted: true, LogicalAttemptCompleted: true, ProviderOutcome: "COMPLETED"), default);

        Assert.Equal(1, await _store.CountModelAttemptsAsync(item.WorkItemId, EngineeringRole.CodexImplementer, default));
        var totals = await _store.ReadUsageTotalsAsync(DateTime.UtcNow, default);
        Assert.Equal(123, totals.DailyTokens);
        Assert.Equal(123, totals.MonthlyTokens);
        Assert.True(totals.UsageEvidenceComplete);
    }


    [Fact]
    public async Task ProviderFailure_DoesNotConsumeLogicalEngineeringAttempt()
    {
        var incident = Incident(source: "AgentPortal/Controllers/HomeController.cs");
        var item = await _store.AttachIncidentAsync(
            incident, LegendEngineeringPolicies.Classify(incident), default);

        await _store.RecordUsageAsync(new EngineeringUsageObservation(
            Guid.NewGuid(), item.WorkItemId, EngineeringModelTier.CodeImplementation,
            EngineeringRole.CodexImplementer, "ChatGPTPlanResponses", "resp-created",
            null, null, null, null, false, DateTime.UtcNow,
            ProviderAttempted: true,
            LogicalAttemptCompleted: false,
            ProviderOutcome: "OUTCOME_UNKNOWN",
            ProviderStatusCode: 503,
            ProviderErrorCode: "subscription_sharing_usage_unavailable",
            ProviderRequestId: "req-fixture"), default);

        Assert.Equal(0, await _store.CountModelAttemptsAsync(
            item.WorkItemId, EngineeringRole.CodexImplementer, default));
    }

    [Fact]
    public async Task ExpiredLeaseRecovery_RestoresExactPriorActionableState()
    {
        var incident = Incident(source: "AgentPortal/Controllers/HomeController.cs");
        var item = await _store.AttachIncidentAsync(
            incident, LegendEngineeringPolicies.Classify(incident), default);
        var priorState = item.State;
        var lease = await _store.TryAcquireLeaseAsync(
            item.WorkItemId, "owner-a", TimeSpan.FromMinutes(5), default);
        Assert.True(lease.Acquired);

        var leased = await _store.GetWorkItemAsync(item.WorkItemId, default);
        Assert.NotNull(leased);
        await _store.UpdateWorkItemAsync(
            leased! with { LeaseExpiresUtc = DateTime.UtcNow.AddMinutes(-1) },
            default);

        Assert.Equal(1, await _store.RecoverExpiredLeasesAsync(DateTime.UtcNow, default));
        var recovered = await _store.GetWorkItemAsync(item.WorkItemId, default);
        Assert.NotNull(recovered);
        Assert.Equal(priorState, recovered!.State);
        Assert.Null(recovered.LeaseIdentity);
        Assert.Null(recovered.LeaseOwner);
        Assert.Null(recovered.LeaseExpiresUtc);
    }

    [Fact]
    public async Task ProviderRetryTransition_ClearsLease_AndResumesOnlyWhenDue()
    {
        var incident = Incident(source: "AgentPortal/Controllers/HomeController.cs");
        var item = await _store.AttachIncidentAsync(
            incident, LegendEngineeringPolicies.Classify(incident), default);
        var priorState = item.State;
        var lease = await _store.TryAcquireLeaseAsync(
            item.WorkItemId, "owner-a", TimeSpan.FromMinutes(5), default);
        Assert.True(lease.Acquired);

        var retryUtc = DateTime.UtcNow.AddMinutes(5);
        var waiting = await _store.TransitionProviderFailureAsync(
            item.WorkItemId,
            lease.LeaseIdentity!,
            "chatgpt_plan_response_temporarily_unavailable",
            "req-fixture",
            retryUtc,
            waitForProviderControl: false,
            default);
        Assert.NotNull(waiting);
        Assert.Equal("WAITING_PROVIDER_RETRY", waiting!.State);
        Assert.Null(waiting.LeaseIdentity);
        Assert.Equal(priorState, waiting.ResumeState);
        Assert.Equal(0, await _store.ActivateProviderWaitingWorkAsync(
            retryUtc.AddSeconds(-1), releaseProviderControlBlocks: false, default));
        Assert.Equal(1, await _store.ActivateProviderWaitingWorkAsync(
            retryUtc.AddSeconds(1), releaseProviderControlBlocks: false, default));

        var resumed = await _store.GetWorkItemAsync(item.WorkItemId, default);
        Assert.NotNull(resumed);
        Assert.Equal(priorState, resumed!.State);
        Assert.Null(resumed.NextRetryUtc);
        Assert.Null(resumed.ResumeState);
    }

    [Fact]
    public async Task LeaseHeartbeat_RenewsExactIdentityWithoutChangingWorkflowRevision()
    {
        var incident = Incident(source: "AgentPortal/Controllers/HomeController.cs");
        var item = await _store.AttachIncidentAsync(
            incident, LegendEngineeringPolicies.Classify(incident), default);
        var lease = await _store.TryAcquireLeaseAsync(
            item.WorkItemId, "heartbeat-owner", TimeSpan.FromMinutes(5), default);
        Assert.True(lease.Acquired);

        var before = await _store.GetWorkItemAsync(item.WorkItemId, default);
        Assert.NotNull(before);
        var renewed = await _store.RenewLeaseAsync(
            item.WorkItemId,
            lease.LeaseIdentity!,
            TimeSpan.FromMinutes(5),
            DateTime.UtcNow.AddMinutes(20),
            default);
        Assert.True(renewed);

        var after = await _store.GetWorkItemAsync(item.WorkItemId, default);
        Assert.NotNull(after);
        Assert.Equal(before!.StateRevision, after!.StateRevision);
        Assert.Equal(lease.LeaseIdentity, after.LeaseIdentity);
        Assert.True(after.LeaseExpiresUtc > before.LeaseExpiresUtc);
    }

    [Fact]
    public async Task NewEvidenceDuringLease_RestoresActionableStateAndClearsStaleLease()
    {
        var firstIncident = Incident(source: "AgentPortal/Controllers/HomeController.cs");
        var decision = LegendEngineeringPolicies.Classify(firstIncident);
        var item = await _store.AttachIncidentAsync(firstIncident, decision, default);
        var priorState = item.State;
        var lease = await _store.TryAcquireLeaseAsync(
            item.WorkItemId, "evidence-owner", TimeSpan.FromMinutes(5), default);
        Assert.True(lease.Acquired);

        var recurrence = Incident(source: "AgentPortal/Controllers/HomeController.cs");
        recurrence.Occurrences = firstIncident.Occurrences + 1;
        recurrence.LastSeenUtc = firstIncident.LastSeenUtc.AddSeconds(1);
        var updated = await _store.AttachIncidentAsync(
            recurrence, LegendEngineeringPolicies.Classify(recurrence), default);

        Assert.Equal(priorState, updated.State);
        Assert.Null(updated.LeaseIdentity);
        Assert.Null(updated.LeaseOwner);
        Assert.Null(updated.LeaseExpiresUtc);
    }

    [Fact]
    public async Task ChatGptPlanCircuit_IsAccountWide_EpisodeAware_AndClearedOnlyByReadinessProof()
    {
        var authority = PlanCredentialAuthority(new PlanTokenHandler(HttpStatusCode.OK, "{}"));
        await authority.StoreAuthorizationAsync(
            "client-fixture",
            "access-token-1234567890",
            "refresh-token-1234567890",
            new[] { "openid", "offline_access", "resource.invoke", "chatgpt.tokens.use.direct" },
            DateTime.UtcNow.AddHours(1),
            default);

        await authority.RecordProviderFailureAsync(
            new ChatGptPlanProviderFailure(
                "USAGE_LIMIT",
                "subscription_sharing_usage_limit_exceeded",
                null,
                "req-limit",
                429,
                "error_object",
                null),
            default);

        var blocked = await authority.GetAsync(default);
        Assert.Equal("USAGE_LIMIT", blocked.ProviderBlockerClass);
        Assert.Equal("subscription_sharing_usage_limit_exceeded", blocked.ProviderBlockerCode);
        Assert.False(string.IsNullOrWhiteSpace(blocked.ProviderCircuitEpisodeId));

        var denied = await authority.TryAcquireProviderExecutionLeaseAsync(
            "worker-a", TimeSpan.FromMinutes(2), allowCircuitProbe: false, default);
        Assert.False(denied.Acquired);
        Assert.Equal(blocked.ProviderBlockerCode, denied.Code);

        var episode = blocked.ProviderCircuitEpisodeId;
        await authority.RecordReadinessSuccessAsync(
            new string('a', 64),
            new Dictionary<string, string>
            {
                [EngineeringRole.HeadGpt] = "model-a",
                [EngineeringRole.CodexImplementer] = "model-a",
                [EngineeringRole.IndependentReviewer] = "model-a"
            },
            "resp-ready",
            "req-ready",
            default);

        var recovered = await authority.GetAsync(default);
        Assert.Null(recovered.ProviderBlockerCode);
        Assert.Null(recovered.ProviderCircuitEpisodeId);
        Assert.Equal(episode, recovered.ProviderRecoveredEpisodeId);
        Assert.Equal("READY", recovered.ReadinessState);
    }

    [Fact]
    public async Task ChatGptPlanProviderExecutionLease_SerializesAcrossWorkers()
    {
        var authority = PlanCredentialAuthority(new PlanTokenHandler(HttpStatusCode.OK, "{}"));
        await authority.StoreAuthorizationAsync(
            "client-fixture",
            "access-token-1234567890",
            "refresh-token-1234567890",
            new[] { "openid", "offline_access", "resource.invoke", "chatgpt.tokens.use.direct" },
            DateTime.UtcNow.AddHours(1),
            default);

        var first = await authority.TryAcquireProviderExecutionLeaseAsync(
            "worker-a", TimeSpan.FromMinutes(2), allowCircuitProbe: false, default);
        Assert.True(first.Acquired);
        var second = await authority.TryAcquireProviderExecutionLeaseAsync(
            "worker-b", TimeSpan.FromMinutes(2), allowCircuitProbe: false, default);
        Assert.False(second.Acquired);
        Assert.Equal("chatgpt_plan_provider_execution_busy", second.Code);

        await authority.ReleaseProviderExecutionLeaseAsync(first.LeaseIdentity!, default);
        var afterRelease = await authority.TryAcquireProviderExecutionLeaseAsync(
            "worker-b", TimeSpan.FromMinutes(2), allowCircuitProbe: false, default);
        Assert.True(afterRelease.Acquired);
    }

    [Fact]
    public void EngineeringRuntime_HasNoHiddenBillingModeOrParallelExecutionAuthority()
    {
        var root = SourceRoot();
        var budget = File.ReadAllText(Path.Combine(
            root, "AgentPortal", "Services", "Engineering", "LegendEngineeringBudgetAuthority.cs"));
        var hosted = File.ReadAllText(Path.Combine(
            root, "AgentPortal", "Services", "Engineering", "LegendEngineeringHostedService.cs"));
        var adapter = File.ReadAllText(Path.Combine(
            root, "AgentPortal", "Services", "Engineering", "ChatGptPlanResponsesAdapter.cs"));

        Assert.DoesNotContain("LegendEngineering:Budget:Mode", budget, StringComparison.Ordinal);
        Assert.Contains("CHATGPT_PLAN_PROVIDER_ENFORCED", budget, StringComparison.Ordinal);
        Assert.Contains("RecoverExpiredLeasesAsync", hosted, StringComparison.Ordinal);
        Assert.Contains("TryAcquireProviderExecutionLeaseAsync", adapter, StringComparison.Ordinal);
        Assert.Contains("response.incomplete", adapter, StringComparison.Ordinal);
        Assert.Contains("ProviderRequestId", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("OPENAI_API_KEY", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("/v1/agents", adapter, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkItemStateRevision_RejectsStaleWriterWithoutUsingMutableTimestamp()
    {
        var incident = Incident(source: "AgentPortal/Controllers/HomeController.cs");
        var item = await _store.AttachIncidentAsync(incident, LegendEngineeringPolicies.Classify(incident), default);
        Assert.False(string.IsNullOrWhiteSpace(item.StateRevision));

        var persisted = await _store.UpdateWorkItemAsync(
            item with { State = "FIRST_TRANSITION", UpdatedUtc = DateTime.UtcNow.AddMinutes(1) },
            default);
        Assert.NotEqual(item.StateRevision, persisted.StateRevision);
        Assert.Equal("FIRST_TRANSITION", persisted.State);

        var stale = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.UpdateWorkItemAsync(
                item with { State = "STALE_TRANSITION", UpdatedUtc = DateTime.UtcNow.AddHours(1) },
                default));
        Assert.Equal("work_item_state_changed", stale.Message);

        var fresh = await _store.UpdateWorkItemAsync(
            persisted with { State = "SECOND_TRANSITION", UpdatedUtc = DateTime.UtcNow.AddHours(2) },
            default);
        Assert.Equal("SECOND_TRANSITION", fresh.State);
    }

    [Fact]
    public async Task UnchangedIncidentScan_DoesNotChurnWorkItemStateRevision()
    {
        var incident = Incident(source: "AgentPortal/Controllers/HomeController.cs");
        var decision = LegendEngineeringPolicies.Classify(incident);
        var first = await _store.AttachIncidentAsync(incident, decision, default);
        var second = await _store.AttachIncidentAsync(incident, decision, default);

        Assert.Equal(first.WorkItemId, second.WorkItemId);
        Assert.Equal(first.StateRevision, second.StateRevision);
        Assert.Equal(first.UpdatedUtc, second.UpdatedUtc);
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

        var newer = Incident(source: incident.SourceFilePath!, occurrences: 2);
        newer.AppIdentifier = incident.AppIdentifier;
        newer.Category = incident.Category;
        newer.ErrorName = incident.ErrorName;
        newer.GitCommitHash = incident.GitCommitHash;
        newer.ReleaseVerified = incident.ReleaseVerified;
        newer.LastSeenUtc = incident.LastSeenUtc.AddMinutes(1);
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


    [Fact]
    public async Task ChatGptPlanCredential_UnapprovedPrivateClient_FailsClosed()
    {
        var authority = PlanCredentialAuthority(
            new PlanTokenHandler(HttpStatusCode.OK, "{}"),
            privateClientApproved: false,
            clientId: "private-client");
        var state = await authority.GetAsync(default);
        Assert.False(state.Ready);
        Assert.Equal("chatgpt_plan_private_client_eligibility_unverified", state.Code);
        Assert.Null(state.AccessToken);
    }

    [Fact]
    public async Task ChatGptPlanCredential_MissingPlanUsageScope_FailsClosed()
    {
        var authority = PlanCredentialAuthority(
            new PlanTokenHandler(HttpStatusCode.OK, "{}"),
            privateClientApproved: true,
            clientId: "private-client");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            authority.StoreAuthorizationAsync(
                "private-client",
                "opaque-access-token-1234567890",
                "opaque-refresh-token-1234567890",
                new[] { "openid", "offline_access", "resource.invoke" },
                DateTime.UtcNow.AddHours(1),
                default));

        Assert.Equal("chatgpt_plan_usage_scope_missing", error.Message);
    }

    [Fact]
    public async Task ChatGptPlanCredential_ApprovedPlanScope_IsEligibleWithoutApiKey()
    {
        var authority = PlanCredentialAuthority(
            new PlanTokenHandler(HttpStatusCode.OK, "{}"),
            privateClientApproved: true,
            clientId: "private-client");

        await authority.StoreAuthorizationAsync(
            "private-client",
            "opaque-access-token-1234567890",
            "opaque-refresh-token-1234567890",
            new[] { "openid", "offline_access", "resource.invoke", "chatgpt.tokens.use.direct" },
            DateTime.UtcNow.AddHours(1),
            default);

        var state = await authority.GetAsync(default);
        Assert.True(state.Ready, state.Code);
        Assert.Equal("chatgpt_plan_ready", state.Code);
        Assert.Contains("chatgpt.tokens.use.direct", state.GrantedScopes);
    }

    [Fact]
    public void ChatGptPlanResponsesAdapter_HasNoAgentsApiOrApiKeyFallback()
    {
        var source = File.ReadAllText(Path.Combine(SourceRoot(), "AgentPortal", "Services", "Engineering", "ChatGptPlanResponsesAdapter.cs"));
        Assert.Contains("https://api.openai.com/v1/responses", source, StringComparison.Ordinal);
        Assert.Contains("https://api.openai.com/v1/models", source, StringComparison.Ordinal);
        Assert.Contains("new AuthenticationHeaderValue(\"Bearer\"", source, StringComparison.Ordinal);
        Assert.Contains("store = false", source, StringComparison.Ordinal);
        Assert.Contains("stream = true", source, StringComparison.Ordinal);
        Assert.Contains("text = new", source, StringComparison.Ordinal);
        Assert.Contains("type = \"json_schema\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OPENAI_API_KEY", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenAiKeyResolver", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/v1/agents/sessions", source, StringComparison.Ordinal);
        Assert.DoesNotContain("app-server", source, StringComparison.Ordinal);
    }


    [Fact]
    public void ReleasePlanner_P1IsImmediate_AndUnrelatedCandidatesAreNotCombined()
    {
        var p1 = ReleaseWorkItem("P1", EngineeringRiskClass.TierA, "AgentPortal", "source:AgentPortal/A.cs", 'a');
        var unrelated = ReleaseWorkItem("P3", EngineeringRiskClass.TierA, "ClientApp", "source:ClientApp/B.cs", 'b');
        var decision = LegendEngineeringReleaseCohortPlanner.Plan([p1, unrelated], DateTime.UtcNow);
        Assert.True(decision.Ready);
        Assert.Equal("p1_immediate", decision.Code);
        Assert.Single(decision.WorkItemIds);
        Assert.Contains(p1.WorkItemId, decision.WorkItemIds);
    }

    [Fact]
    public void ReleasePlanner_TierBStopsForFounderApproval()
    {
        var item = ReleaseWorkItem("P1", EngineeringRiskClass.TierB, "AgentPortal", "source:AgentPortal/A.cs", 'c');
        var decision = LegendEngineeringReleaseCohortPlanner.Plan([item], DateTime.UtcNow);
        Assert.True(decision.Ready);
        Assert.True(decision.FounderApprovalRequired);
    }

    [Fact]
    public void ReleasePlanner_P3WaitsUntilDailyCohortWhenCoverageIsBelowThreshold()
    {
        var ready = ReleaseWorkItem("P3", EngineeringRiskClass.TierA, "AgentPortal", "source:AgentPortal/A.cs", 'd',
            priorityScore: 30, updatedUtc: DateTime.UtcNow.AddHours(-2));
        var pending = ReleaseWorkItem("P2", EngineeringRiskClass.TierA, "AgentPortal", "source:AgentPortal/A.cs", 'e',
            priorityScore: 70, state: "QUEUED", validation: "NOT_STARTED", updatedUtc: DateTime.UtcNow.AddHours(-2));
        var decision = LegendEngineeringReleaseCohortPlanner.Plan([ready, pending], DateTime.UtcNow);
        Assert.False(decision.Ready);
        Assert.Equal("cohort_waiting", decision.Code);
        Assert.True(decision.WeightedReadyCoverage < 70);
    }

    [Fact]
    public async Task ChatGptPlanCredential_RefreshesOnceAndRotatesDurableOfflineGrant()
    {
        var handler = new PlanTokenHandler(
            HttpStatusCode.OK,
            """{"access_token":"new-access-token-1234567890","refresh_token":"new-refresh-token-1234567890","expires_in":3600,"scope":"offline_access resource.invoke chatgpt.tokens.use.direct"}""");
        var authority = PlanCredentialAuthority(handler);

        await authority.StoreAuthorizationAsync(
            "client-fixture",
            "old-access-token-1234567890",
            "old-refresh-token-1234567890",
            new[] { "offline_access", "resource.invoke", "chatgpt.tokens.use.direct" },
            DateTime.UtcNow.AddMinutes(2),
            default);

        var first = await authority.GetAsync(default);
        Assert.True(first.Ready, first.Code);
        Assert.Equal("new-access-token-1234567890", first.AccessToken);
        Assert.Equal(1, handler.Calls);
        Assert.Contains("grant_type=refresh_token", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("scope=", handler.LastBody, StringComparison.Ordinal);

        var second = await authority.GetAsync(default);
        Assert.True(second.Ready, second.Code);
        Assert.Equal("new-access-token-1234567890", second.AccessToken);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ChatGptPlanCredential_TerminalRefreshFailureRequiresFounderReauthorization()
    {
        var handler = new PlanTokenHandler(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
        var authority = PlanCredentialAuthority(handler);

        await authority.StoreAuthorizationAsync(
            "client-fixture",
            "old-access-token-1234567890",
            "old-refresh-token-1234567890",
            new[] { "offline_access", "resource.invoke", "chatgpt.tokens.use.direct" },
            DateTime.UtcNow.AddMinutes(2),
            default);

        var first = await authority.GetAsync(default);
        Assert.False(first.Ready);
        Assert.Equal("chatgpt_plan_reauthorization_required", first.Code);
        Assert.Equal(1, handler.Calls);

        var second = await authority.GetAsync(default);
        Assert.False(second.Ready);
        Assert.Equal("chatgpt_plan_reauthorization_required", second.Code);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ChatGptPlanCredential_TransientRefreshFailurePreservesGrantForRetry()
    {
        var handler = new PlanTokenHandler(
            HttpStatusCode.ServiceUnavailable,
            """{"error":"temporarily_unavailable"}""",
            (HttpStatusCode.OK,
             """{"access_token":"retry-access-token-1234567890","refresh_token":"retry-refresh-token-1234567890","expires_in":3600,"scope":"offline_access resource.invoke chatgpt.tokens.use.direct"}"""));
        var authority = PlanCredentialAuthority(handler);

        await authority.StoreAuthorizationAsync(
            "client-fixture",
            "old-access-token-1234567890",
            "old-refresh-token-1234567890",
            new[] { "offline_access", "resource.invoke", "chatgpt.tokens.use.direct" },
            DateTime.UtcNow.AddMinutes(2),
            default);

        var first = await authority.GetAsync(default);
        Assert.False(first.Ready);
        Assert.Equal("chatgpt_plan_refresh_temporarily_unavailable", first.Code);

        var retry = await authority.GetAsync(default);
        Assert.True(retry.Ready, retry.Code);
        Assert.Equal("retry-access-token-1234567890", retry.AccessToken);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task ChatGptPlanCredential_RejectsGrantWithoutOfflineAccess()
    {
        var authority = PlanCredentialAuthority(new PlanTokenHandler(HttpStatusCode.OK, "{}"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            authority.StoreAuthorizationAsync(
                "client-fixture",
                "access-token-1234567890",
                "refresh-token-1234567890",
                new[] { "resource.invoke", "chatgpt.tokens.use.direct" },
                DateTime.UtcNow.AddHours(1),
                default));

        Assert.Equal("chatgpt_plan_usage_scope_missing", error.Message);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }



    private static EngineeringWorkItemSnapshot ReleaseWorkItem(
        string priority,
        string risk,
        string application,
        string authority,
        char key,
        int priorityScore = 90,
        string state = "VALIDATED",
        string validation = "GREEN",
        DateTime? updatedUtc = null)
    {
        var now = updatedUtc ?? DateTime.UtcNow.AddHours(-8);
        return new EngineeringWorkItemSnapshot(
            Guid.NewGuid(), [Guid.NewGuid()], new string(key, 64), authority,
            [application], [application], ["authority:" + authority], new string('a', 40),
            new string('e', 64), EngineeringFailureClass.CodeDefect, 90, 90, 80, 70, 90, 25,
            risk, 40, priorityScore, priority, state,
            EngineeringRole.CodexImplementer, EngineeringModelTier.CodeImplementation,
            null, null, null, 1, "active", 123, new string('b', 40), validation,
            LegendEngineeringPolicies.ReleaseCohort(priority), now, now);
    }

    private static string SourceRoot()
    {
        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(workspace) &&
            File.Exists(Path.Combine(workspace, "MASTERAPP.sln")))
            return workspace;

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MASTERAPP.sln")))
                directory = directory.Parent;
            if (directory is not null) return directory.FullName;
        }

        throw new InvalidOperationException("Repository root not found.");
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

    private LegendChatGptPlanCredentialAuthority PlanCredentialAuthority(
        HttpMessageHandler handler,
        bool privateClientApproved = true,
        string clientId = "client-fixture")
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendEngineering:ChatGptPlan:PrivateClientApproved"] = privateClientApproved ? "true" : "false",
            ["LegendEngineering:ChatGptPlan:ClientId"] = clientId
        }).Build();
        return new LegendChatGptPlanCredentialAuthority(
            _db,
            configuration,
            new EphemeralDataProtectionProvider(),
            new PlanClientFactory(handler));
    }

    private sealed class PlanClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class PlanTokenHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();

        public PlanTokenHandler(HttpStatusCode status, string body, params (HttpStatusCode Status, string Body)[] additional)
        {
            _responses.Enqueue((status, body));
            foreach (var response in additional) _responses.Enqueue(response);
        }

        public int Calls { get; private set; }
        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(new Uri("https://auth.openai.com/api/accounts/oauth/token"), request.RequestUri);
            LastBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var response = _responses.Count > 0
                ? _responses.Dequeue()
                : (Status: HttpStatusCode.ServiceUnavailable, Body: """{"error":"temporarily_unavailable"}""");
            return new HttpResponseMessage(response.Status)
            {
                Content = new StringContent(response.Body, Encoding.UTF8),
                RequestMessage = request
            };
        }
    }

    private void CreateControlPlaneTables()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE LegendEngineeringControlLocks (LockKey TEXT PRIMARY KEY, Revision INTEGER NOT NULL);
            INSERT INTO LegendEngineeringControlLocks (LockKey,Revision) VALUES ('lease-authority',0);
            INSERT INTO LegendEngineeringControlLocks (LockKey,Revision) VALUES ('chatgpt-plan-credential',0);
            CREATE TABLE LegendEngineeringChatGptPlanCredentials (
              CredentialKey TEXT PRIMARY KEY, ClientId TEXT NOT NULL, AccessTokenCiphertext TEXT NOT NULL,
              RefreshTokenCiphertext TEXT NOT NULL, GrantedScopesJson TEXT NOT NULL,
              AccessTokenExpiresUtc TEXT NOT NULL, State TEXT NOT NULL, Revision TEXT NOT NULL,
              RefreshLeaseIdentity TEXT NULL, RefreshLeaseUntilUtc TEXT NULL, ConnectedUtc TEXT NOT NULL,
              LastRefreshedUtc TEXT NULL, UpdatedUtc TEXT NOT NULL,
              ProviderBlockerClass TEXT NULL, ProviderBlockerCode TEXT NULL, ProviderBlockedUtc TEXT NULL,
              ProviderRetryNotBeforeUtc TEXT NULL, ProviderRequestId TEXT NULL, ProviderHttpStatus INTEGER NULL,
              ProviderErrorShape TEXT NULL, ProviderErrorParam TEXT NULL, ProviderCircuitEpisodeId TEXT NULL, ProviderRecoveredEpisodeId TEXT NULL,
              ProviderRecoveredUtc TEXT NULL, ProviderFailureStreak INTEGER NOT NULL DEFAULT 0,
              ReadinessState TEXT NOT NULL DEFAULT 'UNVERIFIED', ReadinessSignature TEXT NULL,
              ReadinessModelsJson TEXT NULL, ReadinessCheckedUtc TEXT NULL, ReadinessResponseId TEXT NULL,
              ReadinessRequestId TEXT NULL, ReadinessCode TEXT NULL, ProviderExecutionLeaseIdentity TEXT NULL,
              ProviderExecutionLeaseOwner TEXT NULL, ProviderExecutionLeaseUntilUtc TEXT NULL);
            CREATE TABLE LegendEngineeringChatGptPlanClientRegistration (
              RegistrationKey TEXT PRIMARY KEY, ClientId TEXT NOT NULL, AuthenticationMethod TEXT NOT NULL,
              ClientSecretCiphertext TEXT NULL, EligibilityConfirmed INTEGER NOT NULL, Revision TEXT NOT NULL,
              UpdatedUtc TEXT NOT NULL);
            CREATE TABLE LegendEngineeringChatGptPlanOAuthTransactions (
              StateHash TEXT PRIMARY KEY, ClientId TEXT NOT NULL, CodeVerifierCiphertext TEXT NOT NULL,
              NonceCiphertext TEXT NOT NULL, RedirectUri TEXT NOT NULL, CreatedUtc TEXT NOT NULL,
              ExpiresUtc TEXT NOT NULL);
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
              TotalTokens INTEGER NULL, CostMicrousd INTEGER NULL, UsageObserved INTEGER NOT NULL, CreatedUtc TEXT NOT NULL,
              ProviderAttempted INTEGER NOT NULL DEFAULT 0, LogicalAttemptCompleted INTEGER NOT NULL DEFAULT 0,
              ProviderOutcome TEXT NULL, ProviderStatusCode INTEGER NULL, ProviderErrorShape TEXT NULL,
              ProviderErrorCode TEXT NULL, ProviderErrorParam TEXT NULL, ProviderRequestId TEXT NULL);
            """;
        command.ExecuteNonQuery();
    }
}
