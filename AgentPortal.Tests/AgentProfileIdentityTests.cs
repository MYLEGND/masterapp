using Domain.Entities;
using Xunit;

namespace AgentPortal.Tests;

public sealed class AgentProfileIdentityTests
{
    [Theory]
    [InlineData("CEO", "CEO - LEGEND")]
    [InlineData("  Managing Partner  ", "Managing Partner - LEGEND")]
    [InlineData(null, "LEGEND")]
    [InlineData("   ", "LEGEND")]
    public void LegendRoleLabel_UsesOnlyTheSyncedJobTitle(string? jobTitle, string expected)
    {
        Assert.Equal(expected, AgentProfileIdentity.LegendRoleLabel(jobTitle));
    }
}
