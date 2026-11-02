using System;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Accounts;
using Domain.Messaging;
using Infrastructure.Businesses;
using Xunit;

namespace AgentPortal.Tests;

public sealed class StoreActorMembershipTests
{
    [Theory]
    [InlineData("analytics", true)]
    [InlineData("crm", true)]
    [InlineData("orders", false)]
    [InlineData("unknown", false)]
    public async Task Stable_actor_uses_exact_permission_without_storefront_requirement(string capability, bool allowed)
    {
        using var db = ControllerTestHelpers.BuildDb();
        var business = new CommerceBusiness { Key = "synthetic-store", IsActive = true, Status = "Active" };
        var profile = new ClientProfile { ClientUserId = "synthetic-actor", ExternalIdentityObjectId = "synthetic-oid" };
        var member = new CommerceBusinessMember { CommerceBusinessId = business.Id, ClientProfileId = profile.Id,
            RoleKey = "staff", CanManageStorefront = false, CanManageOrders = true, CanManageAnalytics = true, CanManageCrm = true };
        db.AddRange(business, profile, member);
        await db.SaveChangesAsync();
        var resolved = await BusinessWorkspaceAccess.ResolveActorMembershipAsync(db, business.Id, "SYNTHETIC-OID", capability);
        Assert.Equal(allowed, resolved is not null);
        Assert.Null(await BusinessWorkspaceAccess.ResolveActorMembershipAsync(db, Guid.NewGuid(), "synthetic-oid", capability));
        Assert.Null(await BusinessWorkspaceAccess.ResolveActorMembershipAsync(db, business.Id, "display@example.invalid", capability, profile.Id));
        member.Status = "Revoked";
        await db.SaveChangesAsync();
        Assert.Null(await BusinessWorkspaceAccess.ResolveActorMembershipAsync(db, business.Id, "synthetic-oid", capability));
    }

    [Fact]
    public async Task Ambiguous_identity_requires_explicit_authorized_profile_and_alias_pause_denies()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var business = new CommerceBusiness { Key = "synthetic-multiple", IsActive = true, Status = "Active" };
        var one = new ClientProfile { ClientUserId = "one", ExternalIdentityObjectId = "shared-oid" };
        var two = new ClientProfile { ClientUserId = "two", ExternalIdentityObjectId = "shared-oid" };
        db.AddRange(business, one, two,
            new CommerceBusinessMember { CommerceBusinessId = business.Id, ClientProfileId = one.Id, RoleKey = "owner" },
            new CommerceBusinessMember { CommerceBusinessId = business.Id, ClientProfileId = two.Id, RoleKey = "owner" });
        await db.SaveChangesAsync();
        Assert.Null(await BusinessWorkspaceAccess.ResolveActorMembershipAsync(db, business.Id, "shared-oid", "settings"));
        Assert.Null(await BusinessWorkspaceAccess.ResolveActorMembershipAsync(db, business.Id, "shared-oid", "settings", one.Id));
        two.ExternalIdentityObjectId = "other-oid";
        await db.SaveChangesAsync();
        Assert.Equal(one.Id, (await BusinessWorkspaceAccess.ResolveActorMembershipAsync(db, business.Id, "shared-oid", "settings", one.Id))!.ClientProfileId);
        db.Add(new AccountLifecycleRecord { UserId = "shared-oid", ParticipantType = MessagingParticipantTypes.Client,
            ProfileId = one.Id, State = AccountLifecycleStates.Paused });
        await db.SaveChangesAsync();
        Assert.Null(await BusinessWorkspaceAccess.ResolveActorMembershipAsync(db, business.Id, "shared-oid", "settings", one.Id));
        Assert.NotNull(await BusinessWorkspaceAccess.ResolveActorMembershipAsync(db, business.Id, "other-oid", "settings", two.Id));
    }
    [Fact]
    public async Task Delegation_requires_exact_link_shared_mode_and_active_agent_lifecycle()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var business = new CommerceBusiness { Key = "synthetic-delegation", IsActive = true, Status = "Active" };
        var client = new ClientProfile { ClientUserId = "synthetic-client", AccountManagementMode = "SharedAccount" };
        var agent = new AgentProfile { AgentUserId = "synthetic-agent", IsActive = true };
        db.AddRange(business, client, agent,
            new CommerceBusinessMember { CommerceBusinessId = business.Id, ClientProfileId = client.Id, RoleKey = "owner" });
        await db.SaveChangesAsync();
        Assert.Null(await BusinessWorkspaceAccess.ResolveActorMembershipAsync(db, business.Id, agent.AgentUserId, "settings", client.Id));
        var link = new AgentClient { AgentUserId = agent.AgentUserId, ClientUserId = client.ClientUserId };
        db.Add(link);
        await db.SaveChangesAsync();
        Assert.NotNull(await BusinessWorkspaceAccess.ResolveActorMembershipAsync(db, business.Id, agent.AgentUserId, "settings", client.Id));
        client.AccountManagementMode = "SelfManaged";
        await db.SaveChangesAsync();
        Assert.Null(await BusinessWorkspaceAccess.ResolveActorMembershipAsync(db, business.Id, agent.AgentUserId, "settings", client.Id));
        client.AccountManagementMode = "SharedAccount";
        db.Add(new AccountLifecycleRecord { UserId = agent.AgentUserId, ParticipantType = MessagingParticipantTypes.Agent,
            ProfileId = agent.Id, State = AccountLifecycleStates.Paused });
        await db.SaveChangesAsync();
        Assert.Null(await BusinessWorkspaceAccess.ResolveActorMembershipAsync(db, business.Id, agent.AgentUserId, "settings", client.Id));
    }

}
