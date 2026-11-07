using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Models;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Reception;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class ReceptionistOperatorTests
{
    [Fact]
    public async Task ReviewedRetiredPriorMonthHoldCanResumeAndReleaseTheNextCallSlotWithoutChangingHistory()
    {
        await using var f = await Fixture.CreateAsync(budgetRisk: true);
        var (oldCall, binding) = await AccountingRecoveryScenarioAsync(f);
        await OpenRecoveryPeriodsAsync(f, binding);
        await using var db = f.Database();
        var historicalCall = JsonSerializer.Serialize(await db.Set<ReceptionistCall>().AsNoTracking().SingleAsync(x => x.Id == oldCall));
        var historicalCost = JsonSerializer.Serialize(await db.Set<ReceptionistCostReservation>().AsNoTracking().SingleAsync(x => x.CallId == oldCall));
        var historicalReceipts = await db.Set<ReceptionistUsageReceipt>().CountAsync(x => x.CallId == oldCall);
        var observation = await f.Operator.PrepareAccountingAsync(f.Owner.Key, binding.ConfigurationId);
        var owner = Assert.Single(observation.Accounts.Where(x => x.Account.Id == f.OwnerBudgetId));
        Assert.False(owner.AdmissionEnabled); Assert.Empty(owner.AdmissionBlockers);
        Assert.Equal(360, owner.VoiceHeld); Assert.Equal(1, owner.RetiredVoiceCarryCount);
        Assert.Equal(2m, owner.CashHeld); Assert.Equal(3m, owner.ConsumptionHeld);
        Assert.Equal(25m, owner.CurrentPeriod!.CashLimit); Assert.Equal(25m, owner.CurrentPeriod.ConsumptionLimit);
        // The old unscoped internal entry point cannot claim a reviewed binding's exception.
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Admission.SetBudgetAdmissionAsync(f.OwnerBudgetId, owner.Account.Revision, true));
        var (saved, plan) = await ReviewRecoveryDraftAsync(f, RecoveryDraft(f, observation, owner, "set_budget_admission") with { Enabled = true });
        Assert.Contains(plan.Facts, x => x.Label == "Unfinished voice held" && x.Value == "360 seconds");
        Assert.True((await f.Operator.ApplyAsync(f.Request(plan))).ReadBackVerified);
        Assert.Equal("already_satisfied", (await f.Operator.ApplyAsync(f.Request(plan))).Outcome);
        Assert.Contains(await f.Operator.SavedAccountingOperationsAsync(), x => x.Key == "saved-" + saved.Id.ToString("N") && x.ReviewState == "accepted");

        var creation = Guid.NewGuid();
        var first = await f.ReserveBudgetAsync(binding, creation); Assert.True(first.MayCreate);
        var parallel = await f.ReserveBudgetAsync(binding, Guid.NewGuid());
        Assert.False(parallel.MayCreate); Assert.Equal("owner_concurrency_limit", parallel.Reason);
        Assert.True(await f.Admission.BindBrowserOutcomeAsync(first.CallId!.Value, creation,
            new(true, "live_recovery_first", "v=0\r\n", 201, "synthetic-recovery-create"), true, default));
        f.Clock.Now = f.Clock.Now.AddSeconds(15);
        await f.Admission.RecordProviderStateAsync(first.CallId.Value, "closed", "synthetic-recovery-closure", f.Clock.Now);
        await f.Admission.ReconcileVoiceUsageAsync(first.CallId.Value, "synthetic-recovery-final-voice", 15, true);
        f.Clock.Now = f.Clock.Now.AddSeconds(61);
        Assert.True((await f.ReserveBudgetAsync(binding, Guid.NewGuid())).MayCreate);
        Assert.Equal(historicalCall, JsonSerializer.Serialize(await db.Set<ReceptionistCall>().AsNoTracking().SingleAsync(x => x.Id == oldCall)));
        Assert.Equal(historicalCost, JsonSerializer.Serialize(await db.Set<ReceptionistCostReservation>().AsNoTracking().SingleAsync(x => x.CallId == oldCall)));
        Assert.Equal(historicalReceipts, await db.Set<ReceptionistUsageReceipt>().CountAsync(x => x.CallId == oldCall));
    }

    [Fact]
    public async Task ReviewedAccountingEnablementUsesItsLockedContextForEveryGuardRead()
    {
        await using var f = await Fixture.CreateAsync(budgetRisk: true);
        var (_, binding) = await AccountingRecoveryScenarioAsync(f);
        await OpenRecoveryPeriodsAsync(f, binding);
        var observation = await f.Operator.PrepareAccountingAsync(f.Owner.Key, binding.ConfigurationId);
        var owner = Assert.Single(observation.Accounts.Where(x => x.Account.Id == f.OwnerBudgetId));
        var (_, plan) = await ReviewRecoveryDraftAsync(f, RecoveryDraft(f, observation, owner, "set_budget_admission") with { Enabled = true });
        var probe = new RecoveryLockedReadProbe();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>(f.Options).AddInterceptors(probe).Options;
        using var services = new ServiceCollection().AddSingleton(options).BuildServiceProvider();
        var operation = new ReceptionistOperatorService(f.Store, new ReceptionistAdmissionService(options, f.Clock),
            f.Host, f.Protector, f.Http, f.Clock, services);
        var receipt = await operation.ApplyAsync(f.Request(plan));
        Assert.True(receipt.ReadBackVerified); Assert.Equal("applied", receipt.Outcome);
        Assert.True(probe.ReadsUnderLock > 0);
        Assert.True((await f.Admission.GetBudgetAccountAsync(f.OwnerBudgetId))!.AdmissionEnabled);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task EveryCurrentPeriodCarriesFullHistoricalVoiceInsteadOfSubtractingUnallocatedObservation(int month)
    {
        await using var f = await Fixture.CreateAsync(budgetRisk: true);
        var (_, binding) = await AccountingRecoveryScenarioAsync(f, month: month);
        await OpenRecoveryPeriodsAsync(f, binding);
        await using var db = f.Database();
        var period = await db.Set<ReceptionistBudgetPeriod>().SingleAsync(x => x.BudgetAccountId == f.OwnerBudgetId && x.MonthKey == 202600 + month);
        // A reduced hold (360 - 30 unallocated seconds) would wrongly admit this call.
        period.VoiceSecondsCommitted = binding.Configuration.MonthlyVoiceSecondsLimit - 720 + 1;
        period.Revision = Guid.NewGuid(); await db.SaveChangesAsync();
        await EnableRecoveryAccountingAsync(f, binding);
        var denied = await f.ReserveBudgetAsync(binding, Guid.NewGuid());
        Assert.False(denied.MayCreate); Assert.Equal("owner_voice_minutes_limit", denied.Reason);
        var observation = await f.Operator.PrepareAccountingAsync(f.Owner.Key, binding.ConfigurationId);
        Assert.Equal(360, Assert.Single(observation.Accounts.Where(x => x.Account.Id == f.OwnerBudgetId)).VoiceHeld);
    }

    [Theory]
    [InlineData("no-retirement")]
    [InlineData("historical-public-call")]
    [InlineData("historical-public-binding")]
    [InlineData("wrong-policy-owner")]
    [InlineData("wrong-policy-configuration")]
    [InlineData("legacy-policy")]
    [InlineData("expired-policy")]
    [InlineData("voice-overrun")]
    [InlineData("live-execution-lease")]
    [InlineData("pending-business-work")]
    [InlineData("uncertain-business-work")]
    [InlineData("mismatched-retirement-marker")]
    [InlineData("unexpired-deadline")]
    public async Task HistoricalCarryNeverWaivesUnreviewedOrUnsafeCallConditions(string defect)
    {
        await using var f = await Fixture.CreateAsync(budgetRisk: true);
        var (oldCall, binding) = await AccountingRecoveryScenarioAsync(f);
        await OpenRecoveryPeriodsAsync(f, binding);
        await using var db = f.Database();
        var call = await db.Set<ReceptionistCall>().SingleAsync(x => x.Id == oldCall);
        var marker = await db.Set<ReceptionistActionIntent>().SingleAsync(x => x.CallId == oldCall && x.LogicalActionKey == "__admin_retire_stale_test_call");
        var version = await db.Set<ReceptionistConfigurationVersion>().SingleAsync(x => x.Id == binding.ConfigurationId);
        var changedBinding = binding.Binding;
        switch (defect)
        {
            case "no-retirement": db.Remove(marker); break;
            case "historical-public-call": call.ControlledTest = false; break;
            case "historical-public-binding":
                var oldVersion = await db.Set<ReceptionistConfigurationVersion>().SingleAsync(x => x.Id == call.ConfigurationVersionId);
                var oldBinding = JsonSerializer.Deserialize<ReceptionistProviderBinding>(oldVersion.AcceptedBindingJson!, ReceptionistConfigurationCodec.Json)!;
                oldVersion.AcceptedBindingJson = JsonSerializer.Serialize(oldBinding with { ControlledTestOnly = false, PublicActivationReceipt = "synthetic-other-public-scope" }, ReceptionistConfigurationCodec.Json);
                break;
            case "wrong-policy-owner": changedBinding = changedBinding with { BudgetPolicy = changedBinding.BudgetPolicy! with { OwnerKey = "another-owner" } }; break;
            case "wrong-policy-configuration": changedBinding = changedBinding with { BudgetPolicy = changedBinding.BudgetPolicy! with { AcceptedConfigurationId = Guid.NewGuid() } }; break;
            case "legacy-policy": changedBinding = changedBinding with { BudgetPolicy = null, MaximumSupportedCallSeconds = 360 }; break;
            case "expired-policy": f.Clock.Now = binding.Binding.BudgetPolicy!.ExpiresUtc; break;
            case "voice-overrun": call.ObservedVoiceSeconds = call.ReservedVoiceSeconds + 1; break;
            case "live-execution-lease": call.SidebandLeaseExpiresUtc = f.Clock.Now.AddMinutes(1); call.SidebandObservationOnly = false; break;
            case "pending-business-work":
            case "uncertain-business-work": db.Add(RecoveryBusinessAction(oldCall, f.Clock.Now, defect.StartsWith("pending", StringComparison.Ordinal) ? "pending" : "uncertain")); break;
            case "mismatched-retirement-marker": marker.VerificationReceipt = "different-review"; break;
            case "unexpired-deadline": call.MustStopUtc = f.Clock.Now.AddMinutes(1); break;
        }
        version.AcceptedBindingJson = JsonSerializer.Serialize(changedBinding, ReceptionistConfigurationCodec.Json);
        await db.SaveChangesAsync();
        var package = (await f.ForAsync("set_budget_admission", budgetId: f.OwnerBudgetId)) with { Enabled = true };
        var identity = f.Publish(package);
        Assert.False((await f.Operator.PlanAsync(identity.Key, identity.Hash)).CanApply);
        Assert.False((await f.Admission.GetBudgetAccountAsync(f.OwnerBudgetId))!.AdmissionEnabled);
        // Also exercise admission independently of the accounting permission gate.
        var account = await db.Set<ReceptionistBudgetAccount>().SingleAsync(x => x.Id == f.OwnerBudgetId);
        account.AdmissionEnabled = true; await db.SaveChangesAsync();
        var current = (await f.Admission.GetBrowserBindingAsync(f.Owner, default))!;
        var denied = await f.ReserveBudgetAsync(current, Guid.NewGuid());
        Assert.False(denied.MayCreate);
        Assert.Contains(denied.Reason, new[] { "voice_allocation_unreconciled", "voice_reservation_exceeded", "budget_policy_unavailable", "acceptance_window_changed" });
        Assert.Null((await db.Set<ReceptionistCall>().AsNoTracking().SingleAsync(x => x.Id == oldCall)).ProviderClosedUtc);
    }

    [Fact]
    public async Task BudgetAdmissionReviewMustNameTheExactCurrentAcceptedConfiguration()
    {
        await using var f = await Fixture.CreateAsync(budgetRisk: true);
        var (oldCall, binding) = await AccountingRecoveryScenarioAsync(f);
        await OpenRecoveryPeriodsAsync(f, binding);
        await using var db = f.Database();
        var oldVersionId = await db.Set<ReceptionistCall>().Where(x => x.Id == oldCall).Select(x => x.ConfigurationVersionId).SingleAsync();
        var oldVersion = await db.Set<ReceptionistConfigurationVersion>().SingleAsync(x => x.Id == oldVersionId);
        var package = (await f.ForAsync("set_budget_admission", budgetId: f.OwnerBudgetId)) with
            { Enabled = true, ConfigurationId = oldVersion.Id, ConfigurationSha256 = oldVersion.ConfigurationSha256 };
        var identity = f.Publish(package);
        Assert.False((await f.Operator.PlanAsync(identity.Key, identity.Hash)).CanApply);
        Assert.False((await f.Admission.GetBudgetAccountAsync(f.OwnerBudgetId))!.AdmissionEnabled);
    }

    [Theory]
    [InlineData("call-revision")]
    [InlineData("observation-lease")]
    [InlineData("retirement-evidence")]
    [InlineData("pending-work")]
    public async Task AccountingEnablementPreviewRejectsChangedRecoveryFactsBeforeAnyWrite(string changed)
    {
        await using var f = await Fixture.CreateAsync(budgetRisk: true);
        var (oldCall, binding) = await AccountingRecoveryScenarioAsync(f);
        await OpenRecoveryPeriodsAsync(f, binding);
        var observation = await f.Operator.PrepareAccountingAsync(f.Owner.Key, binding.ConfigurationId);
        var owner = Assert.Single(observation.Accounts.Where(x => x.Account.Id == f.OwnerBudgetId));
        var (_, plan) = await ReviewRecoveryDraftAsync(f, RecoveryDraft(f, observation, owner, "set_budget_admission") with { Enabled = true });
        await using var db = f.Database();
        var call = await db.Set<ReceptionistCall>().SingleAsync(x => x.Id == oldCall);
        if (changed == "call-revision") call.Revision = Guid.NewGuid();
        if (changed == "observation-lease")
        { call.SidebandObservationOnly = true; call.SidebandLeaseToken = Guid.NewGuid(); call.SidebandLeaseExpiresUtc = f.Clock.Now.AddMinutes(1); }
        if (changed == "retirement-evidence")
        {
            var marker = await db.Set<ReceptionistActionIntent>().SingleAsync(x => x.CallId == oldCall && x.LogicalActionKey == "__admin_retire_stale_test_call");
            marker.ConfirmationReceipt = marker.VerificationReceipt = "another-valid-review";
        }
        if (changed == "pending-work") db.Add(RecoveryBusinessAction(oldCall, f.Clock.Now, "pending"));
        await db.SaveChangesAsync();
        if (changed == "pending-work")
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Operator.ApplyAsync(f.Request(plan)));
        else
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => f.Operator.ApplyAsync(f.Request(plan)));
        Assert.False((await f.Admission.GetBudgetAccountAsync(f.OwnerBudgetId))!.AdmissionEnabled);
    }

    [Theory]
    [InlineData("cash")]
    [InlineData("consumption")]
    [InlineData("non-call")]
    [InlineData("ceiling")]
    public async Task RetiredVoiceCarryRetainsKnownCostAndNonCallAccountingGuards(string obstruction)
    {
        await using var f = await Fixture.CreateAsync(budgetRisk: true);
        var (oldCall, binding) = await AccountingRecoveryScenarioAsync(f);
        await OpenRecoveryPeriodsAsync(f, binding);
        await using var db = f.Database();
        var cost = await db.Set<ReceptionistCostReservation>().SingleAsync(x => x.CallId == oldCall);
        if (obstruction == "cash") cost.CashObserved = cost.CashReserved + 1;
        if (obstruction == "consumption") cost.ConsumptionObserved = cost.ConsumptionReserved + 1;
        if (obstruction == "non-call") db.Add(new ReceptionistAccountCharge
        { OwnerBudgetAccountId = f.OwnerBudgetId, FundingBudgetAccountId = f.FundingId,
            ProviderChargeKey = "synthetic-unresolved-non-call", ComponentKey = "synthetic-service" });
        if (obstruction == "ceiling")
        {
            var period = await db.Set<ReceptionistBudgetPeriod>().SingleAsync(x => x.BudgetAccountId == f.OwnerBudgetId && x.MonthKey == 202603);
            period.CashCommitted = 24m;
        }
        await db.SaveChangesAsync();
        var id = f.Publish((await f.ForAsync("set_budget_admission", budgetId: f.OwnerBudgetId)) with { Enabled = true });
        Assert.False((await f.Operator.PlanAsync(id.Key, id.Hash)).CanApply);
        Assert.False((await f.Admission.GetBudgetAccountAsync(f.OwnerBudgetId))!.AdmissionEnabled);
        Assert.False((await db.Set<ReceptionistCostReservation>().AsNoTracking().SingleAsync(x => x.CallId == oldCall)).CashFinal);
    }

    [Fact]
    public async Task CurrentPublicPolicyRequiresSeparatePublicAcceptanceAndCannotReuseRestrictedTestScope()
    {
        await using var f = await Fixture.CreateAsync(budgetRisk: true);
        var (_, restricted) = await AccountingRecoveryScenarioAsync(f);
        await OpenRecoveryPeriodsAsync(f, restricted);
        await EnableRecoveryAccountingAsync(f, restricted);
        var denied = await f.Admission.ReserveBrowserAsync(restricted, Guid.NewGuid(), ReceptionistConfigurationCodec.Hash("synthetic-public-offer"), false, default);
        Assert.False(denied.MayCreate); Assert.Equal("transport_unavailable", denied.Reason);
        var proposal = await CurrentRecoveryPolicyProposalAsync(f, controlledTest: false);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Operator.PrepareForOwnerAsync(f.Owner, proposal, (_, _) => Task.FromResult(true)));
        proposal.Evidence.Add(new() { Kind = "public_activation", Reference = "synthetic-separate-public-acceptance",
            Sha256 = ReceptionistConfigurationCodec.Hash("synthetic-separate-public-acceptance"), ObservedUtc = f.Clock.Now });
        var binding = await AcceptRecoveryPolicyAsync(f, proposal);
        Assert.False(binding.Binding.ControlledTestOnly);
        Assert.Equal("synthetic-separate-public-acceptance", binding.Binding.PublicActivationReceipt);
        var current = await f.Operator.PrepareAccountingAsync(f.Owner.Key, binding.ConfigurationId);
        Assert.Equal(1, Assert.Single(current.Accounts.Where(x => x.Account.Id == f.OwnerBudgetId)).RetiredVoiceCarryCount);
        Assert.True((await f.Admission.ReserveBrowserAsync(binding, Guid.NewGuid(), ReceptionistConfigurationCodec.Hash("synthetic-public-offer"), false, default)).MayCreate);
    }

    [Theory]
    [InlineData("KnownCashCommitted", true)]
    [InlineData("KnownCashCommitted", false)]
    [InlineData("KnownConsumptionCommitted", true)]
    [InlineData("KnownConsumptionCommitted", false)]
    [InlineData("KnownVoiceSeconds", true)]
    [InlineData("KnownVoiceSeconds", false)]
    public async Task TypedAccountingOpeningCannotTurnBlankOrOmittedAmountsIntoZero(string missing, bool blank)
    {
        await using var f = await Fixture.CreateAsync(budgetRisk: true);
        var (_, binding) = await AccountingRecoveryScenarioAsync(f);
        var observation = await f.Operator.PrepareAccountingAsync(f.Owner.Key, binding.ConfigurationId);
        var owner = Assert.Single(observation.Accounts.Where(x => x.Account.Id == f.OwnerBudgetId));
        var collection = new ServiceCollection(); collection.AddLogging(); collection.AddMvcCore().AddDataAnnotations();
        using var services = collection.BuildServiceProvider();
        var controller = new PreparationBindingController
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services } },
            MetadataProvider = services.GetRequiredService<IModelMetadataProvider>(),
            ModelBinderFactory = services.GetRequiredService<IModelBinderFactory>(),
            ObjectValidator = services.GetRequiredService<IObjectModelValidator>()
        };
        var fields = RecoveryFormFields(f, observation, owner);
        if (blank) fields[missing] = ""; else fields.Remove(missing);
        var input = new FounderAccountingDraftInput();
        await controller.TryUpdateModelAsync(input, "", new FormValueProvider(BindingSource.Form, new FormCollection(fields), CultureInfo.InvariantCulture));
        Assert.Null(typeof(FounderAccountingDraftInput).GetProperty(missing)!.GetValue(input));
        var draft = RecoveryDraft(f, observation, owner, "open_budget_period") with
        { KnownCashCommitted = input.KnownCashCommitted, KnownConsumptionCommitted = input.KnownConsumptionCommitted,
            KnownVoiceSeconds = input.KnownVoiceSeconds, ReconciledThroughUtc = f.Clock.Now };
        await Assert.ThrowsAsync<ArgumentException>(() => f.Operator.PrepareSavedAccountingAsync(draft));
        await using var db = f.Database();
        Assert.False(await db.Set<ReceptionistBudgetPeriod>().AnyAsync(x => x.BudgetAccountId == f.OwnerBudgetId && x.MonthKey == 202603));
    }

    [Fact]
    public async Task CurrentAccountingObservationIsScopedReadOnlyAndCannotInventAccountsOrCeilings()
    {
        await using var f = await Fixture.CreateAsync(budgetRisk: true);
        var (_, binding) = await AccountingRecoveryScenarioAsync(f);
        var writes = f.Commands.Writes;
        var observed = await f.Operator.PrepareAccountingAsync(f.Owner.Key, binding.ConfigurationId);
        Assert.Equal(writes, f.Commands.Writes); Assert.Equal("webrtc", observed.CurrentTransport);
        Assert.True(observed.RetiredVoiceCarryPolicyCurrent); Assert.Equal(2, observed.Accounts.Count);
        Assert.All(observed.Accounts, x => Assert.Null(x.CurrentPeriod));
        var json = JsonSerializer.Serialize(observed);
        foreach (var excluded in new[] { Fixture.ApiSecret, Fixture.WebhookSecret, "private-test-business-policy", "ProtectedApiCredential", "AcceptedBindingJson" })
            Assert.DoesNotContain(excluded, json);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Operator.PrepareAccountingAsync("different-owner", binding.ConfigurationId));
        var owner = Assert.Single(observed.Accounts.Where(x => x.Account.Id == f.OwnerBudgetId));
        var draft = RecoveryDraft(f, observed, owner, "open_budget_period") with
        { KnownCashCommitted = 0m, KnownConsumptionCommitted = 0m, KnownVoiceSeconds = 0, ReconciledThroughUtc = f.Clock.Now };
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Operator.PrepareSavedAccountingAsync(draft with { BudgetAccountId = Guid.NewGuid() }));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => f.Operator.PrepareSavedAccountingAsync(draft with { ExpectedAccountRevision = Guid.NewGuid() }));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Operator.PrepareSavedAccountingAsync(draft with { ReconciledThroughUtc = f.Clock.Now.AddMonths(-1) }));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Operator.PrepareSavedAccountingAsync(draft with { Operation = "approve_budget_ceilings" }));
        var prior = f.Http.HttpContext!.User;
        f.Http.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new("oid", Guid.NewGuid().ToString())], "test"));
        try { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Operator.PrepareSavedAccountingAsync(draft)); }
        finally { f.Http.HttpContext.User = prior; }
    }

    [Fact]
    public async Task FounderAccountingControllerPreparesReviewsAppliesAndReadBackVerifiesTheExactOpening()
    {
        var priorFounder = Environment.GetEnvironmentVariable("FOUNDER_OID");
        Environment.SetEnvironmentVariable("FOUNDER_OID", Fixture.FounderOid);
        try
        {
            await using var f = await Fixture.CreateAsync(budgetRisk: true);
            var (_, binding) = await AccountingRecoveryScenarioAsync(f);
            var observed = await f.Operator.PrepareAccountingAsync(f.Owner.Key, binding.ConfigurationId);
            var owner = Assert.Single(observed.Accounts.Where(x => x.Account.Id == f.OwnerBudgetId));
            var provider = new RetirementProviderProbe();
            using var dependencies = new ServiceCollection().AddSingleton(f.Options).AddSingleton<IOpenAiReceptionistClient>(provider).BuildServiceProvider();
            var operation = new ReceptionistOperatorService(f.Store, new ReceptionistAdmissionService(f.Options, f.Clock, dependencies),
                f.Host, f.Protector, f.Http, f.Clock, dependencies);
            var services = new ServiceCollection(); services.AddLogging(); services.AddDataProtection(); services.AddControllersWithViews(); services.AddSingleton(operation);
            using var requestServices = services.BuildServiceProvider();
            f.Http.HttpContext!.RequestServices = requestServices;
            var controller = new AgentPortal.Controllers.FounderEngineeringController(null!)
            { ControllerContext = new ControllerContext { HttpContext = f.Http.HttpContext } };
            var input = new FounderAccountingDraftInput
            {
                ExpectedOwnerKey = f.Owner.Key, ConfigurationId = binding.ConfigurationId, ExpectedConnectionRevision = observed.ConnectionRevision,
                BudgetAccountId = owner.Account.Id, ExpectedAccountRevision = owner.Account.Revision, Operation = "open_budget_period",
                KnownCashCommitted = 1m, KnownConsumptionCommitted = 2m, KnownVoiceSeconds = 3,
                ReconciledThroughUtc = f.Clock.Now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                EvidenceReference = "synthetic-explicit-current-opening", EvidenceSha256 = ReceptionistConfigurationCodec.Hash("synthetic-explicit-current-opening"),
                EvidenceObservedUtc = f.Clock.Now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            };
            var prepared = Assert.IsType<FounderReceptionistOperatorViewModel>(Assert.IsType<ViewResult>(await controller.PrepareSavedAccounting(input, default)).Model);
            var pending = Assert.IsType<ReceptionistOperationReview>(prepared.PendingAccountingReview);
            var review = new FounderAccountingReviewInput { Id = Guid.ParseExact(pending.Key[6..], "N"), Hash = pending.Sha256,
                Revision = pending.PendingReviewRevision!.Value, Accepted = true };
            var accepted = Assert.IsType<FounderReceptionistOperatorViewModel>(Assert.IsType<ViewResult>(await controller.ReviewSavedAccounting(review, default)).Model);
            Assert.Contains(accepted.SavedAccountingOperations!, x => x.Key == pending.Key && x.ReviewState == "accepted");
            var plan = Assert.IsType<FounderReceptionistOperatorViewModel>(Assert.IsType<ViewResult>(await controller.PlanReceptionist(
                new() { PackageKey = pending.Key, PackageSha256 = pending.Sha256 }, default)).Model).Plan!;
            Assert.True(plan.CanApply);
            var request = new FounderReceptionistApplyInput { PackageKey = plan.PackageKey, PackageSha256 = plan.PackageSha256, ExpectedStateToken = plan.ExpectedStateToken! };
            var receipt = Assert.IsType<FounderReceptionistOperatorViewModel>(Assert.IsType<ViewResult>(await controller.ApplyReceptionist(request, default)).Model).Receipt!;
            Assert.True(receipt.ReadBackVerified);
            Assert.Equal("already_applied", Assert.IsType<FounderReceptionistOperatorViewModel>(Assert.IsType<ViewResult>(await controller.ApplyReceptionist(request, default)).Model).Receipt!.Outcome);
            var after = await f.Operator.PrepareAccountingAsync(f.Owner.Key, binding.ConfigurationId);
            var result = Assert.Single(after.Accounts.Where(x => x.Account.Id == f.OwnerBudgetId));
            Assert.False(result.AdmissionEnabled); Assert.Equal(25m, result.CurrentPeriod!.CashLimit); Assert.Equal(25m, result.CurrentPeriod.ConsumptionLimit);
            Assert.Equal(1m, result.CurrentPeriod.CashCommitted); Assert.Equal(2m, result.CurrentPeriod.ConsumptionCommitted); Assert.Equal(3, result.CurrentPeriod.VoiceSecondsCommitted);
            Assert.Equal(360, result.VoiceHeld); Assert.Equal(0, provider.Calls);
        }
        finally { Environment.SetEnvironmentVariable("FOUNDER_OID", priorFounder); }
    }

    [Fact]
    public async Task AccountingControlsRetainAntiforgeryTypedAmountsAndSeparateReviewStages()
    {
        var controller = typeof(AgentPortal.Controllers.FounderEngineeringController);
        foreach (var action in new[] { "PrepareSavedAccounting", "PreviewSavedAccounting", "ReviewSavedAccounting" })
            Assert.NotNull(controller.GetMethod(action)!.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        var view = await File.ReadAllTextAsync(FindSourceFile("AgentPortal/Views/FounderEngineering/Receptionist.cshtml"));
        foreach (var action in new[] { "PrepareSavedAccounting", "PreviewSavedAccounting", "ReviewSavedAccounting" })
            Assert.Contains("asp-action=\"" + action + "\"", view);
        Assert.Contains("A locally retired call is no longer an active application call", view);
        foreach (var field in new[] { "KnownCashCommitted", "KnownConsumptionCommitted", "KnownVoiceSeconds" })
        {
            var control = view.Split('\n').Single(x => x.Contains("name=\"" + field + "\"", StringComparison.Ordinal));
            Assert.DoesNotContain("value=", control);
        }
        Assert.Null(typeof(FounderAccountingDraftInput).GetProperty("CashLimit"));
        Assert.Null(typeof(FounderAccountingDraftInput).GetProperty("Month"));
    }

    private static async Task<(Guid OldCall, ReceptionistIngressBinding Binding)> AccountingRecoveryScenarioAsync(Fixture f, int month = 3)
    {
        var oldPackage = await f.BindingPackageAsync();
        await f.ApplyAsync(oldPackage with { Binding = oldPackage.Binding! with { MaximumSupportedCallSeconds = 360 } });
        await f.EnableOwnerAsync();
        var ingress = (await f.Admission.GetIngressBindingAsync("openai", Fixture.Ingress))!;
        var oldCall = (await f.Admission.ReserveAsync(f.IngressFor(ingress, "synthetic-historical-call"))).CallId!.Value;
        await f.Admission.RecordProviderStateAsync(oldCall, "accepted", "synthetic-historical-accepted");
        await using var db = f.Database();
        var call = await db.Set<ReceptionistCall>().AsNoTracking().SingleAsync(x => x.Id == oldCall);
        f.Clock.Now = DateTime.SpecifyKind(call.MustStopUtc, DateTimeKind.Utc).AddSeconds(1);
        await f.ApplyAsync((await f.ForAsync("retire_stale_test_call", callId: oldCall)) with { ExpectedProviderSessionId = "synthetic-historical-call" });
        f.Clock.Now = new DateTime(2026, month, 3, 12, 0, 0, DateTimeKind.Utc);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Admission.ReconcileVoiceUsageAsync(oldCall, "synthetic-unallocated-historical-voice", 30, false));
        var binding = await AcceptRecoveryPolicyAsync(f, await CurrentRecoveryPolicyProposalAsync(f));
        return (oldCall, binding);
    }

    private static async Task<ReceptionistOperationPreparationInput> CurrentRecoveryPolicyProposalAsync(Fixture f, bool controlledTest = true)
    {
        var proposal = await f.BudgetProposalAsync();
        proposal.ControlledTestOnly = controlledTest;
        proposal.ProviderBudgetPeriodStartUtc = new DateTime(f.Clock.Now.Year, f.Clock.Now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        proposal.ProviderBudgetPeriodEndUtc = proposal.ProviderBudgetPeriodStartUtc.Value.AddMonths(1);
        return proposal;
    }

    private static async Task<ReceptionistIngressBinding> AcceptRecoveryPolicyAsync(Fixture f, ReceptionistOperationPreparationInput input)
    {
        var id = await f.Operator.PrepareForOwnerAsync(f.Owner, input, (_, _) => Task.FromResult(true));
        await using var db = f.Database();
        var row = await db.Set<ReceptionistOperationPackage>().AsNoTracking().SingleAsync(x => x.Id == id);
        var pending = await f.Operator.PreviewPendingForOwnerAsync(f.Owner, id, row.PayloadSha256, (_, _) => Task.FromResult(true));
        await f.Operator.ReviewForOwnerAsync(f.Owner, id, pending.PendingReviewRevision!.Value, row.PayloadSha256, true, (_, _) => Task.FromResult(true));
        var plan = await f.Operator.PlanForOwnerAsync(f.Owner, "saved-" + id.ToString("N"), row.PayloadSha256, (_, _) => Task.FromResult(true));
        Assert.True(plan.CanApply);
        Assert.True((await f.Operator.ApplyForOwnerAsync(f.Owner, f.Request(plan), (_, _) => Task.FromResult(true))).ReadBackVerified);
        await f.ApplyAsync((await f.ForAsync("set_owner_admission")) with { Enabled = true });
        var binding = (await f.Admission.GetBrowserBindingAsync(f.Owner, default))!;
        if (input.ControlledTestOnly) await ReceptionistTestWindows.SeedAsync(db, binding.MarketingConnectionId, binding.ConfigurationId, binding.Binding, f.Clock.Now);
        return binding;
    }

    private static ReceptionistAccountingDraft RecoveryDraft(Fixture f, ReceptionistAccountingPreparation observed,
        ReceptionistAccountingAccountPreparation account, string operation)
    {
        var reference = "synthetic-current-accounting:" + Guid.NewGuid().ToString("N");
        return new(f.Owner.Key, observed.ConfigurationId, observed.ConnectionRevision, account.Account.Id, account.Account.Revision,
            operation, reference, ReceptionistConfigurationCodec.Hash(reference), f.Clock.Now);
    }

    private static async Task<(ReceptionistPreparedAccounting Saved, ReceptionistOperatorPlan Plan)> ReviewRecoveryDraftAsync(Fixture f, ReceptionistAccountingDraft draft)
    {
        var saved = await f.Operator.PrepareSavedAccountingAsync(draft);
        var pending = await f.Operator.PreviewSavedAccountingAsync(saved.Id, saved.Sha256);
        Assert.False(pending.CanApply);
        await f.Operator.ReviewSavedAccountingAsync(saved.Id, pending.PendingReviewRevision!.Value, saved.Sha256, true);
        var plan = await f.Operator.PlanAsync("saved-" + saved.Id.ToString("N"), saved.Sha256);
        Assert.True(plan.CanApply);
        return (saved, plan);
    }

    private static async Task OpenRecoveryPeriodsAsync(Fixture f, ReceptionistIngressBinding binding)
    {
        var observed = await f.Operator.PrepareAccountingAsync(f.Owner.Key, binding.ConfigurationId);
        foreach (var account in observed.Accounts)
        {
            var (_, plan) = await ReviewRecoveryDraftAsync(f, RecoveryDraft(f, observed, account, "open_budget_period") with
            { KnownCashCommitted = 0m, KnownConsumptionCommitted = 0m, KnownVoiceSeconds = 0, ReconciledThroughUtc = f.Clock.Now });
            Assert.True((await f.Operator.ApplyAsync(f.Request(plan))).ReadBackVerified);
        }
    }

    private static async Task EnableRecoveryAccountingAsync(Fixture f, ReceptionistIngressBinding binding)
    {
        var observed = await f.Operator.PrepareAccountingAsync(f.Owner.Key, binding.ConfigurationId);
        var owner = Assert.Single(observed.Accounts.Where(x => x.Account.Id == f.OwnerBudgetId));
        var (_, plan) = await ReviewRecoveryDraftAsync(f, RecoveryDraft(f, observed, owner, "set_budget_admission") with { Enabled = true });
        Assert.True((await f.Operator.ApplyAsync(f.Request(plan))).ReadBackVerified);
    }

    private static ReceptionistActionIntent RecoveryBusinessAction(Guid callId, DateTime now, string state) => new()
    {
        CallId = callId, LogicalActionKey = "synthetic-pending-business", CapabilityKey = ReceptionistCapabilities.CanonicalIntake,
        RouteKey = "leads.confirmed_inquiry.v1", ArgumentsSha256 = ReceptionistConfigurationCodec.Hash("synthetic-pending-business"),
        ConfirmationReceipt = "synthetic-confirmation", State = state, CreatedUtc = now, UpdatedUtc = now
    };

    private sealed class RecoveryLockedReadProbe : DbCommandInterceptor
    {
        private DbTransaction? lockedTransaction;
        internal int ReadsUnderLock { get; private set; }
        private void Observe(DbCommand command)
        {
            var sql = command.CommandText.TrimStart();
            if (sql.StartsWith("UPDATE", StringComparison.Ordinal) && command.Transaction is { } transaction)
                lockedTransaction = transaction;
            if (sql.StartsWith("SELECT", StringComparison.Ordinal) && lockedTransaction?.Connection is { } lockedConnection)
            {
                // An independent connection here can wait on the accounting lock held
                // by this operation in SQL Server. SQLite alone would not expose it.
                Assert.Same(lockedConnection, command.Connection);
                ReadsUnderLock++;
            }
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Observe(command); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Observe(command); return ValueTask.FromResult(result); }
    }

    private static Dictionary<string, StringValues> RecoveryFormFields(Fixture f, ReceptionistAccountingPreparation observed,
        ReceptionistAccountingAccountPreparation account) => new()
    {
        ["ExpectedOwnerKey"] = f.Owner.Key, ["ConfigurationId"] = observed.ConfigurationId.ToString(),
        ["ExpectedConnectionRevision"] = observed.ConnectionRevision.ToString(), ["BudgetAccountId"] = account.Account.Id.ToString(),
        ["ExpectedAccountRevision"] = account.Account.Revision.ToString(), ["Operation"] = "open_budget_period",
        ["KnownCashCommitted"] = "0", ["KnownConsumptionCommitted"] = "0", ["KnownVoiceSeconds"] = "0",
        ["ReconciledThroughUtc"] = f.Clock.Now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        ["EvidenceReference"] = "synthetic-form-evidence", ["EvidenceSha256"] = ReceptionistConfigurationCodec.Hash("synthetic-form-evidence"),
        ["EvidenceObservedUtc"] = f.Clock.Now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
    };
}
