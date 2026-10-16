using System;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Businesses;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Infrastructure.Identity;
using Domain.Accounts;
using Domain.Billing;
using ClientApp.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Threading;
using ClientApp.Services;
using ClientApp.Controllers;
using ClientApp.Controllers.Api;
using Infrastructure.Households;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace AgentPortal.Tests;

public sealed class BusinessOwnerIdentityTests
{
    private static MasterAppDbContext Database() => new(new DbContextOptionsBuilder<MasterAppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).ConfigureWarnings(x => x.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);

    private static ClientProfile Profile(string kind, string email) => new()
    {
        ClientUserId = Guid.NewGuid().ToString(), Email = email, NormalizedEmail = email,
        FirstName = "Personal", LastName = "Owner", CrmNotes = "{\"recordType\":\"" + kind + "\"}"
    };

    [Theory]
    [InlineData("BusinessClient")]
    [InlineData("Lead")]
    public async Task NonPersonalProfileCannotBeLookedUpOrLinked(string kind)
    {
        await using var db = Database();
        var entity = Profile("BusinessClient", "entity@example.test");
        var invalid = Profile(kind, "invalid@example.test");
        var personal = Profile("Client", "personal@example.test");
        db.AddRange(entity, invalid, personal); await db.SaveChangesAsync();
        var service = new CommerceBusinessProvisioningService(db);
        Assert.Null(await service.ResolveOwnerAsync(invalid.Email));
        var request = new CommerceBusinessProvisioningRequest("Entity", "Entity", "BusinessClient", entity.Email,
            OwnerRoleKey: "account", OwnerClientProfileId: entity.Id,
            Owners: new[] { new BusinessOwnerInput(invalid.Id, invalid.Email, 100) });
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(request));
        Assert.Empty(db.CommerceBusinesses);
        var business = await service.CreateAsync(request with { Owners = new[] { new BusinessOwnerInput(personal.Id, personal.Email, 100) } });
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateOwnershipAsync(business.Id, entity.Id, "Entity", request.Owners!));
        Assert.Equal(personal.Id, (await db.CommerceBusinessMembers.SingleAsync(x => x.RoleKey == "owner")).ClientProfileId);
    }

    [Fact]
    public async Task PersonalEmailUpdatePreservesLinkedIdShareAndEntityIdentity()
    {
        await using var db = Database();
        var entity = Profile("BusinessClient", "entity@example.test");
        var owner = Profile("Client", "old@example.test");
        db.AddRange(entity, owner); await db.SaveChangesAsync();
        var service = new CommerceBusinessProvisioningService(db);
        var business = await service.CreateAsync(new("Entity", "Entity", "BusinessClient", entity.Email,
            OwnerRoleKey: "account", OwnerClientProfileId: entity.Id,
            Owners: new[] { new BusinessOwnerInput(owner.Id, owner.Email, 100) }));
        owner.Email = owner.NormalizedEmail = "current@example.test"; await db.SaveChangesAsync();
        await service.UpdateOwnershipAsync(business.Id, entity.Id, "Entity revised",
            new[] { new BusinessOwnerInput(owner.Id, owner.Email, 100) });
        var member = await db.CommerceBusinessMembers.SingleAsync(x => x.RoleKey == "owner");
        Assert.Equal(owner.Id, member.ClientProfileId); Assert.Equal(100m, member.OwnershipPercentage);
        Assert.Equal(owner.Email, member.Email); Assert.Equal("Personal Owner", member.DisplayName);
        Assert.Equal("entity@example.test", entity.Email); Assert.Equal(entity.Email, business.OwnerEmail);
        Assert.Equal("Entity revised", business.DisplayName);
    }
    private static DefaultHttpContext Http(ClientProfile actor, Guid? selected = null)
    {
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] {
            new Claim("oid", actor.ExternalIdentityObjectId ?? actor.ClientUserId), new Claim("preferred_username", actor.Email) }, "Test")) };
        if (selected.HasValue) http.Request.Headers.Cookie = "selfClientProfileId=" + selected;
        return http;
    }

    private static async Task<(ClientProfile Entity, ClientProfile Owner, CommerceBusiness Business)> Seed(MasterAppDbContext db)
    {
        var entity = Profile("BusinessClient", "entity@example.test");
        var owner = Profile("Client", "owner@example.test");
        db.AddRange(entity, owner); await db.SaveChangesAsync();
        var business = await new CommerceBusinessProvisioningService(db).CreateAsync(new("Entity", "Entity", "BusinessClient", entity.Email,
            OwnerRoleKey: "account", OwnerClientProfileId: entity.Id,
            Owners: new[] { new BusinessOwnerInput(owner.Id, owner.Email, 100) }));
        return (entity, owner, business);
    }

    [Fact]
    public async Task OwnerEntersFullEntityContextWithoutChangingActorAndCanExit()
    {
        await using var db = Database(); var f = await Seed(db);
        var http = Http(f.Owner);
        var support = new SupportController(db, new ClientAppReturnUrlNormalizer()) { ControllerContext = new() { HttpContext = http } };
        Assert.IsType<RedirectResult>(await support.ViewAsClient(f.Entity.Id, "/profile"));
        Assert.Contains("selfClientProfileId=" + f.Entity.Id, http.Response.Headers.SetCookie.ToString());
        var selected = Http(f.Owner, f.Entity.Id);
        var context = await new EffectiveClientContextService(db).ResolveAsync(selected.User, selected.Request.Cookies);
        Assert.NotNull(context); Assert.Equal(f.Entity.Id, context.ClientProfileId);
        Assert.Equal(f.Entity.ClientUserId, context.ClientUserId); Assert.Equal(f.Owner.Id, context.BusinessActorProfileId);
        Assert.Equal(f.Owner.Email, context.BusinessActorEmail); Assert.True(context.IsBusinessOwnerView); Assert.False(context.IsAgentView);
        Assert.Equal(f.Business.Id, context.ManagedBusinessId); Assert.Equal(f.Owner.ClientUserId, selected.User.FindFirst("oid")!.Value);
        Assert.IsType<RedirectResult>(support.StopViewAsClient("/profile", clearSelf: true));
        Assert.Contains("selfClientProfileId=;", http.Response.Headers.SetCookie.ToString());
        var self = await new EffectiveClientContextService(db).ResolveAsync(http.User, http.Request.Cookies);
        Assert.Equal(f.Owner.Id, self!.ClientProfileId);
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("inactive-business")]
    [InlineData("team")]
    [InlineData("storefront")]
    [InlineData("missing-account")]
    [InlineData("duplicate-account")]
    [InlineData("foreign")]
    [InlineData("entity-as-actor")]
    [InlineData("owner-paused")]
    [InlineData("account-paused")]
    [InlineData("blank-account-user")]
    public async Task InvalidOwnerSelectionFailsClosedWithoutPersonalFallback(string failure)
    {
        await using var db = Database(); var f = await Seed(db);
        var ownerMember = await db.CommerceBusinessMembers.SingleAsync(x => x.RoleKey == "owner");
        var actor = f.Owner;
        switch (failure)
        {
            case "revoked": ownerMember.Status = "Revoked"; break;
            case "inactive-business": f.Business.IsActive = false; break;
            case "team": ownerMember.CanManageTeam = false; break;
            case "storefront": ownerMember.CanManageStorefront = false; break;
            case "missing-account": db.Remove(await db.CommerceBusinessMembers.SingleAsync(x => x.RoleKey == "account")); break;
            case "duplicate-account": db.Add(new CommerceBusinessMember { CommerceBusinessId = f.Business.Id, ClientProfileId = Guid.NewGuid(), RoleKey = "account", Status = "Active" }); break;
            case "foreign": actor = Profile("Client", f.Owner.Email); db.Add(actor); break;
            case "entity-as-actor": actor = Profile("BusinessClient", f.Owner.Email); db.Add(actor); ownerMember.ClientProfileId = actor.Id; break;
            case "blank-account-user":
                var malformed = Profile("BusinessClient", "malformed@example.test"); malformed.ClientUserId = "";
                db.Add(malformed); (await db.CommerceBusinessMembers.SingleAsync(x => x.RoleKey == "account")).ClientProfileId = malformed.Id;
                f.Entity = malformed; break;
            case "owner-paused": case "account-paused":
                var subject = failure == "owner-paused" ? f.Owner : f.Entity;
                db.Add(new AccountLifecycleRecord { UserId = subject.ClientUserId, ProfileId = subject.Id, ParticipantType = "Client", State = "Paused" }); break;
        }
        await db.SaveChangesAsync(); var http = Http(actor, f.Entity.Id);
        Assert.Null(await new EffectiveClientContextService(db).ResolveAsync(http.User, http.Request.Cookies));
        var support = new SupportController(db, new ClientAppReturnUrlNormalizer()) { ControllerContext = new() { HttpContext = http } };
        var denied = await support.ViewAsClient(f.Entity.Id);
        Assert.True(denied is ForbidResult or NotFoundObjectResult);
    }

    [Fact]
    public async Task FinanceWritesOnlySelectedBusinessHouseholdAndRejectsPersonalScope()
    {
        await using var db = Database(); var f = await Seed(db);
        var personalHousehold = Guid.NewGuid(); var businessHousehold = Guid.NewGuid();
        var households = new Mock<IHouseholdMembershipService>(MockBehavior.Strict);
        households.Setup(x => x.ResolveActiveAccessAsync(f.Entity.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HouseholdAccessResolution(true, businessHousehold, f.Entity.Id, null, null));
        households.Setup(x => x.ResolveActiveAccessAsync(f.Owner.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HouseholdAccessResolution(true, personalHousehold, f.Owner.Id, null, null));
        var http = Http(f.Owner, f.Entity.Id);
        var controller = new FinanceToolStatesController(db, new EffectiveClientContextService(db, households.Object))
            { ControllerContext = new() { HttpContext = http } };
        Assert.IsType<OkObjectResult>(await controller.Save(new() { ClientProfileId = f.Entity.Id, ToolId = "BusinessPlan", JsonState = "{\"value\":1}" }));
        var state = await db.FinanceToolStates.SingleAsync();
        Assert.Equal(businessHousehold, state.HouseholdAccountId); Assert.Equal(f.Entity.Id, state.ClientProfileId);
        Assert.IsType<ForbidResult>(await controller.Save(new() { ClientProfileId = f.Owner.Id, ToolId = "BusinessPlan" }));
        Assert.IsType<ForbidResult>(await controller.Load(f.Owner.Id, null, "BusinessPlan"));
        Assert.IsType<OkObjectResult>(await controller.Load(f.Entity.Id, null, "BusinessPlan"));
        households.Verify(x => x.ResolveActiveAccessAsync(f.Owner.Id, It.IsAny<CancellationToken>()), Times.Never);
        var noHousehold = new FinanceToolStatesController(db, new EffectiveClientContextService(db))
            { ControllerContext = new() { HttpContext = http } };
        Assert.IsType<ForbidResult>(await noHousehold.Save(new() { ClientProfileId = f.Entity.Id, ToolId = "BusinessPlan" }));
    }

    [Theory]
    [InlineData("BusinessClient", false)]
    [InlineData("Client", true)]
    public void EntityCreationDoesNotRequireOwnersPersonalNames(string kind, bool requiresNames)
    {
        var model = new AgentPortal.Models.CreateClientViewModel { RecordType = kind, EntityName = "Entity",
            Email = "entity@example.test", AccountManagementMode = "SharedAccount",
            BusinessOwners = new() { new(Guid.NewGuid(), "owner@example.test", 100) } };
        var errors = model.Validate(new ValidationContext(model)).SelectMany(x => x.MemberNames).ToArray();
        Assert.Equal(requiresNames, errors.Contains("FirstName")); Assert.Equal(requiresNames, errors.Contains("LastName"));
    }

    private static ProfileController ProfileController(MasterAppDbContext db, DefaultHttpContext http)
    {
        var lifecycle = new Mock<IAccountLifecycleService>();
        lifecycle.Setup(x => x.GetAsync(It.IsAny<AccountLifecycleSubject>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountLifecycleSnapshot("Active", true, false, null, null, null));
        return new ProfileController(db, new EffectiveClientContextService(db), Mock.Of<IClientEntraLifecycleService>(),
            Mock.Of<IClientSubscriptionIdentitySyncService>(), lifecycle.Object,
            new ClientIdentityContinuationService(db, new EphemeralDataProtectionProvider(), new ClientAppReturnUrlNormalizer()),
            new CommerceBusinessProvisioningService(db), new ConfigurationBuilder().Build())
        { ControllerContext = new() { HttpContext = http }, TempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>()) };
    }

    [Fact]
    public async Task OwnerFormUsesCurrentPersonalAndEntityEmailsAndPreservesMembership()
    {
        await using var db = Database(); var f = await Seed(db);
        f.Owner.Email = f.Owner.NormalizedEmail = "current-owner@example.test";
        f.Owner.FirstName = "Current"; f.Entity.Email = f.Entity.NormalizedEmail = "current-entity@example.test";
        await db.SaveChangesAsync();
        var controller = ProfileController(db, Http(f.Owner, f.Entity.Id));
        Assert.IsType<ViewResult>(await controller.BusinessOwners(f.Business.Id));
        var accounts = Assert.IsAssignableFrom<IReadOnlyDictionary<Guid, BusinessOwnerAccount>>(controller.ViewData["OwnerAccounts"]);
        Assert.Equal(f.Owner.Email, accounts[f.Owner.Id].Email); Assert.Equal("Current", accounts[f.Owner.Id].FirstName);
        Assert.Equal(f.Entity.Email, controller.ViewData["BusinessProfileEmail"]);
        var member = await db.CommerceBusinessMembers.SingleAsync(x => x.RoleKey == "owner");
        Assert.Equal("owner@example.test", member.Email); Assert.Equal(100m, member.OwnershipPercentage);
        Assert.IsType<RedirectToActionResult>(await controller.SaveBusinessOwners(f.Business.Id, "Renamed entity", new() { new(f.Owner.Id, f.Owner.Email, 100) }));
        Assert.Equal(f.Owner.Email, member.Email); Assert.Equal("current-entity@example.test", f.Entity.Email);
        Assert.Equal("Current", f.Owner.FirstName);
        Assert.IsType<JsonResult>(await controller.BusinessWebsiteSession(f.Business.Id));
    }

    [Fact]
    public async Task SelectedAccountCannotMutateAnotherOwnedBusinessOrRemoveActualActor()
    {
        await using var db = Database(); var f = await Seed(db);
        var second = Profile("BusinessClient", "second@example.test"); db.Add(second); await db.SaveChangesAsync();
        var other = await new CommerceBusinessProvisioningService(db).CreateAsync(new("Second", "Second", "BusinessClient", second.Email,
            OwnerRoleKey: "account", OwnerClientProfileId: second.Id, Owners: new[] { new BusinessOwnerInput(f.Owner.Id, f.Owner.Email, 100) }));
        var controller = ProfileController(db, Http(f.Owner, f.Entity.Id));
        Assert.IsType<ForbidResult>(await controller.SaveBusinessEntityName(other.Id, "Forbidden"));
        Assert.IsType<ForbidResult>(await controller.BusinessOwners(other.Id));
        Assert.IsType<ForbidResult>(await controller.BusinessWebsiteSession(other.Id));
        var replacement = Profile("Client", "replacement@example.test"); db.Add(replacement); await db.SaveChangesAsync();
        await controller.SaveBusinessOwners(f.Business.Id, "Entity", new() { new(replacement.Id, replacement.Email, 100) });
        Assert.Contains("Keep your personal account linked", controller.TempData["BusinessWebsiteWarning"]!.ToString());
        Assert.Equal("Active", (await db.CommerceBusinessMembers.SingleAsync(x => x.CommerceBusinessId == f.Business.Id && x.RoleKey == "owner")).Status);
        Assert.Equal("Second", other.DisplayName);
        Assert.Null(await BusinessWorkspaceAccess.ResolveAsync(db, f.Business.Id, f.Owner.Id, f.Owner.ClientUserId, f.Owner.Email, "catalog", default));
    }

    [Theory]
    [InlineData(ClientEntitlementStatus.NotGranted, false)]
    [InlineData(ClientEntitlementStatus.Active, true)]
    public async Task OwnerSelectionUsesTargetSubscriptionWithoutAgentBypass(ClientEntitlementStatus status, bool allowed)
    {
        await using var db = Database(); var f = await Seed(db); var http = Http(f.Owner, f.Entity.Id);
        var entitlement = new Mock<IBillingEntitlementService>(MockBehavior.Strict);
        entitlement.Setup(x => x.EvaluateAsync(It.Is<BillingEntitlementEvaluationRequest>(r => r.ClientProfileId == f.Entity.Id), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BillingEntitlementEvaluationResult(status, null, null, null, null, ClientEntitlementSourceType.Subscription, "test", "test"));
        var requirement = new ClientSubscriptionActiveRequirement();
        var context = new AuthorizationHandlerContext(new[] { requirement }, http.User, null);
        await new ClientSubscriptionActiveHandler(new HttpContextAccessor { HttpContext = http }, new EffectiveClientContextService(db), entitlement.Object).HandleAsync(context);
        Assert.Equal(allowed, context.HasSucceeded);
        entitlement.VerifyAll();
        Assert.Null(await new ClientAppMessagingActorContextResolver(new EffectiveClientContextService(db)).ResolveAsync(http));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExternalIdentityPauseCannotBeBypassedByAppProfileIdentity(bool pauseOwner)
    {
        await using var db = Database(); var f = await Seed(db);
        f.Owner.ExternalIdentityObjectId = Guid.NewGuid().ToString();
        f.Entity.ExternalIdentityObjectId = Guid.NewGuid().ToString();
        await db.SaveChangesAsync();
        var http = Http(f.Owner, f.Entity.Id);
        var resolver = new EffectiveClientContextService(db);
        var active = await resolver.ResolveAsync(http.User, http.Request.Cookies);
        Assert.NotNull(active); Assert.Equal(f.Entity.ExternalIdentityObjectId, active.LifecycleSubjectUserId);
        var subject = pauseOwner ? f.Owner : f.Entity;
        db.Add(new AccountLifecycleRecord { UserId = subject.ExternalIdentityObjectId!, ProfileId = subject.Id, ParticipantType = "Client", State = "Paused" });
        await db.SaveChangesAsync();
        Assert.Null(await resolver.ResolveAsync(http.User, http.Request.Cookies));
    }

    [Fact]
    public void NativeCreationContractKeepsPersonalNamesRequiredOnlyForPersonalClients()
    {
        var form = AgentPortal.Models.ClientCreationFormDefinition.Create(new(), false);
        foreach (var name in new[] { "FirstName", "LastName" })
        {
            var condition = Assert.Single(form.Field(name).RequiredWhen);
            Assert.Equal("RecordType", condition.Field); Assert.Equal(new[] { "Client" }, condition.EqualsAny);
        }
    }

    [Fact]
    public async Task ManagedBusinessProfileSaveKeepsOptionalContactsSeparateFromPersonalOwner()
    {
        await using var db = Database(); var f = await Seed(db);
        var controller = ProfileController(db, Http(f.Owner, f.Entity.Id));
        controller.ModelState.AddModelError("FirstName", "First name is required.");
        controller.ModelState.AddModelError("LastName", "Last name is required.");
        Assert.IsType<ViewResult>(await controller.Save(new ClientApp.Models.EditClientViewModel {
            ClientUserId = f.Entity.ClientUserId, FirstName = "", LastName = "", Email = f.Entity.Email,
            Phone = "555-0100", MaritalStatus = "Single" }));
        Assert.True(controller.ModelState.IsValid);
        Assert.Equal("", f.Entity.FirstName); Assert.Equal("Personal", f.Owner.FirstName);
        Assert.Equal("owner@example.test", f.Owner.Email);
    }

    [Fact]
    public async Task BusinessSnapshotDisplaysEntityInReadOnlyManagedMode()
    {
        await using var db = Database(); var f = await Seed(db);
        var controller = new ProtectionSnapshotController(new EffectiveClientContextService(db))
            { ControllerContext = new() { HttpContext = Http(f.Owner, f.Entity.Id) } };
        var view = Assert.IsType<ViewResult>(await controller.Index());
        var model = Assert.IsType<ClientApp.Models.ProtectionSnapshotViewModel>(view.Model);
        Assert.True(model.IsReadOnly); Assert.False(model.IsAgentView); Assert.Equal("Entity", model.ClientDisplayName);
        Assert.Equal(f.Entity.Id, model.ClientProfileId);
    }

    [Theory]
    [InlineData("BusinessClient", false)]
    [InlineData("Lead", false)]
    [InlineData("Client", true)]
    public async Task InvalidHistoricalOwnerProjectionDoesNotFallBackToEntityEmail(string kind, bool blankIdentity)
    {
        await using var db = Database(); var f = await Seed(db);
        var invalid = Profile(kind, "unavailable@example.test");
        if (blankIdentity) invalid.ClientUserId = "";
        db.Add(invalid);
        var member = await db.CommerceBusinessMembers.SingleAsync(x => x.RoleKey == "owner");
        member.ClientProfileId = invalid.Id; member.Email = f.Entity.Email;
        await db.SaveChangesAsync();
        var controller = ProfileController(db, Http(f.Entity));
        Assert.IsType<ViewResult>(await controller.BusinessOwners(f.Business.Id));
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyDictionary<Guid, BusinessOwnerAccount>>(controller.ViewData["OwnerAccounts"]));
        Assert.Equal(f.Entity.Email, controller.ViewData["BusinessProfileEmail"]);
        Assert.Equal(invalid.Id, member.ClientProfileId);
    }

}
