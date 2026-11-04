using AgentPortal.Services;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Businesses;
using Infrastructure.Analytics;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class BusinessAccountReconciliationTests
{
    [Fact]
    public async Task ExplicitSelectionPreservesSecondaryHistoryAndStandaloneStoreAndSelectsCanonicalNavigation()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var profile = Profile();
        var shop = new CommerceBusiness { Key = "parfait", PrimaryDomain = "shop.example.test" };
        var secondary = new CommerceBusiness { Key = "retained-draft" };
        var selected = Member(profile, shop);
        var retained = Member(profile, secondary);
        var draft = new WebsiteContentState { CommerceBusinessId = secondary.Id, DraftJson = "{\"retained\":true}" };
        var product = new CommerceProduct { CommerceBusinessId = shop.Id, Name = "Existing product", PriceCents = 3500 };
        db.AddRange(profile, shop, secondary, selected, retained, draft, product,
            new CommerceBusinessStorefrontSettings { CommerceBusinessId = shop.Id },
            new CommerceBusinessStorefrontSettings { CommerceBusinessId = secondary.Id });
        await db.SaveChangesAsync();
        var retainedUpdated = retained.UpdatedUtc;
        var service = new CommerceBusinessProvisioningService(db);
        var create = new CommerceBusinessProvisioningRequest("Existing", "Existing", "BusinessClient", profile.Email,
            OwnerRoleKey: "account", OwnerClientProfileId: profile.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(create));
        var request = Request(profile, shop, selected, retained);
        Assert.Equal(shop.Id, (await service.ReconcileExistingAccountAsync(request)).Id);
        Assert.Equal("account", selected.RoleKey);
        Assert.Null(selected.OwnershipPercentage);
        Assert.Equal("owner", retained.RoleKey);
        Assert.Equal("Active", retained.Status);
        Assert.Equal(retainedUpdated, retained.UpdatedUtc);
        Assert.Equal("{\"retained\":true}", draft.DraftJson);
        Assert.Equal("shop.example.test", shop.PrimaryDomain);
        Assert.Equal(shop.Id, product.CommerceBusinessId);
        Assert.Equal(3500, product.PriceCents);
        Assert.Contains("ExistingBusinessAccountReconciliation", shop.OwnershipHistoryJson);
        Assert.Equal(shop.Id, (await service.CreateAsync(create)).Id);
        Assert.Equal(2, await db.CommerceBusinesses.CountAsync());
        var navigation = new BusinessWorkspaceService(db, Mock.Of<IAnalyticsQueryService>());
        var selectedNavigation = Assert.Single(await navigation.NavigationAsync(profile.Id, profile.ClientUserId, null, default));
        Assert.Equal(shop.Id, selectedNavigation.BusinessId);
        Assert.Equal("https://shopparfait.com/internal/login", selectedNavigation.StoreManagementUrl);
        Assert.Equal(secondary.Id, Assert.Single(await navigation.NavigationAsync(profile.Id, profile.ClientUserId, null, default, secondary.Id)).BusinessId);
        // A business account is not a personal owner identity, even after selection.
        Assert.Null(await BusinessWorkspaceAccess.ResolveAccountOwnerAsync(db, profile.Id, profile.ClientUserId));
    }

    [Theory]
    [InlineData("foreign-actor")]
    [InlineData("stale-snapshot")]
    [InlineData("other-account")]
    [InlineData("inactive-business")]
    public async Task ReconciliationRejectsUnauthorizedConflictingOrStaleSelectionWithoutChanges(string failure)
    {
        using var db = ControllerTestHelpers.BuildDb();
        var profile = Profile();
        var shop = new CommerceBusiness { Key = "selected" };
        var member = Member(profile, shop);
        db.AddRange(profile, shop, member);
        await db.SaveChangesAsync();
        var request = Request(profile, shop, member);
        if (failure == "foreign-actor") request = request with { ActorUserId = "unrelated-user" };
        if (failure == "stale-snapshot") member.UpdatedUtc = member.UpdatedUtc.AddSeconds(1);
        if (failure == "inactive-business") shop.IsActive = false;
        if (failure == "other-account") db.Add(new CommerceBusinessMember { CommerceBusinessId = shop.Id, ClientProfileId = Guid.NewGuid(), RoleKey = "account" });
        await db.SaveChangesAsync();
        var error = await Record.ExceptionAsync(() => new CommerceBusinessProvisioningService(db).ReconcileExistingAccountAsync(request));
        Assert.NotNull(error);
        Assert.True(error is UnauthorizedAccessException or InvalidOperationException or DbUpdateConcurrencyException);
        Assert.Equal("owner", member.RoleKey);
        Assert.Null(shop.OwnershipHistoryJson);
        Assert.Equal(1, await db.CommerceBusinesses.CountAsync());
    }

    [Fact]
    public async Task ControllerReviewAndConfirmationDeriveActorAndRejectForeignIdentity()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var profile = Profile();
        var shop = new CommerceBusiness { Key = "reviewed-shop" };
        var member = Member(profile, shop);
        db.AddRange(profile, shop, member);
        await db.SaveChangesAsync();
        var controller = ControllerTestHelpers.BuildClientsController(db,
            Mock.Of<IExecutionEngine>(), Mock.Of<ICommitmentService>(),
            ControllerTestHelpers.BuildUser(profile.ClientUserId, profile.Email));
        controller.HttpContext.RequestServices = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddSingleton<ICommerceBusinessProvisioningService>(new CommerceBusinessProvisioningService(db)).BuildServiceProvider();
        var review = Assert.IsType<Microsoft.AspNetCore.Mvc.ViewResult>(await controller.ReconcileBusinessAccount(profile.Id));
        var model = Assert.IsType<AgentPortal.Models.BusinessAccountReconciliationViewModel>(review.Model);
        Assert.Equal(shop.Id, Assert.Single(model.Choices).BusinessId);
        controller.ControllerContext.HttpContext.User = ControllerTestHelpers.BuildUser("foreign-user", "foreign@example.test");
        Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundResult>(await controller.ReconcileBusinessAccount(profile.Id, shop.Id, model.ExpectedAssociations));
        Assert.Equal("owner", member.RoleKey);
        var post = typeof(AgentPortal.Controllers.ClientsController).GetMethod("ReconcileBusinessAccount", new[] { typeof(Guid), typeof(Guid), typeof(string) })!;
        Assert.NotEmpty(post.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.ValidateAntiForgeryTokenAttribute), true));
    }

    private static ClientProfile Profile() => new() { ClientUserId = "business-account-user", Email = "business@example.test",
        CrmNotes = "{\"recordType\":\"BusinessClient\"}" };
    private static CommerceBusinessMember Member(ClientProfile profile, CommerceBusiness business) => new()
        { CommerceBusinessId = business.Id, ClientProfileId = profile.Id, Email = profile.Email, RoleKey = "owner", CanManageCrm = true };
    private static BusinessAccountReconciliationRequest Request(ClientProfile profile, CommerceBusiness business, params CommerceBusinessMember[] members) =>
        new(business.Id, profile.Id, profile.ClientUserId, members.Select(x => new BusinessAccountAssociationSnapshot(
            x.Id, x.CommerceBusinessId, x.RoleKey, x.Status, x.UpdatedUtc)).ToArray());
}
