using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Models;
using AgentPortal.Services;
using Domain.Billing;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

[CollectionDefinition("Founder subscriber audit", DisableParallelization = true)]
public sealed class FounderSubscriberAuditCollection { }

[Collection("Founder subscriber audit")]
public sealed class FounderSubscriberAuditTests
{
    [Theory]
    [InlineData(-1, "Expired")]
    [InlineData(1, "Invitation Sent")]
    public async Task InvitationExpiryIsDerivedWithoutMutatingHistory(int days, string expected)
    {
        var prior = Environment.GetEnvironmentVariable("FOUNDER_OID");
        var oid = Guid.NewGuid().ToString();
        Environment.SetEnvironmentVariable("FOUNDER_OID", oid);
        try
        {
            await using var db = Database();
            var client = new ClientProfile { ClientUserId = Guid.NewGuid().ToString(), Email = "qa@example.com" };
            var offer = new ClientSubscriptionOffer { ClientProfileId = client.Id, OwnerAgentUserId = "agent", Currency = "USD" };
            var invitation = new SubscriptionActivationInvitation
            {
                ClientProfileId = client.Id, ClientSubscriptionOfferId = offer.Id,
                Status = SubscriptionActivationInvitationStatus.Sent, ExpiresUtc = DateTime.UtcNow.AddDays(days)
            };
            db.AddRange(client, offer, invitation);
            await db.SaveChangesAsync();
            var service = new FounderSubscribersService(db, NullLogger<FounderSubscribersService>.Instance);
            var result = await service.GetPricingGroupAsync(Founder(oid), 0, "USD", new FounderSubscribersQuery(), 1);
            Assert.NotNull(result);
            Assert.Equal(expected, Assert.Single(result!.Subscribers).Status);
            Assert.Equal(SubscriptionActivationInvitationStatus.Sent, invitation.Status);
        }
        finally { Environment.SetEnvironmentVariable("FOUNDER_OID", prior); }
    }

    [Fact]
    public async Task InvalidDestinationDoesNotStartImpersonation()
    {
        var prior = Environment.GetEnvironmentVariable("FOUNDER_OID");
        var oid = Guid.NewGuid().ToString();
        Environment.SetEnvironmentVariable("FOUNDER_OID", oid);
        try
        {
            await using var db = Database();
            var profile = new ClientProfile { ClientUserId = Guid.NewGuid().ToString() };
            db.AddRange(profile, new ClientSubscription { ClientProfileId = profile.Id, OwnerAgentUserId = "agent" });
            await db.SaveChangesAsync();
            var controller = new FounderSubscribersController(
                new FounderSubscribersService(db, NullLogger<FounderSubscribersService>.Instance),
                new FounderImpersonationService(new EphemeralDataProtectionProvider(), db, NullLogger<FounderImpersonationService>.Instance),
                NullLogger<FounderSubscribersController>.Instance)
            { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Founder(oid) } } };
            Assert.IsType<BadRequestObjectResult>(await controller.OpenClientContext(profile.Id, "agent", "invalid", CancellationToken.None));
            Assert.False(controller.Response.Headers.ContainsKey("Set-Cookie"));
        }
        finally { Environment.SetEnvironmentVariable("FOUNDER_OID", prior); }
    }

    private static MasterAppDbContext Database() => new(new DbContextOptionsBuilder<MasterAppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static ClaimsPrincipal Founder(string oid) => new(new ClaimsIdentity(new[] { new Claim("oid", oid) }, "test"));
}
