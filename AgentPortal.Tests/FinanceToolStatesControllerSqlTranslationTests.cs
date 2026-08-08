using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Controllers.API;
using AgentPortal.Services;
using AgentPortal.Services.Tracking;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Households;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class FinanceToolStatesControllerSqlTranslationTests
{
    [Fact]
    public void SaveRequest_AllowsMissingClientUserId_ForAgentWorkspace()
    {
        var property = typeof(FinanceToolStatesController.SaveFinanceStateRequest)
            .GetProperty(nameof(FinanceToolStatesController.SaveFinanceStateRequest.ClientUserId));

        Assert.NotNull(property);

        var nullability = new System.Reflection.NullabilityInfoContext()
            .Create(property!);

        Assert.Equal(System.Reflection.NullabilityState.Nullable, nullability.WriteState);

        var request = new FinanceToolStatesController.SaveFinanceStateRequest
        {
            ClientProfileId = Guid.Empty,
            ToolId = "LegendLivingBalanceSheet",
            JsonState = "{}"
        };

        Assert.Null(request.ClientUserId);
    }

    [Fact]
    public void Controller_HasOneDependencyInjectionConstructor()
    {
        var constructor = Assert.Single(typeof(FinanceToolStatesController).GetConstructors());

        Assert.Collection(
            constructor.GetParameters(),
            parameter => Assert.Equal(typeof(MasterAppDbContext), parameter.ParameterType),
            parameter => Assert.Equal(typeof(EffectiveAgentContext), parameter.ParameterType),
            parameter => Assert.Equal(typeof(IHouseholdMembershipService), parameter.ParameterType));
    }

    [Fact]
    public async Task Load_WithClientUserId_UsesASqlTranslatableLookup()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new MasterAppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        const string agentUserId = "client-finance-lookup";
        const string clientUserId = agentUserId;
        var clientProfileId = Guid.NewGuid();
        var householdAccountId = Guid.NewGuid();

        db.ClientProfiles.Add(new ClientProfile
        {
            Id = clientProfileId,
            ClientUserId = clientUserId,
            FirstName = "Client",
            LastName = "Lookup",
            Email = "client.lookup@example.test"
        });
        db.HouseholdAccounts.Add(new HouseholdAccount
        {
            Id = householdAccountId,
            SubscriptionOwnerClientProfileId = clientProfileId
        });
        db.FinanceToolStates.Add(new FinanceToolState
        {
            HouseholdAccountId = householdAccountId,
            ClientProfileId = clientProfileId,
            ToolId = "ProtectionSnapshot",
            JsonState = "{\"complete\":true}"
        });
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("oid", agentUserId) },
                "TestAuth"))
        };
        var agentContext = new EffectiveAgentContext(
            new HttpContextAccessor { HttpContext = http },
            Mock.Of<IAgentTrackingService>(),
            NullLogger<EffectiveAgentContext>.Instance);
        var households = new Mock<IHouseholdMembershipService>();
        households
            .Setup(service => service.ResolveActiveAccessAsync(clientProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HouseholdAccessResolution(
                HasActiveMembership: true,
                HouseholdAccountId: householdAccountId,
                SubscriptionOwnerClientProfileId: clientProfileId,
                Role: null,
                ReasonCode: null));

        var controller = new FinanceToolStatesController(db, agentContext, households.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };

        var result = await controller.Load(Guid.Empty, clientUserId.ToUpperInvariant(), "ProtectionSnapshot");

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
    }
}
