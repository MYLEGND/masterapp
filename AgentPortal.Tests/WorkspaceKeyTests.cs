using System;
using Shared.Analytics;
using Shared.Workspaces;
using Xunit;

namespace AgentPortal.Tests;

public class WorkspaceKeyTests
{
    [Fact]
    public void SameOwnerHasSameKeyAcrossMarketingAndAnalytics()
    {
        var id = Guid.NewGuid();
        Assert.Equal(WorkspaceKey.ForBusiness(id), MarketingOwnerScope.Business(id).Workspace);
        Assert.Equal(MarketingOwnerScope.Business(id).Workspace, ScopeContext.ForBusiness(id).Workspace);
        Assert.Equal(MarketingOwnerScope.Agent(id).Workspace, ScopeContext.ForAgent(id).Workspace);
        Assert.Equal($"business:{id:N}", MarketingOwnerScope.Business(id).Key);
        Assert.Equal($"agent:{id:N}", MarketingOwnerScope.Agent(id).Key);
        Assert.Equal("founder", MarketingOwnerScope.Founder.Key);
        Assert.NotEqual(MarketingOwnerScope.Agent(id).Workspace, MarketingOwnerScope.Business(id).Workspace);
    }

    [Fact]
    public void InvalidOrAggregateScopeCannotBecomeMarketingOwner()
    {
        Assert.Null(ScopeContext.ForAgent(Guid.Empty).Workspace);
        Assert.Null(ScopeContext.Global.Workspace);
        Assert.Null(ScopeContext.ForSite("public-site").Workspace);
        Assert.Null(new ScopeContext { ScopeType = ScopeType.Agent,
            AgentTrackingProfileId = Guid.NewGuid(), CommerceBusinessId = Guid.NewGuid() }.Workspace);
        Assert.Throws<ArgumentException>(() => WorkspaceKey.ForBusiness(Guid.Empty));
        Assert.Throws<ArgumentException>(() => WorkspaceKey.ForAgentTrackingProfile(Guid.Empty));
    }
}
