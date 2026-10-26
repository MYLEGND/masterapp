using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Analytics;
using Infrastructure.Data;
using Infrastructure.Data.Configurations;
using Infrastructure.Reception;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.Extensions.Primitives;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class ReceptionistOperatorTests
{
    [Fact]
    public async Task PreparationReadsExactStoredStateWithoutSecretsWritesOrInventedFunding()
    {
        await using var f = await Fixture.CreateAsync();
        var package = await f.BindingPackageAsync();
        var writes = f.Commands.Writes;
        var result = await f.Operator.PrepareAsync(f.Owner.Key, package.ConfigurationId);
        Assert.Equal("observed_not_reviewed", result.Status);
        Assert.Equal(package.ConfigurationSha256, result.ConfigurationSha256);
        Assert.Equal(package.ExpectedConnectionRevision, result.ConnectionRevision);
        Assert.Equal(package.MarketingConnectionId, result.MarketingConnectionId);
        Assert.False(result.CurrentBindingAccepted); Assert.False(result.AdmissionEnabled);
        Assert.Null(result.HistoricalAcceptedConfiguration);
        Assert.Equal(f.OwnerBudgetId, Assert.Single(result.Accounts).Id);
        Assert.Equal(writes, f.Commands.Writes);
        var json = JsonSerializer.Serialize(result);
        foreach (var value in new[] { Fixture.ApiSecret, Fixture.WebhookSecret, "private-test-business-policy",
            "ProtectedApiCredential", "AcceptedBindingJson", "ConfigurationJson", "ExpectedStateToken" })
            Assert.DoesNotContain(value, json);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Operator.PrepareAsync("foreign-owner", package.ConfigurationId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Operator.PrepareAsync(f.Owner.Key, Guid.NewGuid()));
        Assert.Equal(writes, f.Commands.Writes);
    }

    [Fact]
    public async Task PreparationDoesNotInheritEarlierAcceptanceAndRejectsStaleIdentityOrCorruptHash()
    {
        await using var f = await Fixture.CreateAsync();
        var package = await f.BindingPackageAsync(); var identity = f.Publish(package);
        await f.Operator.ApplyAsync(f.Request(await f.Operator.PlanAsync(identity.Key, identity.Hash)));
        await using var db = f.Database();
        var connection = await db.MarketingConnections.AsNoTracking().SingleAsync();
        await f.Store.SaveReceptionistConfigurationAsync(f.Owner, "openai", connection.Revision,
            Fixture.Configuration(), "synthetic-owner");
        var current = await db.Set<ReceptionistConfigurationVersion>().OrderByDescending(x => x.Version).FirstAsync();
        var writes = f.Commands.Writes;
        var result = await f.Operator.PrepareAsync(f.Owner.Key, current.Id);
        Assert.Equal(current.Id, result.ConfigurationId); Assert.False(result.CurrentBindingAccepted);
        Assert.False(result.AdmissionEnabled);
        Assert.Equal(connection.ReceptionistActiveConfigurationId, result.HistoricalAcceptedConfiguration!.ConfigurationId);
        Assert.Equal(package.ConfigurationSha256, result.HistoricalAcceptedConfiguration.ConfigurationSha256);
        var historical = result.HistoricalAcceptedConfiguration.BindingEvidence;
        Assert.Equal("historical_not_current_acceptance", historical.Status);
        Assert.True(historical.OwnerCurrencyAndProjectCompatible);
        Assert.Equal(f.FundingId, Assert.Single(historical.Costs).FundingBudgetAccountId);
        Assert.Equal(package.Binding!.Costs[0].CashFixedMaximum, historical.Costs[0].CashFixedMaximum);
        Assert.Equal(package.Binding.Capabilities[0].RouteKey, Assert.Single(historical.Capabilities).RouteKey);
        Assert.Contains(historical.ReceiptReferences, x => x.Label == "provider_setup" && x.Value == package.Binding.ProviderVerificationReceipt);
        Assert.Equal(identity.Hash, historical.OperatorAcceptancePackageSha256);
        Assert.Equal(package.Binding.CredentialReference, historical.CredentialReferenceName);
        var observedFunding = Assert.Single(historical.CurrentlyObservedReferencedFundingAccounts);
        Assert.Equal(f.FundingId, observedFunding.Id);
        Assert.Equal((await db.Set<ReceptionistBudgetAccount>().AsNoTracking().SingleAsync(x => x.Id == f.FundingId)).Revision,
            observedFunding.Revision);
        var response = JsonSerializer.Serialize(result);
        foreach (var excluded in new[] { Fixture.ApiSecret, Fixture.WebhookSecret, "ProtectedApiCredential",
            "ProtectedWebhookSecret", "AcceptedBindingJson", "ConfigurationJson", "private-test-business-policy", "IngressKey" })
            Assert.DoesNotContain(excluded, response);
        Assert.Equal(f.OwnerBudgetId, Assert.Single(result.Accounts).Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Operator.PrepareAsync(f.Owner.Key, package.ConfigurationId));
        Assert.Equal(writes, f.Commands.Writes);
        var storedConnection = await db.MarketingConnections.SingleAsync();
        storedConnection.ReceptionistProviderProjectKey = "different-project";
        await db.SaveChangesAsync();
        Assert.False((await f.Operator.PrepareAsync(f.Owner.Key, current.Id)).HistoricalAcceptedConfiguration!
            .BindingEvidence.OwnerCurrencyAndProjectCompatible);
        var funding = await db.Set<ReceptionistBudgetAccount>().SingleAsync(x => x.Id == f.FundingId);
        funding.Kind = "owner"; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Operator.PrepareAsync(f.Owner.Key, current.Id));
        funding.Kind = "funding"; await db.SaveChangesAsync();
        current.ConfigurationSha256 = new string('0', 64); await db.SaveChangesAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => f.Operator.PrepareAsync(f.Owner.Key, current.Id));
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("project")]
    public async Task PreparationRejectsConflictingConnectionAuthority(string mismatch)
    {
        await using var f = await Fixture.CreateAsync();
        var package = await f.BindingPackageAsync(); var identity = f.Publish(package);
        await f.Operator.ApplyAsync(f.Request(await f.Operator.PlanAsync(identity.Key, identity.Hash)));
        await using var db = f.Database();
        var connection = await db.MarketingConnections.SingleAsync();
        if (mismatch == "provider") connection.Provider = "other";
        else connection.ReceptionistProviderProjectKey = "different-project";
        await db.SaveChangesAsync();
        var writes = f.Commands.Writes;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Operator.PrepareAsync(f.Owner.Key,
            connection.ReceptionistActiveConfigurationId!.Value));
        Assert.Equal(writes, f.Commands.Writes);
    }

    [Theory]
    [InlineData("email")]
    [InlineData("name_identifier")]
    [InlineData("different_oid")]
    [InlineData("missing_configuration")]
    public async Task EveryServiceEntryRequiresTheConfiguredCanonicalFounderOid(string caseName)
    {
        await using var f = await Fixture.CreateAsync();
        var package = await f.BindingPackageAsync(); var identity = f.Publish(package);
        f.Http.HttpContext!.User = caseName switch
        {
            "email" => new(new ClaimsIdentity([new(ClaimTypes.Email, "synthetic-founder@example.invalid")], "test")),
            "name_identifier" => new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, Fixture.FounderOid)], "test")),
            "different_oid" => new(new ClaimsIdentity([new("oid", "00000000-0000-0000-0000-000000000022")], "test")),
            _ => f.Http.HttpContext.User
        };
        if (caseName == "missing_configuration") f.Host["FOUNDER_OID"] = null;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Operator.CatalogAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Operator.PrepareAsync(f.Owner.Key, package.ConfigurationId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Operator.PlanAsync(identity.Key, identity.Hash));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Operator.ApplyAsync(new(identity.Key, identity.Hash, "untrusted")));
        await using var db = f.Database();
        Assert.Single(await db.Set<ReceptionistConfigurationVersion>().ToListAsync());
        Assert.Empty(await db.Set<ReceptionistUsageReceipt>().ToListAsync());
    }

    [Fact]
    public async Task PreviewUsesRealEvidenceAndHasNoWritesOrSecretPolicyReceiptLeakage()
    {
        await using var f = await Fixture.CreateAsync();
        var package = await f.BindingPackageAsync(); var identity = f.Publish(package);
        var writes = f.Commands.Writes;
        var scoped = await f.Admission.ReadOperatorStateAsync(package, identity.Hash, CancellationToken.None);
        scoped.RequirePackageRevisions(package);
        var catalog = await f.Operator.CatalogAsync();
        var plan = await f.Operator.PlanAsync(identity.Key, identity.Hash);
        Assert.True(plan.CanApply); Assert.Empty(plan.Blockers); Assert.NotNull(plan.ExpectedStateToken);
        Assert.Equal(writes, f.Commands.Writes); Assert.Single(catalog);
        var response = JsonSerializer.Serialize(new { catalog, plan });
        Assert.DoesNotContain(Fixture.ApiSecret, response); Assert.DoesNotContain(Fixture.WebhookSecret, response);
        Assert.DoesNotContain("private-test-business-policy", response); Assert.DoesNotContain(package.Binding!.ProviderVerificationReceipt, response);
        Assert.DoesNotContain("ProtectedApiCredential", response); Assert.DoesNotContain("AcceptedBindingJson", response);
        await using var db = f.Database();
        Assert.Null((await db.Set<ReceptionistConfigurationVersion>().SingleAsync()).AcceptedBindingJson);
        Assert.False((await db.MarketingConnections.SingleAsync()).ReceptionistAdmissionEnabled);
    }

    [Fact]
    public async Task BindingApplyPinsCredentialsInactiveAndExactReplayWorksAfterBootstrapRemoval()
    {
        await using var f = await Fixture.CreateAsync();
        var package = await f.BindingPackageAsync(); var identity = f.Publish(package);
        var plan = await f.Operator.PlanAsync(identity.Key, identity.Hash);
        var receipt = await f.Operator.ApplyAsync(f.Request(plan));
        Assert.Equal("applied", receipt.Outcome); Assert.True(receipt.CommitKnown); Assert.True(receipt.ReadBackVerified);
        var binding = (await f.Admission.GetIngressBindingAsync("openai", Fixture.Ingress))!;
        Assert.Equal(identity.Hash, binding.Binding.OperatorAcceptancePackageSha256);
        Assert.Equal(Fixture.ApiSecret, f.Runtime.Credential(binding.Owner, binding.Binding, false));
        Assert.Equal(Fixture.WebhookSecret, f.Runtime.Credential(binding.Owner, binding.Binding, true));
        Assert.False((await f.Store.GetReceptionistConfigurationAsync(f.Owner, "openai")).AdmissionEnabled);
        f.Host["Receptionist:Secrets:test_api:Value"] = null;
        f.Host["Receptionist:Secrets:test_webhook:Value"] = null;
        var replay = await f.Operator.ApplyAsync(f.Request(plan));
        Assert.Equal("already_applied", replay.Outcome); Assert.True(replay.CommitKnown); Assert.True(replay.ReadBackVerified);
        Assert.Equal(receipt.DurableReference, replay.DurableReference);
        await using var db = f.Database(); Assert.Equal(2, await db.Set<ReceptionistConfigurationVersion>().CountAsync());
        var snapshot = JsonSerializer.Serialize(await f.Store.GetReceptionistConfigurationAsync(f.Owner, "openai"));
        Assert.DoesNotContain(Fixture.ApiSecret, snapshot); Assert.DoesNotContain(binding.Binding.ProtectedApiCredential!, snapshot);
    }

    [Fact]
    public async Task CredentialRotationLeavesOldCallDrainPinnedAndNewIngressUsesTheNewVersion()
    {
        await using var f = await Fixture.CreateAsync();
        await f.AcceptAsync(); await f.EnableOwnerAsync();
        var oldIngress = (await f.Admission.GetIngressBindingAsync("openai", Fixture.Ingress))!;
        var callId = (await f.Admission.ReserveAsync(f.IngressFor(oldIngress, "old-call"))).CallId!.Value;
        await f.Admission.RecordProviderStateAsync(callId, "accepted", "synthetic-provider-accepted");
        var package = await f.BindingPackageAsync(api: "synthetic-rotated-api-key", webhook: "synthetic-rotated-webhook-key");
        var identity = f.Publish(package); var plan = await f.Operator.PlanAsync(identity.Key, identity.Hash);
        Assert.True((await f.Operator.ApplyAsync(f.Request(plan))).ReadBackVerified);
        f.Host["Receptionist:Secrets:test_api:Value"] = null; f.Host["Receptionist:Secrets:test_webhook:Value"] = null;
        var current = (await f.Admission.GetIngressBindingAsync("openai", Fixture.Ingress))!;
        Assert.NotEqual(oldIngress.ConfigurationId, current.ConfigurationId);
        Assert.Equal("synthetic-rotated-api-key", f.Runtime.Credential(current.Owner, current.Binding, false));
        var original = (await f.Admission.GetCallContextAsync(callId))!;
        Assert.Equal(Fixture.ApiSecret, f.Runtime.Credential(original.Owner, original.Binding, false));
        Assert.Equal(Fixture.WebhookSecret, f.Runtime.Credential(original.Owner, original.Binding, true));
        Assert.Equal(callId, Assert.Single(await f.Admission.FindStopRequestsAsync(10)).CallId);
        Assert.False((await f.Admission.ReserveAsync(f.IngressFor(oldIngress, "stale-route"))).Reserved);
        await Assert.ThrowsAsync<CryptographicException>(() => Task.Run(() => f.Runtime.Credential(MarketingOwnerScope.Business(Guid.NewGuid()), current.Binding, false)));
        Assert.Throws<InvalidOperationException>(() => f.Runtime.Credential(current.Owner, current.Binding with { ProviderProjectKey = "substituted_project" }, false));
        Assert.Throws<CryptographicException>(() => f.Runtime.Credential(current.Owner, current.Binding with { ProtectedApiCredential = current.Binding.ProtectedWebhookSecret }, false));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("funding")]
    [InlineData("evidence")]
    [InlineData("secret_project")]
    [InlineData("bad_api_header")]
    [InlineData("bad_webhook_key")]
    public async Task MismatchedPrivateEvidenceCannotBecomeAnAcceptedBinding(string mismatch)
    {
        await using var f = await Fixture.CreateAsync();
        var package = await f.BindingPackageAsync();
        if (mismatch == "owner") package = package with { ExpectedOwnerKey = MarketingOwnerScope.Business(Guid.NewGuid()).Key };
        if (mismatch == "funding") package = package with { FundingScopes = new() { [f.FundingId] = "unrelated-funding" } };
        if (mismatch == "evidence") package = package with { Evidence = package.Evidence.Where(x => x.Kind != "exclusive_project").ToArray() };
        if (mismatch == "secret_project") f.Host["Receptionist:Secrets:test_api:ProviderProjectKey"] = "wrong_project";
        if (mismatch == "bad_api_header")
        {
            f.Host["Receptionist:Secrets:test_api:Value"] = "invalid api header";
            package = package with { ApiCredentialSha256 = ReceptionistConfigurationCodec.Hash("invalid api header") };
        }
        if (mismatch == "bad_webhook_key")
        {
            f.Host["Receptionist:Secrets:test_webhook:Value"] = "too-short";
            package = package with { WebhookSecretSha256 = ReceptionistConfigurationCodec.Hash("too-short") };
        }
        var identity = f.Publish(package);
        if (mismatch == "evidence") await Assert.ThrowsAsync<ArgumentException>(() => f.Operator.PlanAsync(identity.Key, identity.Hash));
        else Assert.False((await f.Operator.PlanAsync(identity.Key, identity.Hash)).CanApply);
        await using var db = f.Database(); Assert.Single(await db.Set<ReceptionistConfigurationVersion>().ToListAsync());
    }

    [Theory]
    [InlineData("costs_null")]
    [InlineData("capability_null")]
    [InlineData("voice_null")]
    public async Task MalformedPrivateCollectionsRefuseSafelyBeforeAnyMutation(string caseName)
    {
        await using var f = await Fixture.CreateAsync();
        var package = await f.BindingPackageAsync();
        package = caseName switch
        {
            "costs_null" => package with { Binding = package.Binding! with { Costs = null! } },
            "capability_null" => package with { Binding = package.Binding! with { Capabilities = [null!] } },
            _ => (await f.ForAsync("reconcile_dated_voice")) with
                { CallId = Guid.NewGuid(), ExpectedCallRevision = Guid.NewGuid(), VoiceAllocations = [null!] }
        };
        var identity = f.Publish(package); var writes = f.Commands.Writes;
        if (caseName == "costs_null") Assert.False((await f.Operator.PlanAsync(identity.Key, identity.Hash)).CanApply);
        else await Assert.ThrowsAsync<ArgumentException>(() => f.Operator.PlanAsync(identity.Key, identity.Hash));
        Assert.Equal(writes, f.Commands.Writes);
        await using var db = f.Database(); Assert.Single(await db.Set<ReceptionistConfigurationVersion>().ToListAsync());
    }

    [Fact]
    public async Task PackageAndSecretInputsAreRecheckedAfterPreviewAndInsideTheCanonicalTransaction()
    {
        await using var f = await Fixture.CreateAsync();
        var package = await f.BindingPackageAsync(); var identity = f.Publish(package);
        var plan = await f.Operator.PlanAsync(identity.Key, identity.Hash);
        f.Host["Receptionist:Secrets:test_api:Value"] = "unreviewed-secret-rotation";
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Operator.ApplyAsync(f.Request(plan)));
        f.Secrets(Fixture.ApiSecret, Fixture.WebhookSecret);
        f.Commands.BeforeNextUpdate = () => f.Host["Receptionist:OperatorPackages:" + identity.Key + ":Json"] = "{}";
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Operator.ApplyAsync(f.Request(plan)));
        await using var db = f.Database(); Assert.Single(await db.Set<ReceptionistConfigurationVersion>().ToListAsync());
        Assert.Null((await db.MarketingConnections.SingleAsync()).ReceptionistProviderProjectKey);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("tampered")]
    [InlineData("other_package")]
    public async Task ProtectedPreviewCannotAuthorizeAnotherPackageOrOutliveItsReviewWindow(string failure)
    {
        await using var f = await Fixture.CreateAsync();
        var package = await f.BindingPackageAsync(); var identity = f.Publish(package);
        var plan = await f.Operator.PlanAsync(identity.Key, identity.Hash); Assert.True(plan.CanApply);
        var request = f.Request(plan);
        if (failure == "expired") f.Clock.Now = f.Clock.Now.AddMinutes(6);
        if (failure == "tampered") request = request with { ExpectedStateToken = "invalid-" + request.ExpectedStateToken };
        if (failure == "other_package")
        {
            var other = f.Publish(package with { Label = "Another reviewed subject" }, "other_package");
            request = request with { PackageKey = other.Key, PackageSha256 = other.Hash };
        }
        if (failure == "tampered") await Assert.ThrowsAsync<CryptographicException>(() => f.Operator.ApplyAsync(request));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => f.Operator.ApplyAsync(request));
        await using var db = f.Database(); Assert.Single(await db.Set<ReceptionistConfigurationVersion>().ToListAsync());
        Assert.Null((await db.MarketingConnections.SingleAsync()).ReceptionistProviderProjectKey);
    }

    [Fact]
    public async Task SettingsRevisionAndReservationChangesInvalidateAReviewedCeilingApply()
    {
        await using var f = await Fixture.CreateAsync();
        await f.AcceptAsync(); await f.EnableOwnerAsync();
        var package = (await f.ForAsync("approve_budget_ceilings", budgetId: f.OwnerBudgetId)) with { CashLimit = 50m, ConsumptionLimit = 90m };
        var identity = f.Publish(package); var plan = await f.Operator.PlanAsync(identity.Key, identity.Hash);
        Assert.True(plan.CanApply);
        var ingress = (await f.Admission.GetIngressBindingAsync("openai", Fixture.Ingress))!;
        Assert.True((await f.Admission.ReserveAsync(f.IngressFor(ingress, "changed-reservation"))).Reserved);
        // Reserve changes liabilities without changing the account's administrative revision.
        Assert.Equal(package.ExpectedAccountRevisions[f.OwnerBudgetId], (await f.Admission.GetBudgetAccountAsync(f.OwnerBudgetId))!.Revision);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => f.Operator.ApplyAsync(f.Request(plan)));
        var staleBinding = await f.BindingPackageAsync(); var staleIdentity = f.Publish(staleBinding, "stale_binding");
        var stalePlan = await f.Operator.PlanAsync(staleIdentity.Key, staleIdentity.Hash);
        var current = await f.Store.GetReceptionistConfigurationAsync(f.Owner, "openai");
        await f.Store.SaveReceptionistConfigurationAsync(f.Owner, "openai", current.ConnectionRevision,
            current.Configuration! with { BusinessPolicy = "A newer owner edit" }, "test-owner");
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => f.Operator.ApplyAsync(f.Request(stalePlan)));
        await using var db = f.Database();
        Assert.All(await db.Set<ReceptionistBudgetPeriod>().ToListAsync(), p => Assert.Null(p.CeilingApprovalReceipt));
    }

    [Fact]
    public async Task AnExactOpeningCanBeAppliedAndReadBackWithoutEnablingOrResettingIt()
    {
        await using var f = await Fixture.CreateAsync(withBudgets: false);
        var package = await f.ForAsync("open_budget_period");
        package = package with { Opening = new("owner", f.Owner.Key, "USD", "UTC", 2026, 2, 40m, 80m, 3m, 5m, 12, package.ReceiptReference, f.Clock.Now.AddSeconds(-1)) };
        var identity = f.Publish(package); var plan = await f.Operator.PlanAsync(identity.Key, identity.Hash);
        var result = await f.Operator.ApplyAsync(f.Request(plan));
        Assert.True(result.CommitKnown); Assert.True(result.ReadBackVerified);
        var replay = await f.Operator.ApplyAsync(f.Request(plan)); Assert.Equal("already_applied", replay.Outcome);
        await using var db = f.Database();
        var account = await db.Set<ReceptionistBudgetAccount>().SingleAsync(); Assert.False(account.AdmissionEnabled);
        var period = await db.Set<ReceptionistBudgetPeriod>().SingleAsync();
        Assert.Equal(3m, period.CashCommitted); Assert.Equal(5m, period.ConsumptionCommitted); Assert.Equal(12, period.VoiceSecondsCommitted);
        var changed = package with { Opening = package.Opening with { KnownCashCommitted = 0m } };
        var next = f.Publish(changed, "changed_opening"); Assert.False((await f.Operator.PlanAsync(next.Key, next.Hash)).CanApply);
        Assert.Equal(3m, (await db.Set<ReceptionistBudgetPeriod>().AsNoTracking().SingleAsync()).CashCommitted);
    }

    [Fact]
    public async Task BudgetCeilingsAndAdmissionUseScopedExpectedRevisionsAndTruthfulStateReadback()
    {
        await using var f = await Fixture.CreateAsync();
        var ceiling = (await f.ForAsync("approve_budget_ceilings", budgetId: f.OwnerBudgetId)) with { CashLimit = 55m, ConsumptionLimit = 95m };
        Assert.True((await f.ApplyAsync(ceiling)).ReadBackVerified);
        var disable = (await f.ForAsync("set_budget_admission", budgetId: f.OwnerBudgetId)) with { Enabled = false };
        var identity = f.Publish(disable); var plan = await f.Operator.PlanAsync(identity.Key, identity.Hash);
        var receipt = await f.Operator.ApplyAsync(f.Request(plan));
        Assert.Equal("applied", receipt.Outcome); Assert.True(receipt.CommitKnown);
        var replay = await f.Operator.ApplyAsync(f.Request(plan));
        Assert.Equal("already_satisfied", replay.Outcome); Assert.False(replay.CommitKnown); Assert.True(replay.ReadBackVerified);
        Assert.Null(replay.DurableReference);
        await using var db = f.Database(); var period = await db.Set<ReceptionistBudgetPeriod>().SingleAsync(x => x.BudgetAccountId == f.OwnerBudgetId);
        Assert.Equal(55m, period.CashLimit); Assert.Equal(95m, period.ConsumptionLimit);
        Assert.Equal(40m, period.OpeningCashCeiling); Assert.Equal(80m, period.OpeningConsumptionCeiling);
        Assert.False((await f.Admission.GetBudgetAccountAsync(f.OwnerBudgetId))!.AdmissionEnabled);
    }

    [Fact]
    public async Task CallClosureDatedVoiceAndCostUseExactReceiptsAndNeverChargeAReplayTwice()
    {
        await using var f = await Fixture.CreateAsync(); await f.AcceptAsync(); await f.EnableOwnerAsync();
        var ingress = (await f.Admission.GetIngressBindingAsync("openai", Fixture.Ingress))!;
        var call = (await f.Admission.ReserveAsync(f.IngressFor(ingress, "accounted-call"))).CallId!.Value;
        f.Clock.Now = f.Clock.Now.AddSeconds(5);
        var closure = (await f.ForAsync("record_provider_closure", callId: call)) with { ObservedProviderClosedUtc = f.Clock.Now };
        Assert.True((await f.ApplyAsync(closure)).ReadBackVerified);
        var usage = (await f.ForAsync("reconcile_dated_voice", callId: call)) with
        { VoiceAllocations = [new(f.Clock.Now.AddSeconds(-2), 4)], FinalVoiceSeconds = 4 };
        Assert.True((await f.ApplyAsync(usage)).ReadBackVerified);
        var cost = (await f.ForAsync("reconcile_call_cost", callId: call)) with
        { ComponentKey = "provider_cost", CallCost = new(f.Clock.Now, 1.25m, f.Clock.Now.AddSeconds(-1), 2.5m, 1.25m, 2.5m) };
        var identity = f.Publish(cost); var plan = await f.Operator.PlanAsync(identity.Key, identity.Hash);
        var first = await f.Operator.ApplyAsync(f.Request(plan)); var replay = await f.Operator.ApplyAsync(f.Request(plan));
        Assert.Equal("applied", first.Outcome); Assert.Equal("already_applied", replay.Outcome); Assert.Equal(first.DurableReference, replay.DurableReference);
        await using var db = f.Database();
        Assert.Equal(3, await db.Set<ReceptionistUsageReceipt>().CountAsync());
        var row = await db.Set<ReceptionistCall>().SingleAsync(); Assert.True(row.VoiceUsageFinal); Assert.Equal(4, row.ObservedVoiceSeconds);
        foreach (var period in await db.Set<ReceptionistBudgetPeriod>().ToListAsync())
        { Assert.Equal(1.25m, period.CashCommitted); Assert.Equal(2.5m, period.ConsumptionCommitted); }
        var conflict = cost with { CallCost = cost.CallCost with { CumulativeCashForPeriod = 1.5m, FinalCashTotal = 1.5m } };
        var changed = f.Publish(conflict, "changed_receipt"); Assert.False((await f.Operator.PlanAsync(changed.Key, changed.Hash)).CanApply);
    }

    [Fact]
    public async Task AccountChargesKeepSeparateDatedFactsAndRejectCrossOwnerTargets()
    {
        await using var f = await Fixture.CreateAsync(); f.Clock.Now = f.Clock.Now.AddMinutes(1);
        var package = await f.ForAsync("reconcile_account_charge");
        package = package with { AccountCharge = new(f.OwnerBudgetId, f.FundingId, "synthetic-charge", "rental", package.ReceiptReference,
            f.Clock.Now, 2m, f.Clock.Now.AddSeconds(-30), 3m, true, true) };
        var identity = f.Publish(package); var plan = await f.Operator.PlanAsync(identity.Key, identity.Hash);
        Assert.True((await f.Operator.ApplyAsync(f.Request(plan))).ReadBackVerified);
        Assert.Equal("already_applied", (await f.Operator.ApplyAsync(f.Request(plan))).Outcome);
        await using var db = f.Database(); var charge = await db.Set<ReceptionistAccountCharge>().SingleAsync();
        Assert.Equal(f.Clock.Now, charge.CashDebitedUtc); Assert.Equal(f.Clock.Now.AddSeconds(-30), charge.ConsumptionIncurredUtc);
        Assert.Single(await db.Set<ReceptionistUsageReceipt>().ToListAsync());
        var bad = package with { AccountCharge = package.AccountCharge with { OwnerBudgetAccountId = f.FundingId } };
        var other = f.Publish(bad, "wrong_owner_charge");
        await Assert.ThrowsAsync<ArgumentException>(() => f.Operator.PlanAsync(other.Key, other.Hash));
    }

    [Fact]
    public async Task LostCommitResponseRequiresExactReceiptOrReportsOnlyVerifiedStateSatisfaction()
    {
        await using var f = await Fixture.CreateAsync();
        var binding = await f.BindingPackageAsync(); var identity = f.Publish(binding);
        var plan = await f.Operator.PlanAsync(identity.Key, identity.Hash);
        f.Transactions.FailAfterNextCommit = true;
        var accepted = await f.Operator.ApplyAsync(f.Request(plan));
        Assert.Equal("already_applied", accepted.Outcome); Assert.True(accepted.CommitKnown); Assert.True(accepted.ReadBackVerified);
        var enable = (await f.ForAsync("set_owner_admission")) with { Enabled = true };
        var next = f.Publish(enable, "admission_after_loss"); var nextPlan = await f.Operator.PlanAsync(next.Key, next.Hash);
        f.Transactions.FailAfterNextCommit = true;
        var observed = await f.Operator.ApplyAsync(f.Request(nextPlan));
        Assert.Equal("already_satisfied", observed.Outcome); Assert.False(observed.CommitKnown); Assert.True(observed.ReadBackVerified);
        Assert.Null(observed.DurableReference);
        Assert.True((await f.Store.GetReceptionistConfigurationAsync(f.Owner, "openai")).AdmissionEnabled);
    }

    [Fact]
    public async Task FailedTransactionHasUnknownOutcomeWithoutInventingADurableReceipt()
    {
        await using var f = await Fixture.CreateAsync();
        var package = await f.BindingPackageAsync(); var identity = f.Publish(package);
        var plan = await f.Operator.PlanAsync(identity.Key, identity.Hash);
        f.Transactions.FailBeforeNextCommit = true;
        var result = await f.Operator.ApplyAsync(f.Request(plan));
        Assert.Equal("commit_unknown", result.Outcome); Assert.False(result.CommitKnown); Assert.False(result.ReadBackVerified); Assert.Null(result.DurableReference);
        await using var db = f.Database(); Assert.Single(await db.Set<ReceptionistConfigurationVersion>().ToListAsync());
        Assert.Null((await db.MarketingConnections.SingleAsync()).ReceptionistProviderProjectKey);
    }

    [Fact]
    public async Task KnownCommitWithReadbackOutageDoesNotClaimIndependentVerification()
    {
        await using var f = await Fixture.CreateAsync();
        var package = await f.BindingPackageAsync(); var identity = f.Publish(package);
        var plan = await f.Operator.PlanAsync(identity.Key, identity.Hash);
        f.Transactions.AfterNextCommit = () => f.Commands.RefuseReads = true;
        var result = await f.Operator.ApplyAsync(f.Request(plan));
        Assert.Equal("applied", result.Outcome); Assert.True(result.CommitKnown); Assert.False(result.ReadBackVerified);
        f.Commands.RefuseReads = false;
        await using var db = f.Database(); Assert.Equal(2, await db.Set<ReceptionistConfigurationVersion>().CountAsync());
    }

    [Fact]
    public async Task FrontendPreparationPersistsUnreviewedWebsiteProposalAndUsesCanonicalState()
    {
        await using var f = await Fixture.CreateAsync();
        var input = await f.WebsiteProposalAsync();
        var id = await f.Operator.PrepareForOwnerAsync(f.Owner, input, (_, _) => Task.FromResult(true));
        await using var db = f.Database();
        var row = await db.Set<ReceptionistOperationPackage>().SingleAsync(x => x.Id == id);
        var package = JsonSerializer.Deserialize<ReceptionistOperatorPackage>(row.PayloadJson, ReceptionistConfigurationCodec.Json)!;
        Assert.Equal("pending", row.ReviewState); Assert.Null(row.ReviewedByActorId); Assert.Null(row.ReviewedUtc);
        Assert.Empty(package.ReviewedByFounderOid); Assert.Equal(default, package.ReviewedUtc);
        Assert.Equal(f.Owner.Key, package.ExpectedOwnerKey); Assert.Equal(input.ExpectedConnectionRevision, package.ExpectedConnectionRevision);
        Assert.Equal(f.FundingId, Assert.Single(package.Binding!.Costs).FundingBudgetAccountId);
        Assert.Equal("webrtc", package.Binding.Transport); Assert.Empty(package.Binding.WebhookSecretReference);
        Assert.Null(package.Binding.ProtectedApiCredential); Assert.Null(package.Binding.PublicActivationReceipt);
        Assert.False((await db.MarketingConnections.SingleAsync()).ReceptionistAdmissionEnabled);
        Assert.Null((await db.Set<ReceptionistConfigurationVersion>().SingleAsync()).AcceptedBindingJson);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Operator.PlanForOwnerAsync(f.Owner, "saved-" + id.ToString("N"),
            row.PayloadSha256, (_, _) => Task.FromResult(true)));
        var preview = await f.Operator.PreviewPendingForOwnerAsync(f.Owner, id, row.PayloadSha256, (_, _) => Task.FromResult(true));
        Assert.False(preview.CanApply); Assert.Equal(row.Revision, preview.PendingReviewRevision);
        Assert.Contains(preview.Facts, x => x.Label == "Proposed evidence: exclusive_project");
        await f.Operator.ReviewForOwnerAsync(f.Owner, id, row.Revision, row.PayloadSha256, true, (_, _) => Task.FromResult(true));
        var plan = await f.Operator.PlanForOwnerAsync(f.Owner, "saved-" + id.ToString("N"), row.PayloadSha256, (_, _) => Task.FromResult(true));
        Assert.True(plan.CanApply);
        var receipt = await f.Operator.ApplyForOwnerAsync(f.Owner, f.Request(plan), (_, _) => Task.FromResult(true));
        Assert.True(receipt.ReadBackVerified);
        Assert.False((await f.Store.GetReceptionistConfigurationAsync(f.Owner, "openai")).AdmissionEnabled);
    }

    [Fact]
    public async Task FrontendPreparationRejectsWrongOwnerStaleStateUnverifiedEvidenceAndUnrelatedFunding()
    {
        await using var f = await Fixture.CreateAsync();
        var input = await f.WebsiteProposalAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Operator.PrepareForOwnerAsync(f.Owner, input, (_, _) => Task.FromResult(false)));
        var original = input.ExpectedConnectionRevision; input.ExpectedConnectionRevision = Guid.NewGuid();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => f.Operator.PrepareForOwnerAsync(f.Owner, input, (_, _) => Task.FromResult(true)));
        input.ExpectedConnectionRevision = original;
        input.Evidence[0].ObservedUtc = f.Clock.Now.AddMinutes(1);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Operator.PrepareForOwnerAsync(f.Owner, input, (_, _) => Task.FromResult(true)));
        input.Evidence[0].ObservedUtc = f.Clock.Now;
        input.Costs[0].FundingAccountId = Guid.NewGuid();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Operator.PrepareForOwnerAsync(f.Owner, input, (_, _) => Task.FromResult(true)));
        await using var db = f.Database(); Assert.Empty(await db.Set<ReceptionistOperationPackage>().ToListAsync());
    }

    [Fact]
    public async Task TenantCannotUseFounderFundingChoicesOrReviewItsOwnProposal()
    {
        await using var f = await Fixture.CreateAsync();
        var input = await f.WebsiteProposalAsync();
        f.Http.HttpContext!.User = new ClaimsPrincipal(new ClaimsIdentity([new("oid", "00000000-0000-0000-0000-000000000022")], "test"));
        var summary = await f.Operator.ManagementAsync(f.Owner, (_, _) => Task.FromResult(true));
        Assert.False(summary.CanReview); Assert.Empty(summary.FundingChoices!);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Operator.PrepareForOwnerAsync(f.Owner, input, (_, _) => Task.FromResult(true)));
    }

    [Fact]
    public async Task TenantPreparesWithItsExistingFundingButCannotReviewOrEnableWithoutAcceptance()
    {
        await using var f = await Fixture.CreateAsync(owner: MarketingOwnerScope.Business(Guid.NewGuid()));
        await f.AcceptAsync();
        var input = await f.WebsiteProposalAsync();
        f.Http.HttpContext!.User = new ClaimsPrincipal(new ClaimsIdentity([new("oid", "00000000-0000-0000-0000-000000000022")], "test"));
        var summary = await f.Operator.ManagementAsync(f.Owner, (_, _) => Task.FromResult(true));
        Assert.Equal(f.FundingId, Assert.Single(summary.FundingChoices!).Id);
        var id = await f.Operator.PrepareForOwnerAsync(f.Owner, input, (_, _) => Task.FromResult(true));
        await using var db = f.Database(); var row = await db.Set<ReceptionistOperationPackage>().SingleAsync(x => x.Id == id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Operator.PreviewPendingForOwnerAsync(f.Owner, id, row.PayloadSha256, (_, _) => Task.FromResult(true)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Operator.ReviewForOwnerAsync(f.Owner, id, row.Revision, row.PayloadSha256, true, (_, _) => Task.FromResult(true)));
        Assert.False((await db.MarketingConnections.SingleAsync()).ReceptionistAdmissionEnabled);
    }

    [Fact]
    public async Task PublicPreparationRequiresActivationEvidenceAndRevocationInvalidatesPendingCredential()
    {
        await using var f = await Fixture.CreateAsync(); var input = await f.WebsiteProposalAsync();
        input.ControlledTestOnly = false;
        await Assert.ThrowsAsync<ArgumentException>(() => f.Operator.PrepareForOwnerAsync(f.Owner, input, (_, _) => Task.FromResult(true)));
        input.ControlledTestOnly = true;
        var id = await f.Operator.PrepareForOwnerAsync(f.Owner, input, (_, _) => Task.FromResult(true));
        await using var db = f.Database(); var row = await db.Set<ReceptionistOperationPackage>().SingleAsync(x => x.Id == id);
        await f.Operator.RevokeCredentialAsync(f.Owner, input.ApiCredentialVersionId, input.ExpectedConnectionRevision, (_, _) => Task.FromResult(true));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => f.Operator.ReviewForOwnerAsync(f.Owner, id, row.Revision,
            row.PayloadSha256, true, (_, _) => Task.FromResult(true)));
    }

    [Fact]
    public async Task AcceptanceWindowIsReviewedImmutableAndRevocationIsMonotonic()
    {
        await using var f = await Fixture.CreateAsync(); await f.AcceptAsync();
        var snapshot = await f.Store.GetReceptionistConfigurationAsync(f.Owner, "openai");
        ReceptionistOperationPreparationInput Input(string operation) => new()
        {
            Operation = operation, ExpectedConnectionRevision = snapshot.ConnectionRevision,
            WindowStartsUtc = f.Clock.Now, WindowExpiresUtc = f.Clock.Now.AddMinutes(30),
            Evidence = [new() { Kind = operation, Reference = "synthetic-restricted-route",
                Sha256 = ReceptionistConfigurationCodec.Hash("synthetic-restricted-route"), ObservedUtc = f.Clock.Now }]
        };
        async Task<ReceptionistOperatorPlan> Review(string operation)
        {
            var id = await f.Operator.PrepareForOwnerAsync(f.Owner, Input(operation), (_, _) => Task.FromResult(true));
            await using var db = f.Database(); var row = await db.Set<ReceptionistOperationPackage>().SingleAsync(x => x.Id == id);
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Operator.PlanForOwnerAsync(f.Owner, "saved-" + id.ToString("N"), row.PayloadSha256, (_, _) => Task.FromResult(true)));
            await f.Operator.ReviewForOwnerAsync(f.Owner, id, row.Revision, row.PayloadSha256, true, (_, _) => Task.FromResult(true));
            return await f.Operator.PlanForOwnerAsync(f.Owner, "saved-" + id.ToString("N"), row.PayloadSha256, (_, _) => Task.FromResult(true));
        }
        var plan = await Review("create_acceptance_window"); Assert.True(plan.CanApply);
        Assert.True((await f.Operator.ApplyForOwnerAsync(f.Owner, f.Request(plan), (_, _) => Task.FromResult(true))).ReadBackVerified);
        await using (var db = f.Database())
        {
            var row = await db.Set<ReceptionistAcceptanceWindow>().SingleAsync();
            Assert.Equal(f.Clock.Now.AddMinutes(30), row.ExpiresUtc); Assert.Null(row.RevokedUtc);
            Assert.False(string.IsNullOrWhiteSpace(row.ReviewedByActorId));
        }
        Assert.True((await f.Operator.ApplyForOwnerAsync(f.Owner, f.Request(plan), (_, _) => Task.FromResult(true))).ReadBackVerified);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Operator.PrepareForOwnerAsync(f.Owner, Input("create_acceptance_window"), (_, _) => Task.FromResult(true)));
        var revoke = await Review("revoke_acceptance_window"); Assert.True(revoke.CanApply);
        Assert.True((await f.Operator.ApplyForOwnerAsync(f.Owner, f.Request(revoke), (_, _) => Task.FromResult(true))).ReadBackVerified);
        await using (var db = f.Database()) Assert.NotNull((await db.Set<ReceptionistAcceptanceWindow>().SingleAsync()).RevokedUtc);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Operator.PrepareForOwnerAsync(f.Owner, Input("create_acceptance_window"), (_, _) => Task.FromResult(true)));
    }

    [Fact]
    public async Task CompletedWindowRemainsVerifiableAfterItsExpiryWithoutRecreation()
    {
        await using var f = await Fixture.CreateAsync(); await f.AcceptAsync();
        var snapshot = await f.Store.GetReceptionistConfigurationAsync(f.Owner, "openai");
        var input = new ReceptionistOperationPreparationInput
        {
            Operation = "create_acceptance_window", ExpectedConnectionRevision = snapshot.ConnectionRevision,
            WindowStartsUtc = f.Clock.Now, WindowExpiresUtc = f.Clock.Now.AddMinutes(10),
            Evidence = [new() { Kind = "create_acceptance_window", Reference = "synthetic-route",
                Sha256 = new string('a', 64), ObservedUtc = f.Clock.Now }]
        };
        var id = await f.Operator.PrepareForOwnerAsync(f.Owner, input, (_, _) => Task.FromResult(true));
        await using var db = f.Database(); var row = await db.Set<ReceptionistOperationPackage>().SingleAsync(x => x.Id == id);
        await f.Operator.ReviewForOwnerAsync(f.Owner, id, row.Revision, row.PayloadSha256, true, (_, _) => Task.FromResult(true));
        var key = "saved-" + id.ToString("N");
        var plan = await f.Operator.PlanForOwnerAsync(f.Owner, key, row.PayloadSha256, (_, _) => Task.FromResult(true));
        Assert.True((await f.Operator.ApplyForOwnerAsync(f.Owner, f.Request(plan), (_, _) => Task.FromResult(true))).ReadBackVerified);
        f.Clock.Now = f.Clock.Now.AddMinutes(11);
        var replay = await f.Operator.PlanForOwnerAsync(f.Owner, key, row.PayloadSha256, (_, _) => Task.FromResult(true));
        Assert.True(replay.CanApply);
        Assert.True((await f.Operator.ApplyForOwnerAsync(f.Owner, f.Request(replay), (_, _) => Task.FromResult(true))).ReadBackVerified);
        Assert.Single(await db.Set<ReceptionistAcceptanceWindow>().ToArrayAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Operator.PrepareForOwnerAsync(f.Owner, input, (_, _) => Task.FromResult(true)));
    }

    [Fact]
    public async Task ConcurrentReviewedWindowsCannotCreateTwoLifetimeIdentities()
    {
        await using var f = await Fixture.CreateAsync(); await f.AcceptAsync();
        var snapshot = await f.Store.GetReceptionistConfigurationAsync(f.Owner, "openai");
        var input = new ReceptionistOperationPreparationInput
        {
            Operation = "create_acceptance_window", ExpectedConnectionRevision = snapshot.ConnectionRevision,
            WindowStartsUtc = f.Clock.Now, WindowExpiresUtc = f.Clock.Now.AddMinutes(10),
            Evidence = [new() { Kind = "create_acceptance_window", Reference = "synthetic-route", Sha256 = new string('a', 64), ObservedUtc = f.Clock.Now }]
        };
        var plans = new List<ReceptionistOperatorPlan>();
        for (var i = 0; i < 2; i++)
        {
            var id = await f.Operator.PrepareForOwnerAsync(f.Owner, input, (_, _) => Task.FromResult(true));
            await using var db = f.Database(); var row = await db.Set<ReceptionistOperationPackage>().SingleAsync(x => x.Id == id);
            await f.Operator.ReviewForOwnerAsync(f.Owner, id, row.Revision, row.PayloadSha256, true, (_, _) => Task.FromResult(true));
            plans.Add(await f.Operator.PlanForOwnerAsync(f.Owner, "saved-" + id.ToString("N"), row.PayloadSha256, (_, _) => Task.FromResult(true)));
        }
        async Task<bool> Apply(ReceptionistOperatorPlan plan)
        {
            try { return (await f.Operator.ApplyForOwnerAsync(f.Owner, f.Request(plan), (_, _) => Task.FromResult(true))).ReadBackVerified; }
            catch (Exception e) when (e is InvalidOperationException or DbUpdateConcurrencyException) { return false; }
        }
        var results = await Task.WhenAll(plans.Select(plan => Task.Run(() => Apply(plan))));
        Assert.Single(results.Where(x => x));
        await using var read = f.Database(); Assert.Single(await read.Set<ReceptionistAcceptanceWindow>().ToArrayAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    [InlineData(-1)]
    public async Task AcceptanceWindowRejectsInvalidDuration(int minutes)
    {
        await using var f = await Fixture.CreateAsync(); await f.AcceptAsync();
        var snapshot = await f.Store.GetReceptionistConfigurationAsync(f.Owner, "openai");
        var input = new ReceptionistOperationPreparationInput
        {
            Operation = "create_acceptance_window", ExpectedConnectionRevision = snapshot.ConnectionRevision,
            WindowStartsUtc = f.Clock.Now, WindowExpiresUtc = f.Clock.Now.AddMinutes(minutes),
            Evidence = [new() { Kind = "create_acceptance_window", Reference = "synthetic-route",
                Sha256 = ReceptionistConfigurationCodec.Hash("synthetic-route"), ObservedUtc = f.Clock.Now }]
        };
        await Assert.ThrowsAsync<ArgumentException>(() => f.Operator.PrepareForOwnerAsync(f.Owner, input, (_, _) => Task.FromResult(true)));
        await using var db = f.Database(); Assert.Empty(await db.Set<ReceptionistAcceptanceWindow>().ToArrayAsync());
    }

    [Fact]
    public async Task OwnerPermissionPreparationUsesCurrentAcceptedConfigurationAndSeparateReview()
    {
        await using var f = await Fixture.CreateAsync(); await f.AcceptAsync();
        var snapshot = await f.Store.GetReceptionistConfigurationAsync(f.Owner, "openai");
        var input = new ReceptionistOperationPreparationInput
        {
            Operation = "set_owner_admission", Enabled = true, ExpectedConnectionRevision = snapshot.ConnectionRevision,
            Evidence = [new() { Kind = "set_owner_admission", Reference = "synthetic-permission",
                Sha256 = ReceptionistConfigurationCodec.Hash("synthetic-permission"), ObservedUtc = f.Clock.Now }]
        };
        var id = await f.Operator.PrepareForOwnerAsync(f.Owner, input, (_, _) => Task.FromResult(true));
        await using var db = f.Database(); var row = await db.Set<ReceptionistOperationPackage>().SingleAsync(x => x.Id == id);
        Assert.False((await f.Store.GetReceptionistConfigurationAsync(f.Owner, "openai")).AdmissionEnabled);
        await f.Operator.ReviewForOwnerAsync(f.Owner, id, row.Revision, row.PayloadSha256, true, (_, _) => Task.FromResult(true));
        var plan = await f.Operator.PlanForOwnerAsync(f.Owner, "saved-" + id.ToString("N"), row.PayloadSha256, (_, _) => Task.FromResult(true));
        Assert.True(plan.CanApply); Assert.True((await f.Operator.ApplyForOwnerAsync(f.Owner, f.Request(plan), (_, _) => Task.FromResult(true))).ReadBackVerified);
        Assert.True((await f.Store.GetReceptionistConfigurationAsync(f.Owner, "openai")).AdmissionEnabled);
    }

    [Theory]
    [InlineData("accept_binding")]
    [InlineData("set_owner_admission")]
    public async Task PreparationFormBindingAcceptsOnlyConditionallyRequiredFields(string operation)
    {
        var collection = new ServiceCollection(); collection.AddLogging(); collection.AddMvcCore().AddDataAnnotations();
        using var services = collection.BuildServiceProvider();
        var controller = new PreparationBindingController
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services } },
            MetadataProvider = services.GetRequiredService<IModelMetadataProvider>(),
            ModelBinderFactory = services.GetRequiredService<IModelBinderFactory>(),
            ObjectValidator = services.GetRequiredService<IObjectModelValidator>()
        };
        var fields = new Dictionary<string, StringValues>
        {
            ["Operation"] = operation, ["ExpectedConnectionRevision"] = Guid.NewGuid().ToString(),
            ["Evidence[0].Kind"] = operation, ["Evidence[0].Reference"] = "synthetic-evidence",
            ["Evidence[0].Sha256"] = new string('a', 64), ["Evidence[0].ObservedUtc"] = "2026-10-26T12:00:00Z"
        };
        if (operation == "accept_binding")
        {
            fields["Model"] = "gpt-live-1"; fields["Voice"] = "marin";
            fields["Evidence[1].Kind"] = "public_activation"; fields["Evidence[1].Reference"] = "";
            fields["Evidence[1].Sha256"] = ""; fields["Evidence[1].ObservedUtc"] = "";
        }
        var input = new ReceptionistOperationPreparationInput();
        Assert.True(await controller.TryUpdateModelAsync(input, "", new FormValueProvider(BindingSource.Form,
            new FormCollection(fields), CultureInfo.InvariantCulture)),
            string.Join("; ", controller.ModelState.SelectMany(x => x.Value!.Errors.Select(e => x.Key + ":" + e.ErrorMessage))));
        Assert.Equal(DateTimeKind.Utc, input.Evidence[0].ObservedUtc!.Value.Kind);
        if (operation == "accept_binding") Assert.Null(input.Evidence[1].Reference);
        else { Assert.Null(input.Model); Assert.Null(input.Voice); }
    }

    private sealed class PreparationBindingController : Controller { }

    private sealed class TestClock(DateTime now) : TimeProvider
    {
        internal DateTime Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => new(Now);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal const string FounderOid = "00000000-0000-0000-0000-000000000011";
        internal const string Project = "proj_operator_test_project";
        internal const string Ingress = "operator_test_ingress";
        internal const string ApiSecret = "synthetic-operator-api-key";
        internal const string WebhookSecret = "synthetic-operator-webhook-key";
        private readonly string path = Path.Combine(Path.GetTempPath(), "reception-operator-" + Guid.NewGuid().ToString("N") + ".sqlite");
        private MasterAppDbContext storeContext = null!;
        internal MarketingOwnerScope Owner = MarketingOwnerScope.Founder;
        internal readonly TestClock Clock = new(new DateTime(2026, 2, 3, 12, 0, 0, DateTimeKind.Utc));
        internal readonly CommandProbe Commands = new();
        internal readonly TransactionProbe Transactions = new();
        internal readonly MarketingCredentialProtector Protector = new(new EphemeralDataProtectionProvider());
        internal readonly IConfigurationRoot Host = new ConfigurationBuilder().AddInMemoryCollection().Build();
        internal readonly TestHttpContextAccessor Http = new();
        internal DbContextOptions<MasterAppDbContext> Options { get; private set; } = null!;
        internal Guid OwnerBudgetId { get; private set; }
        internal Guid FundingId { get; private set; }
        internal MarketingConnectionStore Store { get; private set; } = null!;
        internal ReceptionistAdmissionService Admission => new(Options, Clock);
        internal ReceptionistOperatorService Operator => new(Store, Admission, Host, Protector, Http, Clock,
            new ServiceCollection().AddSingleton(Options).BuildServiceProvider());
        internal ReceptionistRuntimeAuthority Runtime => new(Host, Protector);
        internal MasterAppDbContext Database() => new(Options);

        internal static async Task<Fixture> CreateAsync(bool withBudgets = true, MarketingOwnerScope? owner = null)
        {
            var f = new Fixture();
            if (owner is not null) f.Owner = owner;
            var connection = "Data Source=" + f.path + ";Pooling=False;Default Timeout=20";
            await using (var schema = new ReceptionSchemaContext(new DbContextOptionsBuilder().UseSqlite(connection).Options))
            {
                f.Options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).UseModel(schema.Model)
                    .AddInterceptors(f.Commands, f.Transactions).Options;
                await schema.Database.EnsureCreatedAsync();
            }
            f.storeContext = f.Database(); f.Store = new(f.storeContext, f.Protector);
            if (f.Owner.CommerceBusinessId is { } businessId)
            { f.storeContext.Add(new CommerceBusiness { Id = businessId }); await f.storeContext.SaveChangesAsync(); }
            f.Host["FOUNDER_OID"] = FounderOid;
            f.Http.HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new("oid", FounderOid)], "test")) };
            await f.Store.SaveReceptionistConfigurationAsync(f.Owner, "openai", Guid.Empty, Configuration(), "synthetic-owner");
            if (withBudgets)
            {
                f.OwnerBudgetId = await f.OpenAsync("owner", f.Owner.Key);
                f.FundingId = await f.OpenAsync("funding", "synthetic-provider-funding");
            }
            f.Secrets(ApiSecret, WebhookSecret);
            return f;
        }

        private async Task<Guid> OpenAsync(string kind, string scope)
        {
            var id = await Admission.OpenBudgetPeriodAsync(new(kind, scope, "USD", "UTC", 2026, 2,
                40m, 80m, 0m, 0m, 0, "synthetic-opening:" + scope, Clock.Now.AddTicks(-1)));
            await Admission.SetBudgetAdmissionAsync(id, (await Admission.GetBudgetAccountAsync(id))!.Revision, true);
            return id;
        }

        internal void Secrets(string api, string webhook)
        {
            foreach (var (reference, kind, value) in new[] { ("test_api", "voice-api", api), ("test_webhook", "voice-webhook", webhook) })
            {
                var root = "Receptionist:Secrets:" + reference + ":";
                Host[root + "OwnerKey"] = Owner.Key; Host[root + "Provider"] = "openai";
                Host[root + "ProviderProjectKey"] = Project; Host[root + "Kind"] = kind; Host[root + "Value"] = value;
            }
        }

        internal async Task<ReceptionistOperationPreparationInput> WebsiteProposalAsync()
        {
            var snapshot = await Store.GetReceptionistConfigurationAsync(Owner, "openai");
            var credential = await Operator.SaveCredentialAsync(Owner, new(snapshot.ConnectionRevision, Project, "voice-api", ApiSecret),
                (_, _) => Task.FromResult(true));
            snapshot = await Store.GetReceptionistConfigurationAsync(Owner, "openai");
            return new()
            {
                ExpectedConnectionRevision = snapshot.ConnectionRevision, Operation = "accept_binding", ApiCredentialVersionId = credential,
                Model = "gpt-live-1", Voice = "marin", MaximumSupportedCallSeconds = 120, ControlledTestOnly = true,
                Costs = [new() { Key = "provider_voice", FundingAccountId = FundingId, CashFixedMaximum = 2m, ConsumptionFixedMaximum = 3m }],
                Evidence = new[] { "provider_setup", "exclusive_project", "cost_bounds", "disclosure", "retention", "website_fallback", "capability.canonical_intake" }
                    .Select(x => new ReceptionistPreparationEvidenceInput { Kind = x, Reference = "synthetic-" + x,
                        Sha256 = ReceptionistConfigurationCodec.Hash("synthetic-" + x), ObservedUtc = Clock.Now }).ToList()
            };
        }

        internal async Task<ReceptionistOperatorPackage> ForAsync(string operation, Guid? callId = null, Guid? budgetId = null)
        {
            await using var db = Database();
            var connection = await db.MarketingConnections.AsNoTracking().SingleAsync();
            var call = callId.HasValue ? await db.Set<ReceptionistCall>().AsNoTracking().SingleAsync(x => x.Id == callId) : null;
            var latest = await db.Set<ReceptionistConfigurationVersion>().AsNoTracking()
                .Where(x => x.MarketingConnectionId == connection.Id).OrderByDescending(x => x.Version).FirstAsync();
            var versionId = call?.ConfigurationVersionId ?? (operation == "set_owner_admission"
                ? connection.ReceptionistActiveConfigurationId!.Value : latest.Id);
            var version = await db.Set<ReceptionistConfigurationVersion>().AsNoTracking().SingleAsync(x => x.Id == versionId);
            Guid[] ids = operation switch
            {
                "accept_binding" or "reconcile_account_charge" or "reconcile_call_cost" or "reconcile_dated_voice" or "record_provider_closure" => [OwnerBudgetId, FundingId],
                "set_owner_admission" => OwnerBudgetId == Guid.Empty ? [] : [OwnerBudgetId],
                "approve_budget_ceilings" or "set_budget_admission" => [budgetId!.Value],
                _ => []
            };
            var accounts = await db.Set<ReceptionistBudgetAccount>().AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync();
            var reference = "synthetic-reviewed-" + Guid.NewGuid().ToString("N");
            return new()
            {
                Version = 1, Label = "Synthetic operator review", Operation = operation, ReviewedByFounderOid = FounderOid,
                ReviewedUtc = Clock.Now, ExpiresUtc = Clock.Now.AddHours(1), MarketingConnectionId = connection.Id,
                ExpectedOwnerKey = Owner.Key, Provider = "openai", ProviderProjectKey = Project, ConfigurationId = version.Id,
                ConfigurationSha256 = version.ConfigurationSha256, ExpectedConnectionRevision = connection.Revision,
                ExpectedAccountRevisions = accounts.ToDictionary(x => x.Id, x => x.Revision),
                FundingScopes = accounts.Where(x => x.Kind == "funding").ToDictionary(x => x.Id, x => x.ScopeKey),
                ReceiptReference = reference, Evidence = [new(operation, reference, ReceptionistConfigurationCodec.Hash(reference), Clock.Now)],
                CallId = callId, ExpectedCallRevision = call?.Revision, BudgetAccountId = budgetId
            };
        }

        internal async Task<ReceptionistOperatorPackage> BindingPackageAsync(string api = ApiSecret, string webhook = WebhookSecret)
        {
            Secrets(api, webhook);
            var package = await ForAsync("accept_binding");
            var binding = new ReceptionistProviderBinding
            {
                IngressKey = Ingress, ProviderProjectKey = Project, Model = "gpt-live-1", Voice = "marin",
                BackendMode = "application_services", Transport = "sip", CredentialReference = "test_api", WebhookSecretReference = "test_webhook",
                ProviderVerificationReceipt = "synthetic-private-provider-receipt", ExclusiveProjectVerificationReceipt = "synthetic-exclusive-project-receipt",
                CostVerificationReceipt = "synthetic-cost-evidence", DisclosureConfigurationReceipt = "synthetic-disclosure-evidence",
                RetentionConfigurationReceipt = "synthetic-retention-evidence", CarrierFallbackReceipt = "synthetic-fallback-evidence",
                MaximumSupportedCallSeconds = 120, ControlledTestOnly = true,
                Costs = [new("provider_cost", FundingId, 2m, 0m, 3m, 0m)],
                Capabilities = [new(ReceptionistCapabilities.CanonicalIntake, "leads.confirmed_inquiry.v1", ReceptionistCapabilityStates.Accepted, "synthetic-intake-evidence")]
            };
            var evidence = new[]
            {
                ("provider_setup", binding.ProviderVerificationReceipt), ("exclusive_project", binding.ExclusiveProjectVerificationReceipt),
                ("cost_bounds", binding.CostVerificationReceipt), ("disclosure", binding.DisclosureConfigurationReceipt),
                ("retention", binding.RetentionConfigurationReceipt), ("carrier_fallback", binding.CarrierFallbackReceipt),
                ("capability." + ReceptionistCapabilities.CanonicalIntake, binding.Capabilities[0].AcceptanceReceipt)
            }.Select(x => new ReceptionistOperatorEvidence(x.Item1, x.Item2, ReceptionistConfigurationCodec.Hash(x.Item2), Clock.Now)).ToArray();
            return package with { Binding = binding, ApiCredentialSha256 = ReceptionistConfigurationCodec.Hash(api),
                WebhookSecretSha256 = ReceptionistConfigurationCodec.Hash(webhook), Evidence = evidence };
        }

        internal (string Key, string Hash) Publish(ReceptionistOperatorPackage package, string? key = null)
        {
            key ??= "synthetic_" + Guid.NewGuid().ToString("N");
            var json = JsonSerializer.Serialize(package, ReceptionistConfigurationCodec.Json); var hash = ReceptionistConfigurationCodec.Hash(json);
            Host["Receptionist:OperatorPackages:" + key + ":Json"] = json; Host["Receptionist:OperatorPackages:" + key + ":Sha256"] = hash;
            return (key, hash);
        }

        internal ReceptionistOperatorApplyRequest Request(ReceptionistOperatorPlan plan) =>
            new(plan.PackageKey, plan.PackageSha256, plan.ExpectedStateToken!);
        internal async Task<ReceptionistOperatorReceipt> ApplyAsync(ReceptionistOperatorPackage package)
        {
            var identity = Publish(package); var plan = await Operator.PlanAsync(identity.Key, identity.Hash);
            Assert.True(plan.CanApply); return await Operator.ApplyAsync(Request(plan));
        }
        internal async Task AcceptAsync() => Assert.True((await ApplyAsync(await BindingPackageAsync())).ReadBackVerified);
        internal async Task EnableOwnerAsync()
        {
            Assert.True((await ApplyAsync((await ForAsync("set_owner_admission")) with { Enabled = true })).ReadBackVerified);
            var binding = (await Admission.GetIngressBindingAsync("openai", Ingress))!;
            await using var db = Database();
            if (!await db.Set<ReceptionistAcceptanceWindow>().AnyAsync(x => x.ConfigurationVersionId == binding.ConfigurationId))
                await ReceptionistTestWindows.SeedAsync(db, binding.MarketingConnectionId, binding.ConfigurationId, binding.Binding, Clock.Now);
        }
        internal TrustedReceptionistIngress IngressFor(ReceptionistIngressBinding binding, string session) =>
            new("openai", Fixture.Ingress, Project, session, "synthetic-event:" + session, true,
                binding.MarketingConnectionId, binding.ConfigurationId, binding.Owner.Key);
        public async ValueTask DisposeAsync()
        { await storeContext.DisposeAsync(); Protector.Dispose(); File.Delete(path); }

        internal static ReceptionistBusinessConfiguration Configuration() => new()
        {
            BusinessName = "Synthetic operator-test owner", ConversationInstructions = "Use synthetic approved instructions.",
            BusinessPolicy = "private-test-business-policy", TimeZoneId = "UTC", Currency = "USD",
            MonthlyCashLimit = 40m, MonthlyConsumptionLimit = 80m, MonthlyVoiceSecondsLimit = 10000,
            MaximumConcurrentCalls = 2, MaximumCallSeconds = 120, AdmissionWindowSeconds = 60, MaximumCallsPerWindow = 20,
            Hours = Enum.GetValues<DayOfWeek>().Select(day => new ReceptionistHoursWindow(day, TimeOnly.MinValue, new TimeOnly(23, 59, 59))).ToArray(),
            RequestedCapabilities = [ReceptionistCapabilities.CanonicalIntake],
            ServiceDefinitionsJson = "[{\"serviceKey\":\"website_services\",\"displayName\":\"Website services\",\"aliases\":[\"website\"],\"discoveryQuestion\":\"What do you need?\",\"publicSummary\":\"Synthetic information.\"}]",
            ApprovedKnowledgeJson = "{\"answers\":[]}",
            IntakeRulesJson = "{\"canonicalInquiry\":{\"services\":[{\"serviceKey\":\"website_services\",\"crmBucket\":\"ServiceInquiry\"}],\"notifyOwner\":false}}"
        };
    }

    // The production accessor is AsyncLocal. This request fixture is explicitly retained across
    // asynchronous fixture creation; FounderAuthority and all service authorization remain real.
    private sealed class TestHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class CommandProbe : DbCommandInterceptor
    {
        internal int Writes { get; private set; }
        internal bool RefuseReads { get; set; }
        internal Action? BeforeNextUpdate { get; set; }
        private void Observe(DbCommand command)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.Ordinal) && RefuseReads) throw new TimeoutException("Synthetic readback outage.");
            if (command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.Ordinal))
            {
                var action = BeforeNextUpdate; BeforeNextUpdate = null; action?.Invoke();
            }
            if (!command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.Ordinal)) Writes++;
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Observe(command); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Observe(command); return ValueTask.FromResult(result); }
    }

    private sealed class TransactionProbe : DbTransactionInterceptor
    {
        internal bool FailAfterNextCommit { get; set; }
        internal bool FailBeforeNextCommit { get; set; }
        internal Action? AfterNextCommit { get; set; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (FailBeforeNextCommit) { FailBeforeNextCommit = false; throw new TimeoutException("Synthetic precommit failure."); }
            return ValueTask.FromResult(result);
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            var action = AfterNextCommit; AfterNextCommit = null; action?.Invoke();
            if (FailAfterNextCommit) { FailAfterNextCommit = false; throw new TimeoutException("Synthetic lost commit response."); }
            return Task.CompletedTask;
        }
    }

    private sealed class ReceptionSchemaContext(DbContextOptions options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.ApplyConfiguration(new MarketingConnectionConfiguration()); ReceptionistModelConfiguration.Configure(model, Database.ProviderName);
            foreach (var type in new[] { typeof(AgentTrackingProfile), typeof(CommerceBusiness) })
            {
                var entity = model.Entity(type);
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(x => x.Name != "Id")) entity.Ignore(property.Name);
                entity.HasKey("Id");
            }
            var allowed = new HashSet<Type>(typeof(ReceptionistCall).Assembly.GetTypes().Where(x => x.Namespace == "Domain.Entities" && x.Name.StartsWith("Receptionist", StringComparison.Ordinal)))
            { typeof(MarketingConnection), typeof(AgentTrackingProfile), typeof(CommerceBusiness) };
            foreach (var entity in model.Model.GetEntityTypes().ToArray()) if (!allowed.Contains(entity.ClrType)) model.Ignore(entity.ClrType);
        }
    }
}
