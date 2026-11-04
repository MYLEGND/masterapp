using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Moq;
using ParfaitApp.Models;
using ParfaitApp.Services;
using Xunit;

namespace AgentPortal.Tests;
public sealed class ParfaitCanonicalTeamAccessTests
{
    [Fact]
    public async Task StableIdentityAndCanonicalTenantOwnAccess_NotEmailOrForeignMembership()
    {
        await using var db=ControllerTestHelpers.BuildDb();
        var oid=Guid.NewGuid().ToString();
        var business=new CommerceBusiness {Key="parfait",Status="Active",IsActive=true};
        var foreign=new CommerceBusiness {Key="other",Status="Active",IsActive=true};
        var profile=new ClientProfile {ClientUserId=oid,Email="same@mylegnd.com"};
        var member=new CommerceBusinessMember {CommerceBusinessId=foreign.Id,ClientProfileId=profile.Id,RoleKey="owner"};
        db.AddRange(business,foreign,profile,member);await db.SaveChangesAsync();
        var service=Service(db);
        Assert.False((await service.AuthorizeSignInAsync(User(oid))).Allowed);
        member.CommerceBusinessId=business.Id;await db.SaveChangesAsync();
        Assert.True((await service.AuthorizeSignInAsync(User(oid))).Allowed);
        Assert.False((await service.AuthorizeSignInAsync(User(Guid.NewGuid().ToString()))).Allowed);
        Assert.False((await service.AuthorizeSignInAsync(new(new ClaimsIdentity(new[]{new Claim(ClaimTypes.Email,"same@mylegnd.com")},"test")))).Allowed);
        member.Status="Revoked";await db.SaveChangesAsync();
        Assert.False(await service.ValidatePrincipalAsync(User(oid)));
    }
    [Theory]
    [InlineData(null,true)]
    [InlineData("[]",false)]
    [InlineData("broken",false)]
    [InlineData("[\"/internal/commerce/orders\"]",true)]
    [InlineData("[\"/internal/analytics\"]",false)]
    public async Task NativeRestrictionsOnlyReduceCanonicalCapabilities(string? grants,bool allowed)
    {
        await using var db=ControllerTestHelpers.BuildDb();
        var oid=Guid.NewGuid().ToString();var business=new CommerceBusiness {Key="parfait",Status="Active",IsActive=true};
        var profile=new ClientProfile {ClientUserId=oid};
        var member=new CommerceBusinessMember {CommerceBusinessId=business.Id,ClientProfileId=profile.Id,RoleKey="staff",CanManageStorefront=true,CanManageOrders=true,CanManageAnalytics=false,ParfaitPageGrantsJson=grants};
        db.AddRange(business,profile,member);await db.SaveChangesAsync();var service=Service(db);
        Assert.Equal(allowed,(await service.AuthorizePageAsync(User(oid),"/internal/commerce/orders")).Allowed);
        Assert.False((await service.AuthorizePageAsync(User(oid),"/internal/analytics")).Allowed);
        Assert.False((await service.AuthorizePageAsync(User(oid),"/internal/unregistered")).Allowed);
        member.CanManageOrders=false;await db.SaveChangesAsync();
        Assert.False((await service.AuthorizePageAsync(User(oid),"/internal/commerce/orders")).Allowed);
    }
    [Fact]
    public async Task MissingProvisioningConfigurationDoesNotBlockExistingOwnerButCannotGrantNewAccess()
    {
        await using var db=ControllerTestHelpers.BuildDb();
        var oid=Guid.NewGuid().ToString();
        var business=new CommerceBusiness {Key="parfait",Status="Active",IsActive=true};
        var profile=new ClientProfile {ClientUserId=oid};
        db.AddRange(business,profile,new CommerceBusinessMember {CommerceBusinessId=business.Id,ClientProfileId=profile.Id,RoleKey="owner",CanManageTeam=true});
        await db.SaveChangesAsync();
        var configuration=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["AzureAd:TenantId"]=Guid.NewGuid().ToString() }).Build();
        var resolved=0;
        var identity=new Lazy<Infrastructure.Identity.IClientEntraLifecycleService>(()=> {
            resolved++;
            return new Infrastructure.Identity.ClientEntraLifecycleService(db,configuration,Microsoft.Extensions.Logging.Abstractions.NullLogger<Infrastructure.Identity.ClientEntraLifecycleService>.Instance);
        });
        var teamPage=new ParfaitInternalPageDefinition {Key="/internal/settings/team",Route="/internal/settings/team",Title="Team",Group="Settings",Description="Team"};
        var registry=new Mock<IParfaitInternalPageRegistry>();registry.Setup(x=>x.GetAllPages()).Returns(new[]{teamPage});
        registry.Setup(x=>x.ResolveByPath("/internal/settings/team")).Returns(teamPage);
        var service=new ParfaitTeamAccessService(db,new ParfaitBusinessScopeService(db),registry.Object,
            new Infrastructure.Businesses.BusinessWorkspaceService(db,null!,configuration),null!,
            new HttpContextAccessor {HttpContext=new DefaultHttpContext {User=User(oid)}},identity,Mock.Of<IGraphMailService>(),configuration);
        Assert.True((await service.AuthorizeSignInAsync(User(oid))).Allowed);
        Assert.Single((await service.GetTeamManagementViewModelAsync()).Members);
        Assert.Equal(0,resolved);
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>service.AddMemberAsync(new ParfaitTeamCreateMemberInput {Email="new@example.invalid",DisplayName="New Member",RoleKey="support"},"https://example.invalid","Owner"));
        Assert.Equal("Microsoft account invitations are not configured. Contact the administrator.",error.Message);
        Assert.Equal(1,resolved);
        Assert.Single(db.CommerceBusinessMembers);
        Assert.Empty(db.ClientIdentityContinuations);
        profile.Email="first-owner@example.invalid";
        var secondProfile=new ClientProfile {ClientUserId=Guid.NewGuid().ToString(),Email="second-owner@example.invalid"};
        db.AddRange(secondProfile,new CommerceBusinessMember {CommerceBusinessId=business.Id,ClientProfileId=secondProfile.Id,RoleKey="account",Status="Active"},
            new CommerceBusinessMember {CommerceBusinessId=business.Id,RoleKey="owner",Status="Revoked",Email="revoked@example.invalid"});
        await db.SaveChangesAsync();
        var summary=await service.GetTeamManagementViewModelAsync();
        Assert.Equal(2,summary.FounderCount);
        Assert.Contains(profile.Email,summary.FounderEmail);
        Assert.Contains(secondProfile.Email,summary.FounderEmail);
        Assert.DoesNotContain("revoked@example.invalid",summary.FounderEmail);
        Assert.Equal(1,resolved);
    }

    [Fact]
    public async Task ReceptionistSettingsRequireCanonicalSettingsAndRespectPageRestrictions()
    {
        await using var db=ControllerTestHelpers.BuildDb();
        var oid=Guid.NewGuid().ToString();
        var business=new CommerceBusiness {Key="parfait",Status="Active",IsActive=true};
        var profile=new ClientProfile {ClientUserId=oid};
        var member=new CommerceBusinessMember {CommerceBusinessId=business.Id,ClientProfileId=profile.Id,RoleKey="staff",CanManageOrders=true};
        db.AddRange(business,profile,member);await db.SaveChangesAsync();var service=Service(db);
        Assert.False((await service.AuthorizePageAsync(User(oid),"/internal/settings/receptionist")).Allowed);
        member.RoleKey="owner";await db.SaveChangesAsync();
        Assert.True((await service.AuthorizePageAsync(User(oid),"/internal/settings/receptionist")).Allowed);
        member.ParfaitPageGrantsJson="[\"/internal/commerce/orders\"]";await db.SaveChangesAsync();
        Assert.False((await service.AuthorizePageAsync(User(oid),"/internal/settings/receptionist")).Allowed);
        var pause=typeof(ParfaitApp.Controllers.InternalModulesController).GetMethod("PauseReceptionist")!;
        Assert.NotEmpty(pause.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.ValidateAntiForgeryTokenAttribute),true));
        Assert.DoesNotContain(pause.GetParameters(),p=>p.Name is "businessId" or "enabled");
    }

    private static ClaimsPrincipal User(string oid)=>new(new ClaimsIdentity(new[]{new Claim("oid",oid),new Claim(ClaimTypes.Email,"same@mylegnd.com")},"test"));
    private static ParfaitTeamAccessService Service(Infrastructure.Data.MasterAppDbContext db)
    {
        var pages=new List<ParfaitInternalPageDefinition> {
            new(){Key="/internal/commerce/orders",Route="/internal/commerce/orders",Title="Orders",Group="Commerce",Description=""},
            new(){Key="/internal/analytics",Route="/internal/analytics",Title="Analytics",Group="Analytics",Description=""},
            new(){Key="/internal/settings/receptionist",Route="/internal/settings/receptionist",Title="AI Receptionist",Group="Settings",Description=""}
        };
        var registry=new Mock<IParfaitInternalPageRegistry>();registry.Setup(x=>x.GetAllPages()).Returns(pages);
        registry.Setup(x=>x.ResolveByPath(It.IsAny<string>())).Returns((string path)=>pages.Find(p=>p.Key==path));
        return new(db,new ParfaitBusinessScopeService(db),registry.Object,null!,null!,new HttpContextAccessor(),null!,Mock.Of<IGraphMailService>(),new ConfigurationBuilder().Build());
    }
}
