using System;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Businesses;
using Infrastructure.Data;
using Infrastructure.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace AgentPortal.Tests;
public sealed class ParfaitNativeTeamInvitationTests
{
    [Fact]
    public async Task NativeInvitationUsesExactIdentityCurrentInviterAndOneUseCanonicalReceipt()
    {
        await using var connection=new SqliteConnection("Data Source=:memory:");await connection.OpenAsync();
        await using var db=new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);await db.Database.EnsureCreatedAsync();
        var actor=Guid.NewGuid().ToString();var recipient=Guid.NewGuid().ToString();
        var business=new CommerceBusiness {Key="parfait",IsActive=true,Status="Active"};
        var owner=new ClientProfile {ClientUserId=actor,Email="owner@example.invalid"};
        var invited=new ClientProfile {ClientUserId=recipient,ExternalIdentityObjectId=recipient,Email="member@example.invalid"};
        var ownerMember=new CommerceBusinessMember {CommerceBusinessId=business.Id,ClientProfileId=owner.Id,RoleKey="owner",CanManageTeam=true};
        db.AddRange(business,owner,invited,ownerMember);await db.SaveChangesAsync();
        var workspace=new BusinessWorkspaceService(db,null!,new ConfigurationBuilder().Build());
        var scope=(await workspace.NativeParfaitTeamScopeAsync())!;
        var identity=new Mock<IClientEntraLifecycleService>(MockBehavior.Strict);
        var invitation=await workspace.InviteMemberAsync(null!,scope,actor,"Test","Member",invited.Email,identity.Object,default,
            new(true,true,true,false,false,"[\"/internal/commerce/orders\"]",true));
        var member=await db.CommerceBusinessMembers.SingleAsync(m=>m.ClientProfileId==invited.Id);
        Assert.Equal("Pending",member.Status);Assert.True(member.CanManageOrders);Assert.False(member.CanManageTeam);
        Assert.Null(await workspace.AcceptMemberInvitationAsync(invitation.Token,Guid.NewGuid().ToString(),null!,default));
        Assert.Equal("https://shopparfait.com/internal/login",await workspace.AcceptMemberInvitationAsync(invitation.Token,recipient,null!,default));
        Assert.Null(await workspace.AcceptMemberInvitationAsync(invitation.Token,recipient,null!,default));
        await db.Entry(member).ReloadAsync();Assert.Equal("Active",member.Status);
        // Both contexts read the same revision before either save: the second
        // tracked entity must not overwrite the first save through EF identity caching.
        await using var second = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        var stale = await second.CommerceBusinessMembers.SingleAsync(m => m.Id == member.Id);
        var secondWriter = new BusinessWorkspaceService(second, null!, new ConfigurationBuilder().Build());
        var oldTicks=member.UpdatedUtc.Ticks;
        Assert.True(await workspace.SaveMemberPermissionsAsync(null!,scope,actor,member.Id,oldTicks,new(true,true,false,true,null,"[]",true),default));
        Assert.False(await workspace.SaveMemberPermissionsAsync(null!,scope,actor,member.Id,oldTicks,new(true,true,true,true,null,null,true),default));
        Assert.False(await secondWriter.SaveMemberPermissionsAsync(null!,scope,actor,member.Id,oldTicks,new(true,true,true,true,null,null,true),default));
        await db.Entry(member).ReloadAsync();Assert.Equal("[]",member.ParfaitPageGrantsJson);Assert.False(member.CanManageOrders);
        await second.Entry(stale).ReloadAsync();
        var beforeRevocation = stale.UpdatedUtc.Ticks;
        Assert.True(await workspace.RemoveMemberAsync(null!,scope,actor,member.Id,member.UpdatedUtc.Ticks,default));
        Assert.False(await secondWriter.SaveMemberPermissionsAsync(null!,scope,actor,member.Id,beforeRevocation,new(true,true,true,true,null,null,true,Active:true),default));
        await db.Entry(member).ReloadAsync();Assert.Equal("Revoked",member.Status);
        var replacement=await workspace.InviteMemberAsync(null!,scope,actor,"Test","Member",invited.Email,identity.Object,default);
        ownerMember.Status="Revoked";await db.SaveChangesAsync();
        Assert.Null(await workspace.AcceptMemberInvitationAsync(replacement.Token,recipient,null!,default));
        identity.VerifyNoOtherCalls();
    }
}
