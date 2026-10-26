using System;
using System.Security.Claims;
using System.Threading.Tasks;
using ClientApp.Infrastructure;
using ClientApp.Services;
using Domain.Billing;
using Domain.Entities;
using Infrastructure.Billing;
using Infrastructure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class TrainingBillingAccessBoundaryTests
{
    [Theory]
    [InlineData(false, true, false, false, false, null)] // Historical unmarked session, unknown route.
    [InlineData(true, true, false, false, false, null)]
    [InlineData(true, false, true, false, false, null)] // Removed profile/history cannot erase restriction.
    [InlineData(true, true, true, false, true, null)]
    [InlineData(false, true, true, false, true, null)]
    [InlineData(true, false, false, true, true, null)] // Explicit authentication recovery remains possible.
    [InlineData(true, true, true, false, false, "malformed")]
    [InlineData(true, true, true, false, false, "00000000-0000-4000-8000-000000000001")]
    [InlineData(true, true, false, true, true, "malformed")]
    public async Task ProtectedAndHistoricalSessionsRemainConfined(bool marked, bool live, bool billing, bool auth, bool allowed, string? markerOverride = null)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = new ClientProfile { ExternalIdentityObjectId = "training-oid", Email = "training@example.invalid" };
        if (live)
        {
            db.ClientProfiles.Add(profile);
            var businessId = Guid.NewGuid();
            db.ClientSubscriptions.Add(new ClientSubscription { ClientProfileId = profile.Id,
                CommerceBusinessId = businessId, CommerceProductId = Guid.NewGuid(),
                SubscriptionScope = ClientSubscriptionScope.Training(businessId), ActivatedUtc = DateTime.UtcNow,
                Status = ClientSubscriptionStatus.Active });
            await db.SaveChangesAsync();
        }
        var entitlement = new BillingEntitlementService(db, ControllerTestHelpers.BuildHouseholdMembershipService(db));
        var normalizer = new ClientAppReturnUrlNormalizer();
        var service = new ClientIdentityAccessService(db, entitlement,
            new ClientIdentityContinuationService(db, new EphemeralDataProtectionProvider(), normalizer),
            normalizer, Mock.Of<IClientEntraLifecycleService>());
        var identity = new ClaimsIdentity(new[] { new Claim("oid", "training-oid") }, "test");
        if (marked) identity.AddClaim(new(ClientIdentityAccessService.TrainingBillingProfileClaim, markerOverride ?? profile.Id.ToString("D")));
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        if (billing || auth) context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new TrainingBillingRouteAttribute(auth)), "billing-test"));
        var called = false;
        await new TrainingBillingAccessBoundary(_ => { called = true; return Task.CompletedTask; }).InvokeAsync(context, service);
        Assert.Equal(allowed, called);
        if (!allowed) Assert.Equal(403, context.Response.StatusCode);
    }
}
