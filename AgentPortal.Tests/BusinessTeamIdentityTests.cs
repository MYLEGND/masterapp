using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Infrastructure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;
using Microsoft.Kiota.Abstractions.Store;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class BusinessTeamIdentityTests
{
    [Theory]
    [InlineData("Accepted", true)]
    [InlineData("PendingAcceptance", true)]
    [InlineData(null, false)]
    [InlineData("Unknown", false)]
    public async Task TeamIdentityReadRequiresExactGuestStateAndNeverAssignsApplication(string? state, bool accepted)
    {
        using var db = ControllerTestHelpers.BuildDb();
        var id = Guid.NewGuid().ToString();
        var adapter = new Mock<IRequestAdapter>(MockBehavior.Strict);
        adapter.SetupProperty(x => x.BaseUrl, "https://graph.microsoft.com/v1.0");
        adapter.Setup(x => x.EnableBackingStore(It.IsAny<IBackingStoreFactory>()));
        adapter.SetupGet(x => x.SerializationWriterFactory).Returns(new Microsoft.Kiota.Serialization.Json.JsonSerializationWriterFactory());
        var user = new User { Id = id, OtherMails = ["team@example.invalid"], UserType = "Guest", ExternalUserState = state };
        adapter.Setup(x => x.SendAsync(It.IsAny<RequestInformation>(), It.IsAny<ParsableFactory<UserCollectionResponse>>(),
            It.IsAny<Dictionary<string, ParsableFactory<IParsable>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestInformation request, ParsableFactory<UserCollectionResponse> _, Dictionary<string, ParsableFactory<IParsable>> __, CancellationToken ___) =>
            {
                Assert.Equal(Method.GET, request.HttpMethod);
                Assert.Contains("top=2", request.URI.ToString());
                return new() { Value = [user] };
            });
        adapter.Setup(x => x.SendAsync(It.IsAny<RequestInformation>(), It.IsAny<ParsableFactory<User>>(),
            It.IsAny<Dictionary<string, ParsableFactory<IParsable>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestInformation request, ParsableFactory<User> _, Dictionary<string, ParsableFactory<IParsable>> __, CancellationToken ___) =>
            {
                Assert.Equal(Method.GET, request.HttpMethod);
                Assert.Contains("externalUserState", request.URI.ToString());
                Assert.Contains("otherMails", request.URI.ToString());
                return user;
            });
        if (state == "PendingAcceptance")
            adapter.Setup(x => x.SendAsync(It.IsAny<RequestInformation>(), It.IsAny<ParsableFactory<Invitation>>(),
                It.IsAny<Dictionary<string, ParsableFactory<IParsable>>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((RequestInformation request, ParsableFactory<Invitation> _, Dictionary<string, ParsableFactory<IParsable>> __, CancellationToken ___) =>
                {
                    Assert.Equal(Method.POST, request.HttpMethod);
                    using var body = System.Text.Json.JsonDocument.Parse(request.Content!);
                    Assert.False(body.RootElement.GetProperty("sendInvitationMessage").GetBoolean());
                    Assert.Equal(id, body.RootElement.GetProperty("invitedUser").GetProperty("id").GetString());
                    return new() { InvitedUser = new User { Id = id }, InviteRedeemUrl = "https://login.microsoftonline.com/redeem?synthetic=true" };
                });
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["GraphProvisioning:TenantId"] = Guid.NewGuid().ToString(),
            ["GraphProvisioning:ClientId"] = Guid.NewGuid().ToString(),
            ["GraphProvisioning:ClientSecret"] = "synthetic-test-only",
            ["GraphProvisioning:ClientApplicationId"] = Guid.NewGuid().ToString()
        }).Build();
        var service = new ClientEntraLifecycleService(db, configuration, NullLogger<ClientEntraLifecycleService>.Instance);
        typeof(ClientEntraLifecycleService).GetField("_graph", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, new GraphServiceClient(adapter.Object));
        if (accepted)
        {
            var result = await service.EnsureBusinessTeamIdentityAsync("Test", "Member", "team@example.invalid");
            Assert.Equal(id, result.ObjectId);
            Assert.False(result.ApplicationAssignmentCreated);
            Assert.Equal(state == "PendingAcceptance", result.RequiresRedemption);
            if (result.RequiresRedemption) Assert.NotNull(result.RedemptionUrl);
        }
        else await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureBusinessTeamIdentityAsync("Test", "Member", "team@example.invalid"));
        Assert.Empty(db.ClientProfiles);
        // Strict adapter has no write, application-assignment or invitation setup.
    }
}
