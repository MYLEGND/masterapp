using AgentPortal.Services;
using System.Reflection;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MigrationHealthHostedServiceTests
{
    [Theory]
    [InlineData("SocialPosts")]
    [InlineData("SocialPostMediaAssets")]
    [InlineData("SocialPostMusicAttachments")]
    [InlineData("SocialPostReposts")]
    [InlineData("SocialPostSaves")]
    [InlineData("SocialPostShares")]
    [InlineData("SocialPostViews")]
    [InlineData("SocialProfileVisits")]
    public void CriticalTables_IncludesEveryTableRequiredByTheMobileSocialReadPath(string table)
    {
        var field = typeof(MigrationHealthHostedService).GetField(
            "CriticalTables",
            BindingFlags.Static | BindingFlags.NonPublic);

        var criticalTables = Assert.IsType<string[]>(field?.GetValue(null));

        Assert.Contains(table, criticalTables);
    }
}
