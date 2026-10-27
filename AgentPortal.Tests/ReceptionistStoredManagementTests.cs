using System;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using AgentPortal.Controllers;
using AgentPortal.Services;
using AgentPortal.Services.Tracking;
using Infrastructure.Businesses;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Infrastructure.Data;
using Infrastructure.Reception;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class ReceptionistOperatorTests
{
    private static Task<bool> AllowOwner(MarketingOwnerScope owner, CancellationToken ct) => Task.FromResult(owner == MarketingOwnerScope.Founder);
    private static ReceptionistOperatorService StoredService(Fixture f, IServiceProvider services) =>
        new(f.Store, f.Admission, f.Host, f.Protector, f.Http, f.Clock, services);

    [Fact]
    public async Task StoredCredentialsAreWriteOnlyVersionedAndDoNotActivateOrAlterOperatingSelection()
    {
        await using var f = await Fixture.CreateAsync();
        using var services = new ServiceCollection().AddSingleton(f.Options).BuildServiceProvider();
        var service = StoredService(f, services);
        var before = await service.ManagementAsync(f.Owner, AllowOwner);
        var id = await service.SaveCredentialAsync(f.Owner, new(before.ConnectionRevision, "proj_synthetic", "voice-api", Fixture.ApiSecret), AllowOwner);
        var after = await service.ManagementAsync(f.Owner, AllowOwner);
        Assert.NotEqual(before.ConnectionRevision, after.ConnectionRevision);
        Assert.Equal(id, Assert.Single(after.Credentials).Id);
        Assert.DoesNotContain(Fixture.ApiSecret, JsonSerializer.Serialize(after));
        await using var db = f.Database();
        var row = await db.Set<ReceptionistCredentialVersion>().SingleAsync();
        Assert.NotEqual(Fixture.ApiSecret, row.ProtectedValue);
        Assert.Equal(Fixture.ApiSecret, ReceptionistCredentialEnvelope.Unprotect(f.Protector, f.Owner, "openai", "proj_synthetic", "voice-api", row.ProtectedValue));
        var connection = await db.MarketingConnections.SingleAsync();
        Assert.False(connection.ReceptionistAdmissionEnabled); Assert.Null(connection.ReceptionistActiveConfigurationId);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.SaveCredentialAsync(f.Owner,
            new(before.ConnectionRevision, "proj_synthetic", "voice-api", Fixture.ApiSecret), AllowOwner));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ManagementAsync(MarketingOwnerScope.Agent(Guid.NewGuid()), AllowOwner));
        await service.RevokeCredentialAsync(f.Owner, id, after.ConnectionRevision, AllowOwner);
        Assert.True(Assert.Single((await service.ManagementAsync(f.Owner, AllowOwner)).Credentials).Revoked);
    }

    [Fact]
    public async Task StoredOperationNeedsExactReviewAndTenantReceiptsHideSharedFundingAcrossApplyAndReplay()
    {
        await using var f = await Fixture.CreateAsync();
        using var services = new ServiceCollection().AddSingleton(f.Options).BuildServiceProvider();
        var service = StoredService(f, services);
        var row = await StorePending(f);
        var key = "saved-" + row.Id.ToString("N");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PlanForOwnerAsync(f.Owner, key, row.PayloadSha256, AllowOwner));
        await service.ReviewSavedOperationAsync(row.Id, row.Revision, row.PayloadSha256, true, default);
        // Explicit delegation of this reviewed workspace operation, not Founder identity.
        f.Http.HttpContext!.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", "scoped-synthetic-owner")], "test"));
        var plan = await service.PlanForOwnerAsync(f.Owner, key, row.PayloadSha256, AllowOwner);
        Assert.True(plan.CanApply);
        Assert.DoesNotContain(f.FundingId.ToString(), JsonSerializer.Serialize(plan));
        Assert.DoesNotContain("Current cash committed", JsonSerializer.Serialize(plan));
        var receipt = await service.ApplyForOwnerAsync(f.Owner, f.Request(plan), AllowOwner);
        Assert.True(receipt.ReadBackVerified);
        Assert.DoesNotContain(f.FundingId.ToString(), JsonSerializer.Serialize(receipt));
        var replay = await service.ApplyForOwnerAsync(f.Owner, f.Request(plan), AllowOwner);
        Assert.True(replay.ReadBackVerified);
        Assert.DoesNotContain(f.FundingId.ToString(), JsonSerializer.Serialize(replay));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PlanForOwnerAsync(MarketingOwnerScope.Agent(Guid.NewGuid()), key, row.PayloadSha256,
            (_, _) => Task.FromResult(true)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PlanForOwnerAsync(f.Owner, "host-package", row.PayloadSha256, AllowOwner));
    }

    [Fact]
    public async Task RevocationAfterPreviewPreventsStoredBindingApplication()
    {
        await using var f = await Fixture.CreateAsync();
        using var services = new ServiceCollection().AddSingleton(f.Options).BuildServiceProvider();
        var service = StoredService(f, services);
        var row = await StorePending(f);
        await service.ReviewSavedOperationAsync(row.Id, row.Revision, row.PayloadSha256, true, default);
        var plan = await service.PlanForOwnerAsync(f.Owner, "saved-" + row.Id.ToString("N"), row.PayloadSha256, AllowOwner);
        Assert.True(plan.CanApply);
        var current = await service.ManagementAsync(f.Owner, AllowOwner);
        await service.RevokeCredentialAsync(f.Owner, row.ApiCredentialVersionId!.Value, current.ConnectionRevision, AllowOwner);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyForOwnerAsync(f.Owner, f.Request(plan), AllowOwner));
        await using var db = f.Database();
        Assert.Null((await db.MarketingConnections.SingleAsync()).ReceptionistActiveConfigurationId);
    }

    [Fact]
    public async Task AlteredPayloadAndNonFounderReviewCannotGrantAcceptance()
    {
        await using var f = await Fixture.CreateAsync();
        using var services = new ServiceCollection().AddSingleton(f.Options).BuildServiceProvider();
        var service = StoredService(f, services);
        var row = await StorePending(f);
        f.Http.HttpContext!.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", "not-founder")], "test"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReviewSavedOperationAsync(row.Id, row.Revision, row.PayloadSha256, true, default));
        f.Http.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", Fixture.FounderOid)], "test"));
        await using (var db = f.Database())
        {
            var current = await db.Set<ReceptionistOperationPackage>().SingleAsync(); current.PayloadJson += " "; await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.ReviewSavedOperationAsync(row.Id, row.Revision, row.PayloadSha256, true, default));
    }

    private static async Task<ReceptionistOperationPackage> StoreUnboundAdmission(Fixture f)
    {
        var binding = await f.BindingPackageAsync();
        const string reference = "synthetic-admission-evidence";
        var package = binding with { Operation = "set_owner_admission", Binding = null,
            ApiCredentialSha256 = null, WebhookSecretSha256 = null, Enabled = true,
            ReceiptReference = reference, Evidence = [new("set_owner_admission", reference,
                ReceptionistConfigurationCodec.Hash(reference), f.Clock.Now)],
            ReviewedByFounderOid = "", ReviewedUtc = default };
        var json = JsonSerializer.Serialize(package, ReceptionistConfigurationCodec.Json);
        var row = new ReceptionistOperationPackage { MarketingConnectionId = package.MarketingConnectionId,
            ConfigurationVersionId = package.ConfigurationId, Operation = package.Operation,
            PayloadJson = json, PayloadSha256 = ReceptionistConfigurationCodec.Hash(json),
            CreatedByActorId = Fixture.FounderOid, CreatedUtc = f.Clock.Now, ExpiresUtc = package.ExpiresUtc };
        await using var db = f.Database(); db.Add(row); await db.SaveChangesAsync(); return row;
    }

    private sealed class ScopedReceptionistController(Guid allowedBusiness) : BusinessWorkspaceControllerBase(null!)
    {
        protected override Task<CommerceBusiness?> ResolveBusinessAsync(Guid id, string capability, CancellationToken ct) =>
            Task.FromResult<CommerceBusiness?>(id == allowedBusiness && capability == "settings" ? new CommerceBusiness { Id = id } : null);
    }

    private static IUrlHelper ScopedUrls()
    {
        var url = new Mock<IUrlHelper>();
        url.Setup(x => x.Action(It.IsAny<UrlActionContext>())).Returns((UrlActionContext context) =>
        {
            var values = new RouteValueDictionary(context.Values);
            var scope = values.ContainsKey("businessId") ? "businessId=" + values["businessId"] : "agentProfileId=" + values["agentProfileId"];
            return "/" + context.Action + "?" + scope;
        });
        return url.Object;
    }

    private static async Task AssertAdditionalManagementFailures(Controller controller, Fixture f, ReceptionistOperationPackage row,
        Func<ReceptionistOperationPreparationInput, Task<IActionResult>> prepare,
        Func<string, string, bool, Task<IActionResult>> plan,
        Func<ReceptionistOperatorApplyRequest, Task<IActionResult>> apply,
        Func<Guid, Guid, Task<IActionResult>> withdraw,
        Func<ReceptionistCredentialInput, Task<IActionResult>> credential)
    {
        controller.TempData = new TempDataDictionary(controller.HttpContext, Mock.Of<ITempDataProvider>());
        var originalRevision = Guid.NewGuid();
        var input = new ReceptionistOperationPreparationInput { Operation = "accept_binding", ExpectedConnectionRevision = originalRevision,
            Model = "synthetic-owner-entry", Voice = "synthetic-voice", Evidence = [new() { Kind = "provider_setup", Reference = "synthetic-evidence" }] };
        controller.ModelState.AddModelError("Model", "untrusted binder detail should not be rendered");
        var invalidPreparation = Assert.IsType<ViewResult>(await prepare(input));
        Assert.Equal(400, controller.Response.StatusCode);
        var invalidPage = Assert.IsType<ReceptionistManagementPage>(invalidPreparation.Model);
        Assert.Equal(originalRevision, invalidPage.SubmittedPreparation!.ExpectedConnectionRevision);
        Assert.Equal("synthetic-owner-entry", invalidPage.SubmittedPreparation.Model);
        Assert.Equal(ReceptionistPlanFailure.InvalidPreparation, invalidPreparation.ViewData["ReceptionistPlanFailure"]);
        controller.ModelState.Clear(); controller.Response.StatusCode = 200;
        var stalePreparation = Assert.IsType<ViewResult>(await prepare(input));
        Assert.Equal(409, controller.Response.StatusCode);
        Assert.Equal(originalRevision, Assert.IsType<ReceptionistManagementPage>(stalePreparation.Model).SubmittedPreparation!.ExpectedConnectionRevision);
        Assert.Equal(ReceptionistPlanFailure.StalePreparation, stalePreparation.ViewData["ReceptionistPlanFailure"]);
        controller.Response.StatusCode = 200;
        var malformed = Assert.IsType<ViewResult>(await plan("", "", false));
        Assert.Equal(400, controller.Response.StatusCode);
        Assert.Equal(ReceptionistPlanFailure.ChooseOperation, malformed.ViewData["ReceptionistPlanFailure"]);
        foreach (var hash in new[] { "not-a-hash", new string('A', 64), new string('g', 64), new string('a', 63) })
        {
            foreach (var pending in new[] { true, false })
            {
                controller.Response.StatusCode = 200;
                var badHash = Assert.IsType<ViewResult>(await plan("saved-" + row.Id.ToString("N"), hash, pending));
                Assert.Equal(400, controller.Response.StatusCode);
                Assert.Equal(ReceptionistPlanFailure.ChooseOperation, badHash.ViewData["ReceptionistPlanFailure"]);
            }
        }
        controller.ModelState.AddModelError("PackageKey", "invalid"); controller.Response.StatusCode = 200;
        var invalidApply = Assert.IsType<ViewResult>(await apply(new("", "", "")));
        Assert.Equal(400, controller.Response.StatusCode);
        Assert.Equal(ReceptionistPlanFailure.InvalidApply, invalidApply.ViewData["ReceptionistPlanFailure"]);
        controller.ModelState.Clear(); controller.Response.StatusCode = 200;
        var staleApply = Assert.IsType<ViewResult>(await apply(new("saved-" + row.Id.ToString("N"), row.PayloadSha256, "stale")));
        Assert.Equal(409, controller.Response.StatusCode);
        Assert.Equal(ReceptionistPlanFailure.StaleApply, staleApply.ViewData["ReceptionistPlanFailure"]);
        var current = await f.Operator.ManagementAsync(f.Owner, (_, _) => Task.FromResult(true));
        foreach (var malformedWithdrawal in new[] { (Guid.Empty, current.ConnectionRevision), (Guid.NewGuid(), Guid.Empty), (Guid.Empty, Guid.Empty) })
        {
            controller.Response.StatusCode = 200;
            var invalidWithdrawal = Assert.IsType<ViewResult>(await withdraw(malformedWithdrawal.Item1, malformedWithdrawal.Item2));
            Assert.Equal(400, controller.Response.StatusCode);
            Assert.Equal(ReceptionistPlanFailure.InvalidWithdrawal, invalidWithdrawal.ViewData["ReceptionistPlanFailure"]);
        }
        controller.ModelState.AddModelError("credentialId", "untrusted malformed identifier");
        controller.Response.StatusCode = 200;
        var invalidBoundWithdrawal = Assert.IsType<ViewResult>(await withdraw(Guid.NewGuid(), current.ConnectionRevision));
        Assert.Equal(400, controller.Response.StatusCode);
        Assert.Equal(ReceptionistPlanFailure.InvalidWithdrawal, invalidBoundWithdrawal.ViewData["ReceptionistPlanFailure"]);
        Assert.False(controller.ModelState.IsValid); controller.ModelState.Clear();
        controller.Response.StatusCode = 200;
        var unavailable = Assert.IsType<ViewResult>(await withdraw(Guid.NewGuid(), current.ConnectionRevision));
        Assert.Equal(400, controller.Response.StatusCode);
        Assert.Equal(ReceptionistPlanFailure.InvalidWithdrawal, unavailable.ViewData["ReceptionistPlanFailure"]);
        controller.Response.StatusCode = 200;
        var staleWithdrawal = Assert.IsType<ViewResult>(await withdraw(Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal(409, controller.Response.StatusCode);
        Assert.Equal(ReceptionistPlanFailure.StaleWithdrawal, staleWithdrawal.ViewData["ReceptionistPlanFailure"]);
        controller.Response.StatusCode = 200;
        var staleCredential = Assert.IsType<ViewResult>(await credential(new(Guid.NewGuid(), "proj_synthetic", "voice-api", Fixture.ApiSecret)));
        Assert.Equal(409, controller.Response.StatusCode);
        var draft = Assert.IsType<ReceptionistManagementPage>(staleCredential.Model).SubmittedCredential;
        Assert.NotNull(draft); Assert.NotEqual(current.ConnectionRevision, draft!.ExpectedConnectionRevision);
        Assert.DoesNotContain(Fixture.ApiSecret, JsonSerializer.Serialize(staleCredential.Model));
        controller.ModelState.Clear(); controller.Response.StatusCode = 200;
    }

    private static async Task AssertUnconfirmedOperationContext(Controller controller, Fixture f,
        Func<string, string, Task<IActionResult>> reconcile,
        Func<ReceptionistOperatorApplyRequest, Task<IActionResult>> apply)
    {
        var row = await StorePending(f);
        var key = "saved-" + row.Id.ToString("N");
        var service = controller.HttpContext.RequestServices.GetRequiredService<ReceptionistOperatorService>();
        await service.ReviewSavedOperationAsync(row.Id, row.Revision, row.PayloadSha256, true, default);
        var plan = await service.PlanForOwnerAsync(f.Owner, key, row.PayloadSha256, (_, _) => Task.FromResult(true));
        Assert.True(plan.CanApply);
        f.Transactions.FailBeforeNextCommit = true;
        var unknown = Assert.IsType<ViewResult>(await apply(f.Request(plan)));
        var identity = new ReceptionistOperationReconciliation(key, row.PayloadSha256);
        Assert.Equal(identity, Assert.IsType<ReceptionistManagementPage>(unknown.Model).Reconciliation);
        var inspected = Assert.IsType<ViewResult>(await reconcile(key, row.PayloadSha256));
        Assert.Equal(identity, Assert.IsType<ReceptionistManagementPage>(inspected.Model).Reconciliation);
        // The fixture proved the previous transaction rolled back. Now test a known commit
        // whose readback and summary are both unavailable; this is not a production retry.
        f.Transactions.AfterNextCommit = () => f.Commands.RefuseReads = true;
        ViewResult unreadable;
        try { unreadable = Assert.IsType<ViewResult>(await apply(f.Request(plan))); }
        finally { f.Commands.RefuseReads = false; }
        var unreadablePage = Assert.IsType<ReceptionistManagementPage>(unreadable.Model);
        Assert.Equal(identity, unreadablePage.Reconciliation);
        Assert.Equal(Guid.Empty, unreadablePage.Summary.ConnectionRevision);
        Assert.Empty(unreadablePage.Summary.Credentials); Assert.Empty(unreadablePage.Summary.Operations);
        var writes = f.Commands.Writes;
        var recovered = Assert.IsType<ViewResult>(await reconcile(key, row.PayloadSha256));
        Assert.Equal(identity, Assert.IsType<ReceptionistManagementPage>(recovered.Model).Reconciliation);
        Assert.Equal(writes, f.Commands.Writes); // Inspecting never reapplies the operation.
        f.Clock.Now = row.ExpiresUtc.AddSeconds(1);
        var expired = Assert.IsType<ViewResult>(await reconcile(key, row.PayloadSha256));
        Assert.Equal(409, controller.Response.StatusCode);
        Assert.Equal(identity, Assert.IsType<ReceptionistManagementPage>(expired.Model).Reconciliation);
        Assert.Equal(ReceptionistPlanFailure.UnconfirmedOperation, expired.ViewData["ReceptionistPlanFailure"]);
        await using var db = f.Database();
        Assert.Equal(2, await db.Set<ReceptionistConfigurationVersion>().CountAsync());
        Assert.False((await db.MarketingConnections.SingleAsync()).ReceptionistAdmissionEnabled);
        controller.Response.StatusCode = 200;
    }

    private static async Task AssertSetupAndContentFailures(Controller controller, Fixture f,
        Func<ReceptionistAdmissionInput, Task<IActionResult>> admission,
        Func<Task<IActionResult>> review,
        Func<ReceptionistContentApplyInput, Task<IActionResult>> apply,
        Func<ReceptionistContentRollbackInput, Task<IActionResult>> restore)
    {
        controller.TempData = new TempDataDictionary(controller.HttpContext, Mock.Of<ITempDataProvider>());
        var current = await f.Store.GetReceptionistConfigurationAsync(f.Owner, "openai");
        controller.ModelState.AddModelError("Enabled", "untrusted binder detail");
        var invalidAdmission = Assert.IsType<ViewResult>(await admission(new(current.ConnectionRevision, true)));
        Assert.Equal(400, controller.Response.StatusCode);
        Assert.Equal(ReceptionistPlanFailure.InvalidAdmission, invalidAdmission.ViewData["ReceptionistSetupFailure"]);
        Assert.False(controller.ModelState.IsValid);
        Assert.Equal(new ReceptionistAdmissionInput(current.ConnectionRevision, true), Assert.IsType<ReceptionistSetupPage>(invalidAdmission.Model).SubmittedAdmission);
        controller.ModelState.Clear(); controller.Response.StatusCode = 200;
        var originalAdmission = new ReceptionistAdmissionInput(Guid.NewGuid(), true);
        var staleAdmission = Assert.IsType<ViewResult>(await admission(originalAdmission));
        Assert.Equal(409, controller.Response.StatusCode);
        Assert.Equal(ReceptionistPlanFailure.StaleAdmission, staleAdmission.ViewData["ReceptionistSetupFailure"]);
        Assert.Equal(originalAdmission, Assert.IsType<ReceptionistSetupPage>(staleAdmission.Model).SubmittedAdmission);
        controller.Response.StatusCode = 200;
        var unavailable = Assert.IsType<ViewResult>(await review());
        Assert.Equal(200, controller.Response.StatusCode);
        var blockedPreview = Assert.IsType<ReceptionistContentReviewPage>(unavailable.Model).Preview;
        Assert.NotNull(blockedPreview); Assert.False(blockedPreview!.CanApply);
        Assert.Equal(Guid.Empty, blockedPreview.BaselineId);
        controller.Response.StatusCode = 200;
        var invalidApply = Assert.IsType<ViewResult>(await apply(new(current.ConnectionRevision, "bad", false)));
        Assert.Equal(400, controller.Response.StatusCode);
        var invalidContent = Assert.IsType<ReceptionistContentReviewPage>(invalidApply.Model);
        Assert.Equal(ReceptionistPlanFailure.InvalidContent, invalidContent.Failure);
        Assert.Equal(new ReceptionistContentApplyInput(current.ConnectionRevision, "bad", false), invalidContent.SubmittedApply);
        controller.Response.StatusCode = 200;
        var staleContentInput = new ReceptionistContentApplyInput(Guid.NewGuid(), new string('a', 64), true);
        var staleApply = Assert.IsType<ViewResult>(await apply(staleContentInput));
        Assert.Equal(409, controller.Response.StatusCode);
        var staleContent = Assert.IsType<ReceptionistContentReviewPage>(staleApply.Model);
        Assert.Equal(ReceptionistPlanFailure.StaleContent, staleContent.Failure);
        Assert.Equal(staleContentInput, staleContent.SubmittedApply);
        controller.Response.StatusCode = 200;
        var invalidRestoreInput = new ReceptionistContentRollbackInput(current.ConnectionRevision, Guid.NewGuid(), true);
        controller.ModelState.AddModelError("SourceVersionId", "untrusted binder detail");
        var invalidRestore = Assert.IsType<ViewResult>(await restore(invalidRestoreInput));
        Assert.Equal(400, controller.Response.StatusCode);
        var invalidRecovery = Assert.IsType<ReceptionistContentReviewPage>(invalidRestore.Model);
        Assert.Equal(ReceptionistPlanFailure.InvalidContent, invalidRecovery.Failure);
        Assert.Equal(invalidRestoreInput, invalidRecovery.SubmittedRollback);
        Assert.False(controller.ModelState.IsValid); controller.ModelState.Clear();
        controller.Response.StatusCode = 200;
        var staleRestoreInput = new ReceptionistContentRollbackInput(Guid.NewGuid(), current.ConfigurationId!.Value);
        var staleRestore = Assert.IsType<ViewResult>(await restore(staleRestoreInput));
        Assert.Equal(409, controller.Response.StatusCode);
        var staleRecovery = Assert.IsType<ReceptionistContentReviewPage>(staleRestore.Model);
        Assert.Equal(ReceptionistPlanFailure.StaleContent, staleRecovery.Failure);
        Assert.Equal(staleRestoreInput, staleRecovery.SubmittedRollback);
        await using var db = f.Database();
        Assert.False((await db.MarketingConnections.SingleAsync()).ReceptionistAdmissionEnabled);
        Assert.Null((await db.Set<ReceptionistConfigurationVersion>().SingleAsync()).AcceptedBindingJson);
        controller.Response.StatusCode = 200;
    }

    private static async Task AssertManagementFailuresAndSecretSafety(Controller controller, Fixture f,
        ReceptionistOperationPackage row, Func<Task<IActionResult>> plan, Func<Task<IActionResult>> review,
        Func<Task<IActionResult>> invalidCredential, string scopeLabel, Func<Task<IActionResult>> acceptedPlan)
    {
        controller.TempData = new TempDataDictionary(controller.HttpContext, Mock.Of<ITempDataProvider>());
        var view = Assert.IsType<ViewResult>(await plan());
        Assert.Equal(400, controller.Response.StatusCode);
        Assert.Equal(ReceptionistPlanFailure.CurrentProjectRequired, view.ViewData["ReceptionistPlanFailure"]);
        var page = Assert.IsType<ReceptionistManagementPage>(view.Model);
        Assert.Contains(scopeLabel, page.ReturnUrl);
        Assert.Equal("voice-connection", ReceptionistPlanFailure.CurrentProjectRequired.TargetFragment);
        controller.Response.StatusCode = 200;
        var reviewView = Assert.IsType<ViewResult>(await review());
        Assert.Equal(400, controller.Response.StatusCode);
        Assert.Equal(ReceptionistPlanFailure.CurrentProjectRequired, reviewView.ViewData["ReceptionistPlanFailure"]);
        // Synthetic reviewed row with a now-missing prerequisite: inspectable blocked plan, never permission.
        await using (var write = f.Database())
        {
            var stored = await write.Set<ReceptionistOperationPackage>().SingleAsync(x => x.Id == row.Id);
            stored.ReviewState = "accepted"; stored.ReviewedByActorId = Fixture.FounderOid;
            stored.ReviewedUtc = f.Clock.Now; stored.ReviewedPayloadSha256 = row.PayloadSha256;
            await write.SaveChangesAsync();
        }
        controller.Response.StatusCode = 200;
        var blockedPlan = Assert.IsType<ViewResult>(await acceptedPlan());
        Assert.Equal(400, controller.Response.StatusCode);
        Assert.Equal(ReceptionistPlanFailure.CurrentProjectRequired, blockedPlan.ViewData["ReceptionistPlanFailure"]);
        var blockedReview = Assert.IsType<ReceptionistManagementPage>(blockedPlan.Model).Review;
        Assert.NotNull(blockedReview); Assert.False(blockedReview!.CanApply); Assert.Null(blockedReview.ExpectedStateToken);
        await using (var write = f.Database())
        {
            var stored = await write.Set<ReceptionistOperationPackage>().SingleAsync(x => x.Id == row.Id);
            stored.ReviewState = "pending"; stored.ReviewedByActorId = null; stored.ReviewedUtc = null; stored.ReviewedPayloadSha256 = null;
            await write.SaveChangesAsync();
        }
        controller.Response.StatusCode = 200;
        controller.ModelState.SetModelValue("Secret", new ValueProviderResult("synthetic-secret-never-redisplay"));
        controller.ModelState.SetModelValue("input.Secret", new ValueProviderResult("synthetic-secret-never-redisplay"));
        controller.ModelState.SetModelValue("input.SECRET", new ValueProviderResult("synthetic-secret-never-redisplay"));
        var invalid = Assert.IsType<ViewResult>(await invalidCredential());
        Assert.Equal(400, controller.Response.StatusCode);
        var credentialPage = Assert.IsType<ReceptionistManagementPage>(invalid.Model);
        Assert.NotNull(credentialPage.SubmittedCredential);
        Assert.Equal("bad-project", credentialPage.SubmittedCredential!.ProviderProjectKey);
        Assert.DoesNotContain("synthetic-secret-never-redisplay", JsonSerializer.Serialize(credentialPage));
        Assert.DoesNotContain(controller.ModelState.Keys, key => key.Equals("Secret", StringComparison.OrdinalIgnoreCase) || key.EndsWith(".Secret", StringComparison.OrdinalIgnoreCase));
        f.Clock.Now = row.ExpiresUtc.AddSeconds(1);
        controller.Response.StatusCode = 200;
        var stale = Assert.IsType<ViewResult>(await plan());
        Assert.Equal(409, controller.Response.StatusCode);
        Assert.Equal(ReceptionistPlanFailure.StaleOrUnavailable, stale.ViewData["ReceptionistPlanFailure"]);
        Assert.Equal("operation-reviews", ReceptionistPlanFailure.StaleOrUnavailable.TargetFragment);
        await using var db = f.Database();
        Assert.False((await db.MarketingConnections.SingleAsync()).ReceptionistAdmissionEnabled);
        Assert.Null((await db.Set<ReceptionistConfigurationVersion>().SingleAsync()).AcceptedBindingJson);
    }

    [Fact]
    public async Task BusinessControllerReturnsScopedPrerequisiteAndStaleReviewsWithoutSecretRedisplay()
    {
        var businessId = Guid.NewGuid();
        await using var f = await Fixture.CreateAsync(owner: MarketingOwnerScope.Business(businessId));
        var row = await StoreUnboundAdmission(f);
        using var services = new ServiceCollection().AddSingleton(f.Options)
            .AddSingleton<ReceptionistOperatorService>(sp => StoredService(f, sp))
            .AddSingleton<Infrastructure.Analytics.MarketingConnectionStore>(f.Store)
            .AddSingleton<ReceptionistAdmissionService>(f.Admission).BuildServiceProvider();
        f.Http.HttpContext!.RequestServices = services;
        var controller = new ScopedReceptionistController(businessId) { ControllerContext = new ControllerContext { HttpContext = f.Http.HttpContext }, Url = ScopedUrls() };
        var key = "saved-" + row.Id.ToString("N");
        await AssertSetupAndContentFailures(controller, f,
            input => controller.ReceptionistAdmission(businessId, input),
            () => controller.ReviewReceptionistContent(businessId),
            input => controller.ApplyReceptionistContent(input, businessId),
            input => controller.RestoreReceptionistContent(input, businessId));
        await AssertAdditionalManagementFailures(controller, f, row,
            input => controller.PrepareReceptionistOperation(input, businessId),
            (k, h, pending) => controller.PlanReceptionistOperation(k, h, pending, businessId),
            input => controller.ApplyReceptionistOperation(input, businessId),
            (id, revision) => controller.RevokeReceptionistCredential(id, revision, businessId),
            input => controller.SaveReceptionistCredential(input, businessId));
        await AssertManagementFailuresAndSecretSafety(controller, f, row,
            () => controller.PlanReceptionistOperation(key, row.PayloadSha256, true, businessId),
            () => controller.ReviewReceptionistOperation(row.Id, row.Revision, row.PayloadSha256, true, businessId),
            () => controller.SaveReceptionistCredential(new(row.Id, "bad-project", "voice-api", "synthetic-secret-never-redisplay"), businessId),
            "businessId=" + businessId,
            () => controller.PlanReceptionistOperation(key, row.PayloadSha256, false, businessId));
        await AssertUnconfirmedOperationContext(controller, f,
            (k, hash) => controller.PlanReceptionistOperation(k, hash, false, businessId, reconcile: true),
            input => controller.ApplyReceptionistOperation(input, businessId));
        f.Commands.RefuseReads = true;
        try
        {
            var denied = Guid.NewGuid();
            Assert.IsType<ForbidResult>(await controller.PlanReceptionistOperation(key, row.PayloadSha256, true, denied));
            Assert.IsType<ForbidResult>(await controller.SaveReceptionistCredential(new(Guid.Empty, "", "", "synthetic"), denied));
            Assert.IsType<ForbidResult>(await controller.RevokeReceptionistCredential(Guid.NewGuid(), Guid.NewGuid(), denied));
            controller.ModelState.AddModelError("credentialId", "malformed must not bypass denied scope");
            Assert.IsType<ForbidResult>(await controller.RevokeReceptionistCredential(Guid.Empty, Guid.Empty, denied));
            controller.ModelState.Clear();
            Assert.IsType<ForbidResult>(await controller.ApplyReceptionistContent(new(Guid.NewGuid(), "bad", false), denied));
        }
        finally { f.Commands.RefuseReads = false; }
    }

    [Fact]
    public async Task OwnerControllerReturnsScopedPrerequisiteAndStaleReviewsWithoutSecretRedisplay()
    {
        await using var f = await Fixture.CreateAsync();
        var row = await StoreUnboundAdmission(f);
        const string upn = "founder@example.invalid"; f.Host["Founder:Upn"] = upn;
        var profile = new AgentTrackingProfile { AgentUserId = Fixture.FounderOid, AgentUpn = upn, Slug = "synthetic-founder" };
        var ownerOptions = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseInMemoryDatabase("receptionist-owner-" + Guid.NewGuid()).Options;
        await using var db = new MasterAppDbContext(ownerOptions);
        db.AgentTrackingProfiles.Add(profile); await db.SaveChangesAsync();
        var tracking = new Mock<IAgentTrackingService>();
        tracking.Setup(x => x.GetByUserIdAsync(Fixture.FounderOid, It.IsAny<CancellationToken>())).ReturnsAsync(profile);
        var effective = new EffectiveAgentContext(f.Http, tracking.Object, NullLogger<EffectiveAgentContext>.Instance);
        using var services = new ServiceCollection().AddSingleton(f.Options)
            .AddSingleton<ReceptionistOperatorService>(sp => StoredService(f, sp))
            .AddSingleton<Infrastructure.Analytics.MarketingConnectionStore>(f.Store)
            .AddSingleton<ReceptionistAdmissionService>(f.Admission).BuildServiceProvider();
        f.Http.HttpContext!.RequestServices = services;
        var controller = new WebsiteAnalyticsController(analytics: null!, metaAds: null!, metaAdsOAuth: null!, tracking: tracking.Object,
            metaSignalAnalytics: null!, landingRouteDiscovery: null!, aiDataBuilder: null!, visitorConcentrationService: null!,
            kpiDetailBreakdownService: null!, visitorTrustScoringService: null!, incidentMonitor: null!,
            logger: NullLogger<WebsiteAnalyticsController>.Instance, db: db, config: f.Host, effectiveContext: effective, metaCapiCredentialProtector: null!)
        { ControllerContext = new ControllerContext { HttpContext = f.Http.HttpContext }, Url = ScopedUrls() };
        var key = "saved-" + row.Id.ToString("N");
        await AssertSetupAndContentFailures(controller, f,
            input => controller.ReceptionistAdmission(input, profile.Id),
            () => controller.ReviewReceptionistContent(profile.Id),
            input => controller.ApplyReceptionistContent(input, profile.Id),
            input => controller.RestoreReceptionistContent(input, profile.Id));
        await AssertAdditionalManagementFailures(controller, f, row,
            input => controller.PrepareReceptionistOperation(input, profile.Id),
            (k, h, pending) => controller.PlanReceptionistOperation(k, h, pending, profile.Id),
            input => controller.ApplyReceptionistOperation(input, profile.Id),
            (id, revision) => controller.RevokeReceptionistCredential(id, revision, profile.Id),
            input => controller.SaveReceptionistCredential(input, profile.Id));
        await AssertManagementFailuresAndSecretSafety(controller, f, row,
            () => controller.PlanReceptionistOperation(key, row.PayloadSha256, true, profile.Id),
            () => controller.ReviewReceptionistOperation(row.Id, row.Revision, row.PayloadSha256, true, profile.Id),
            () => controller.SaveReceptionistCredential(new(row.Id, "bad-project", "voice-api", "synthetic-secret-never-redisplay"), profile.Id),
            "agentProfileId=" + profile.Id,
            () => controller.PlanReceptionistOperation(key, row.PayloadSha256, false, profile.Id));
        await AssertUnconfirmedOperationContext(controller, f,
            (k, hash) => controller.PlanReceptionistOperation(k, hash, false, profile.Id, reconcile: true),
            input => controller.ApplyReceptionistOperation(input, profile.Id));
        controller.Request.QueryString = new QueryString("?team=true");
        f.Commands.RefuseReads = true;
        try
        {
            Assert.IsType<ForbidResult>(await controller.PlanReceptionistOperation(key, row.PayloadSha256, true, profile.Id));
            controller.ModelState.AddModelError("credentialId", "malformed must not bypass denied scope");
            Assert.IsType<ForbidResult>(await controller.RevokeReceptionistCredential(Guid.Empty, Guid.Empty, profile.Id));
            controller.ModelState.Clear();
        }
        finally { f.Commands.RefuseReads = false; }
    }

    private static async Task<ReceptionistOperationPackage> StorePending(Fixture f)
    {
        var package = await f.BindingPackageAsync();
        await using var db = f.Database();
        ReceptionistCredentialVersion Credential(string purpose, string value) => new()
        {
            MarketingConnectionId = package.MarketingConnectionId, Provider = "openai", ProviderProjectKey = Fixture.Project,
            Purpose = purpose, ProtectedValue = ReceptionistCredentialEnvelope.Protect(f.Protector, f.Owner, "openai", Fixture.Project, purpose, value),
            ValueSha256 = ReceptionistConfigurationCodec.Hash(value), CreatedByActorId = Fixture.FounderOid, CreatedUtc = f.Clock.Now
        };
        var api = Credential("voice-api", Fixture.ApiSecret); var webhook = Credential("voice-webhook", Fixture.WebhookSecret);
        package = package with { ReviewedByFounderOid = "", ReviewedUtc = default,
            Binding = package.Binding! with { CredentialReference = "saved-" + api.Id.ToString("N"), WebhookSecretReference = "saved-" + webhook.Id.ToString("N") } };
        var json = JsonSerializer.Serialize(package, ReceptionistConfigurationCodec.Json);
        var row = new ReceptionistOperationPackage
        {
            MarketingConnectionId = package.MarketingConnectionId, ConfigurationVersionId = package.ConfigurationId, Operation = package.Operation,
            PayloadJson = json, PayloadSha256 = ReceptionistConfigurationCodec.Hash(json), ApiCredentialVersionId = api.Id, WebhookCredentialVersionId = webhook.Id,
            CreatedByActorId = Fixture.FounderOid, CreatedUtc = f.Clock.Now, ExpiresUtc = package.ExpiresUtc
        };
        db.AddRange(api, webhook, row); await db.SaveChangesAsync(); return row;
    }
}
