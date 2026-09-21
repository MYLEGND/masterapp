using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClientApp.Models;
using ClientApp.Services;
using Domain.Accounts;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Identity;
using Infrastructure.Businesses;
using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public class ClientProfileControllerTests
{
    [Fact]
    public async Task Save_PersistsClientManagedProfileFields()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var client = new ClientProfile
        {
            ClientUserId = "client-oid-1",
            ExternalIdentityObjectId = "client-oid-1",
            FirstName = "Zac",
            LastName = "Client",
            Email = "zac.client@example.com",
            NormalizedEmail = "zac.client@example.com",
            Phone = "480-555-0000",
            MaritalStatus = "Single",
            AccountManagementMode = ClientAccountManagementModes.SharedAccount
        };
        db.ClientProfiles.Add(client);
        await db.SaveChangesAsync();

        var entra = new Mock<IClientEntraLifecycleService>();
        entra
            .Setup(service => service.SynchronizeClientIdentityAsync(client.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClientEntraIdentitySynchronizationResult(
                "client-oid-1",
                "zac.client@example.com",
                false));
        var subscriptionSync = new Mock<IClientSubscriptionIdentitySyncService>();
        subscriptionSync
            .Setup(service => service.SynchronizeAfterEmailChangeAsync(
                client.Id,
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClientSubscriptionIdentitySyncResult(false, 0, 0));
        var accountLifecycle = new Mock<IAccountLifecycleService>();
        accountLifecycle
            .Setup(service => service.GetAsync(It.IsAny<AccountLifecycleSubject>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountLifecycleSnapshot("Active", true, false, null, null, null));

        var http = new DefaultHttpContext
        {
            User = ControllerTestHelpers.BuildUser("client-oid-1", "zac.client@example.com")
        };
        var controller = BuildController(
            db,
            entra.Object,
            subscriptionSync.Object,
            accountLifecycle.Object,
            http);

        var result = await controller.Save(new EditClientViewModel
        {
            ClientUserId = "client-oid-1",
            FirstName = "Zacary",
            LastName = "Owen",
            Email = "zac.client@example.com",
            Phone = "480-555-0111",
            MaritalStatus = "Single",
            AccountManagementMode = ClientAccountManagementModes.SelfManaged
        });

        Assert.IsType<ViewResult>(result);
        var updated = await db.ClientProfiles.FindAsync(client.Id);
        Assert.NotNull(updated);
        Assert.Equal("Zacary", updated!.FirstName);
        Assert.Equal("Owen", updated.LastName);
        Assert.Equal("480-555-0111", updated.Phone);
        Assert.Equal(ClientAccountManagementModes.SelfManaged, updated.AccountManagementMode);
        entra.Verify(
            service => service.SynchronizeClientIdentityAsync(client.Id, It.IsAny<CancellationToken>()),
            Times.Once);
        subscriptionSync.Verify(
            service => service.SynchronizeAfterEmailChangeAsync(
                client.Id,
                "zac.client@example.com",
                "zac.client@example.com",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task BusinessWebsiteSetup_IsDeniedForOrdinaryClient()
    {
        using var db = ControllerTestHelpers.BuildDb();
        db.ClientProfiles.Add(new ClientProfile
        {
            ClientUserId = "ordinary-client",
            ExternalIdentityObjectId = "ordinary-client",
            FirstName = "Ordinary",
            LastName = "Client",
            Email = "ordinary@example.com",
            NormalizedEmail = "ordinary@example.com",
            CrmNotes = "{\"recordType\":\"Client\",\"pipelineStage\":\"Client\"}"
        });
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext
        {
            User = ControllerTestHelpers.BuildUser("ordinary-client", "ordinary@example.com")
        };
        var controller = BuildController(
            db,
            Mock.Of<IClientEntraLifecycleService>(),
            Mock.Of<IClientSubscriptionIdentitySyncService>(),
            BuildActiveLifecycle(),
            http);

        var result = await controller.SetupBusinessWebsite("Should Not Exist", null);

        Assert.IsType<ForbidResult>(result);
        Assert.Empty(db.CommerceBusinesses);
        Assert.Empty(db.CommerceBusinessMembers);
    }

    [Fact]
    public async Task BusinessWebsiteSetup_UsesExistingBusinessAuthorityForBusinessClient()
    {
        using var db = ControllerTestHelpers.BuildDb();
        db.ClientProfiles.Add(new ClientProfile
        {
            ClientUserId = "business-client",
            ExternalIdentityObjectId = "business-client",
            FirstName = "Business",
            LastName = "Owner",
            Email = "owner@example.com",
            NormalizedEmail = "owner@example.com",
            CrmNotes = "{\"recordType\":\"BusinessClient\",\"pipelineStage\":\"BusinessClient\"}"
        });
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext
        {
            User = ControllerTestHelpers.BuildUser("business-client", "owner@example.com")
        };
        var controller = BuildController(
            db,
            Mock.Of<IClientEntraLifecycleService>(),
            Mock.Of<IClientSubscriptionIdentitySyncService>(),
            BuildActiveLifecycle(),
            http);

        var result = await controller.SetupBusinessWebsite("Smith Plumbing", "Smith Plumbing LLC");

        Assert.IsType<RedirectToActionResult>(result);
        var business = Assert.Single(db.CommerceBusinesses);
        Assert.Equal("Smith Plumbing", business.DisplayName);
        Assert.Equal("Smith Plumbing LLC", business.LegalName);
        Assert.Equal("BusinessClient", business.BusinessType);
        var membership = Assert.Single(db.CommerceBusinessMembers);
        Assert.Equal(business.Id, membership.CommerceBusinessId);
        Assert.Equal("OWNER@EXAMPLE.COM", membership.NormalizedEmail);
        Assert.Equal(Assert.Single(db.ClientProfiles).Id, membership.ClientProfileId);
        Assert.True(membership.CanManageStorefront);
        Assert.False(membership.CanManageCatalog);
        Assert.False(membership.CanManageOrders);
        var storefront = Assert.Single(db.CommerceBusinessStorefrontSettings);
        Assert.Equal(business.Id, storefront.CommerceBusinessId);
        Assert.Equal("Smith Plumbing", storefront.BrandHeadline);
        Assert.Equal("Draft", storefront.StorefrontStatus);
    }

    [Fact]
    public async Task BusinessWebsiteEdit_DeniesDifferentBusiness()
    {
        using var db = ControllerTestHelpers.BuildDb();
        db.ClientProfiles.Add(new ClientProfile
        {
            ClientUserId = "business-client-a",
            ExternalIdentityObjectId = "business-client-a",
            FirstName = "Owner",
            LastName = "A",
            Email = "owner-a@example.com",
            NormalizedEmail = "owner-a@example.com",
            CrmNotes = "{\"recordType\":\"BusinessClient\",\"pipelineStage\":\"BusinessClient\"}"
        });
        var businessA = new CommerceBusiness
        {
            Key = "business-a",
            DisplayName = "Business A",
            LegalName = "Business A",
            BusinessType = "BusinessClient",
            OwnerEmail = "owner-a@example.com",
            Status = "Active",
            IsActive = true
        };
        var businessB = new CommerceBusiness
        {
            Key = "business-b",
            DisplayName = "Business B",
            LegalName = "Business B",
            BusinessType = "BusinessClient",
            OwnerEmail = "owner-b@example.com",
            Status = "Active",
            IsActive = true
        };
        db.CommerceBusinesses.AddRange(businessA, businessB);
        db.CommerceBusinessMembers.Add(new CommerceBusinessMember
        {
            CommerceBusiness = businessA,
            ClientProfileId = Assert.Single(db.ClientProfiles.Local).Id,
            Email = "owner-a@example.com",
            NormalizedEmail = "OWNER-A@EXAMPLE.COM",
            Status = "Active",
            CanManageStorefront = true
        });
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext
        {
            User = ControllerTestHelpers.BuildUser("business-client-a", "owner-a@example.com")
        };
        var controller = BuildController(
            db,
            Mock.Of<IClientEntraLifecycleService>(),
            Mock.Of<IClientSubscriptionIdentitySyncService>(),
            BuildActiveLifecycle(),
            http);

        Assert.IsType<JsonResult>(await controller.BusinessWebsiteSession(businessA.Id));
        Assert.IsType<ForbidResult>(await controller.BusinessWebsiteSession(businessB.Id));

        var profile = Assert.Single(db.ClientProfiles);
        profile.Email = "owner-b@example.com";
        profile.NormalizedEmail = "owner-b@example.com";
        await db.SaveChangesAsync();

        Assert.Single(db.CommerceBusinessMembers).Status = "Inactive";
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task BusinessWebsiteProfile_UsesVerifiedDomainBindingAsDomainSource()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var client = new ClientProfile
        {
            ClientUserId = "business-domain-owner",
            ExternalIdentityObjectId = "business-domain-owner",
            FirstName = "Domain",
            LastName = "Owner",
            Email = "owner@domain.example",
            NormalizedEmail = "owner@domain.example",
            CrmNotes = "{\"recordType\":\"BusinessClient\",\"pipelineStage\":\"BusinessClient\"}"
        };
        var business = new CommerceBusiness
        {
            Key = "domain-business",
            DisplayName = "Domain Business",
            LegalName = "Domain Business LLC",
            BusinessType = "BusinessClient",
            OwnerEmail = client.Email,
            PrimaryDomain = "legacy.example",
            Status = "Active",
            IsActive = true
        };
        db.ClientProfiles.Add(client);
        db.CommerceBusinesses.Add(business);
        db.CommerceBusinessMembers.Add(new CommerceBusinessMember
        {
            CommerceBusiness = business,
            ClientProfileId = client.Id,
            Email = client.Email,
            NormalizedEmail = client.Email.ToUpperInvariant(),
            Status = "Active",
            RoleKey = "owner",
            CanManageStorefront = true
        });
        db.Set<WebsiteDomainBinding>().Add(new WebsiteDomainBinding
        {
            CommerceBusinessId = business.Id,
            Hostname = "www.domain.example",
            Status = "active",
            CertificateStatus = "active",
            LastCheckedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext
        {
            User = ControllerTestHelpers.BuildUser(client.ExternalIdentityObjectId, client.Email)
        };
        var controller = BuildController(
            db,
            Mock.Of<IClientEntraLifecycleService>(),
            Mock.Of<IClientSubscriptionIdentitySyncService>(),
            BuildActiveLifecycle(),
            http);

        Assert.IsType<ViewResult>(await controller.MyProfile());
        var websites = Assert.IsAssignableFrom<IReadOnlyList<BusinessWebsiteProfileSummary>>(controller.ViewData["BusinessWebsites"]);
        Assert.Equal("www.domain.example", Assert.Single(websites).PrimaryDomain);
    }

    [Fact]
    public async Task SharedAccountAgentView_CanSeeEditAndManageLinkedBusinessWebsite()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var client = new ClientProfile
        {
            ClientUserId = "business-client-shared",
            ExternalIdentityObjectId = "business-client-shared",
            FirstName = "Shared",
            LastName = "Business",
            Email = "owner-shared@example.com",
            NormalizedEmail = "owner-shared@example.com",
            AccountManagementMode = ClientAccountManagementModes.SharedAccount,
            CrmNotes = "{\"recordType\":\"BusinessClient\",\"pipelineStage\":\"BusinessClient\"}"
        };
        var business = new CommerceBusiness
        {
            Key = "shared-business",
            DisplayName = "Shared Business",
            LegalName = "Shared Business LLC",
            BusinessType = "BusinessClient",
            OwnerEmail = client.Email,
            Status = "Active",
            IsActive = true
        };
        db.ClientProfiles.Add(client);
        db.AgentClients.Add(new AgentClient
        {
            AgentUserId = "agent-oid-shared",
            AgentUpn = "agent@mylegnd.com",
            ClientUserId = client.ClientUserId
        });
        db.CommerceBusinesses.Add(business);
        db.CommerceBusinessMembers.Add(new CommerceBusinessMember
        {
            CommerceBusiness = business,
            ClientProfileId = client.Id,
            Email = client.Email,
            NormalizedEmail = "OWNER-SHARED@EXAMPLE.COM",
            Status = "Active",
            RoleKey = "owner",
            CanManageStorefront = true
        });
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext
        {
            User = ControllerTestHelpers.BuildUser("agent-oid-shared", "agent@mylegnd.com")
        };
        http.Request.Headers.Cookie = $"impClientProfileId={client.Id:D}";

        var controller = BuildController(
            db,
            Mock.Of<IClientEntraLifecycleService>(),
            Mock.Of<IClientSubscriptionIdentitySyncService>(),
            BuildActiveLifecycle(),
            http);

        var profileResult = Assert.IsType<ViewResult>(await controller.MyProfile());
        Assert.Equal("Index", profileResult.ViewName);
        Assert.True(Assert.IsType<bool>(controller.ViewData["IsBusinessClient"]));
        var websites = Assert.IsAssignableFrom<IReadOnlyList<BusinessWebsiteProfileSummary>>(controller.ViewData["BusinessWebsites"]);
        var website = Assert.Single(websites);
        Assert.Equal(business.Id, website.BusinessId);

        Assert.IsType<JsonResult>(await controller.BusinessWebsiteSession(business.Id));
    }

    [Fact]
    public async Task SharedAccountAgentWebsiteAccess_RevokesWhenClientBecomesSelfManaged()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var client = new ClientProfile
        {
            ClientUserId = "business-client-private",
            ExternalIdentityObjectId = "business-client-private",
            FirstName = "Private",
            LastName = "Business",
            Email = "owner-private@example.com",
            NormalizedEmail = "owner-private@example.com",
            AccountManagementMode = ClientAccountManagementModes.SharedAccount,
            CrmNotes = "{\"recordType\":\"BusinessClient\",\"pipelineStage\":\"BusinessClient\"}"
        };
        var business = new CommerceBusiness
        {
            Key = "private-business",
            DisplayName = "Private Business",
            LegalName = "Private Business LLC",
            BusinessType = "BusinessClient",
            OwnerEmail = client.Email,
            Status = "Active",
            IsActive = true
        };
        db.ClientProfiles.Add(client);
        db.AgentClients.Add(new AgentClient
        {
            AgentUserId = "agent-oid-private",
            AgentUpn = "agent-private@mylegnd.com",
            ClientUserId = client.ClientUserId
        });
        db.CommerceBusinesses.Add(business);
        db.CommerceBusinessMembers.Add(new CommerceBusinessMember
        {
            CommerceBusiness = business,
            ClientProfileId = client.Id,
            Email = client.Email,
            NormalizedEmail = "OWNER-PRIVATE@EXAMPLE.COM",
            Status = "Active",
            RoleKey = "owner",
            CanManageStorefront = true
        });
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext
        {
            User = ControllerTestHelpers.BuildUser("agent-oid-private", "agent-private@mylegnd.com")
        };
        http.Request.Headers.Cookie = $"impClientProfileId={client.Id:D}";
        var controller = BuildController(
            db,
            Mock.Of<IClientEntraLifecycleService>(),
            Mock.Of<IClientSubscriptionIdentitySyncService>(),
            BuildActiveLifecycle(),
            http);

        Assert.IsType<JsonResult>(await controller.BusinessWebsiteSession(business.Id));

        client.AccountManagementMode = ClientAccountManagementModes.SelfManaged;
        await db.SaveChangesAsync();

        Assert.IsType<ForbidResult>(await controller.BusinessWebsiteSession(business.Id));
    }

    private static IAccountLifecycleService BuildActiveLifecycle()
    {
        var lifecycle = new Mock<IAccountLifecycleService>();
        lifecycle
            .Setup(service => service.GetAsync(It.IsAny<AccountLifecycleSubject>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountLifecycleSnapshot("Active", true, false, null, null, null));
        return lifecycle.Object;
    }

    private static ClientApp.Controllers.ProfileController BuildController(
        Infrastructure.Data.MasterAppDbContext db,
        IClientEntraLifecycleService entra,
        IClientSubscriptionIdentitySyncService subscriptionSync,
        IAccountLifecycleService lifecycle,
        DefaultHttpContext http)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LegendWebsiteBaseUrl"] = "https://www.example.test"
            })
            .Build();

        var keyPath = Path.Combine(Path.GetTempPath(), "client-profile-controller-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(keyPath);
        var continuation = new ClientIdentityContinuationService(
            db,
            DataProtectionProvider.Create(new DirectoryInfo(keyPath)),
            new ClientAppReturnUrlNormalizer());

        return new ClientApp.Controllers.ProfileController(
            db,
            new EffectiveClientContextService(db),
            entra,
            subscriptionSync,
            lifecycle,
            continuation,
            new CommerceBusinessProvisioningService(db),
            configuration)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>())
        };
    }

}
