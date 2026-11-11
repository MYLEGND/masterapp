using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using AgentPortal.Services;
using Domain.Entities;
using Infrastructure.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class ClientsGraphConfigurationTests
{
    private static IConfiguration MissingSecret() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
        ["GraphProvisioning:TenantId"] = "synthetic-tenant",
        ["GraphProvisioning:ClientId"] = "synthetic-client",
        ["GraphProvisioning:ClientApplicationId"] = "synthetic-application"
    }).Build();

    [Fact]
    public async Task CrmLookup_WithRealProvisioningServicesAndNoSecret_ReturnsOnlyOwnedClients()
    {
        using var db = ControllerTestHelpers.BuildDb();
        foreach (var (owner, id) in new[] { ("agent-a", "lead:owned"), ("agent-b", "lead:other") })
        {
            db.ClientProfiles.Add(new ClientProfile { ClientUserId = id, FirstName = "Synthetic", LastName = id,
                Email = id.Replace(':', '-') + "@example.invalid", CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow });
            db.AgentClients.Add(new AgentClient { AgentUserId = owner, ClientUserId = id, CreatedUtc = DateTime.UtcNow });
        }
        await db.SaveChangesAsync();
        var configuration = MissingSecret();
        var lifecycle = new ClientEntraLifecycleService(db, configuration, NullLogger<ClientEntraLifecycleService>.Instance);
        var controller = ControllerTestHelpers.BuildClientsController(db, Mock.Of<IExecutionEngine>(), Mock.Of<ICommitmentService>(),
            ControllerTestHelpers.BuildUser("agent-a"), configuration: configuration, clientEntraLifecycle: lifecycle);
        var result = Assert.IsType<JsonResult>(await controller.WorkstationLookupClients(null));
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("lead:owned", json);
        Assert.DoesNotContain("lead:other", json);
    }

    [Fact]
    public async Task IdentityProvisioning_StillRejectsMissingSecretBeforeProviderOrDatabaseWrites()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var lifecycle = new ClientEntraLifecycleService(db, MissingSecret(), NullLogger<ClientEntraLifecycleService>.Instance);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lifecycle.EnsureBusinessTeamIdentityAsync("Synthetic", "Member", "synthetic@example.invalid"));
        Assert.Contains("GraphProvisioning:ClientSecret", error.Message);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task LegacyGraphLookup_StillRejectsMissingSecretBeforeProviderAccess()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var provisioning = new ClientProvisioningService(MissingSecret(), NullLogger<ClientProvisioningService>.Instance,
            db, Mock.Of<IClientEntraLifecycleService>());
        var error = await Assert.ThrowsAsync<Exception>(() => provisioning.DeleteTenantUserByEmailAsync("synthetic@example.invalid"));
        Assert.Contains("Missing ClientSecret", error.Message);
        Assert.Empty(db.ChangeTracker.Entries());
    }
}
