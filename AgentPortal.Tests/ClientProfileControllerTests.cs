using System.Threading;
using System.Threading.Tasks;
using ClientApp.Models;
using ClientApp.Services;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
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

        var http = new DefaultHttpContext
        {
            User = ControllerTestHelpers.BuildUser("client-oid-1", "zac.client@example.com")
        };
        var controller = new ClientApp.Controllers.ProfileController(
            db,
            new EffectiveClientContextService(db),
            entra.Object,
            subscriptionSync.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>())
        };

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
}
