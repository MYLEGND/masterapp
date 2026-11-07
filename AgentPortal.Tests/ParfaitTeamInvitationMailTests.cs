using System.Reflection;
using ParfaitApp.Services;
using Xunit;

namespace AgentPortal.Tests;
public sealed class ParfaitTeamInvitationMailTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("https://invite.example.invalid/redeem?a=1&b=2\"<script>")]
    public void InvitationPreservesRequiredIdentitySetupAndEncodesBothLinks(string? redemption)
    {
        var method = typeof(GraphMailService).GetMethod("BuildTeamInvitationHtml", BindingFlags.NonPublic | BindingFlags.Static)!;
        var html = (string)method.Invoke(null, new object?[] { "external@example.invalid", "<Member>", "support", new string[0],
            "https://portal.example.invalid/accept?token=a&b=2", "<Owner>", "<Store>", redemption })!;
        Assert.Contains("Accept invitation", html);
        Assert.Contains("invited Microsoft account", html);
        Assert.DoesNotContain("@mylegnd.com", html);
        Assert.Contains("token=a&amp;b=2", html);
        Assert.Contains("&lt;Member&gt;", html);
        Assert.DoesNotContain("<script>", html);
        if (redemption is null) Assert.DoesNotContain("Microsoft account setup", html);
        else {
            Assert.Contains("Microsoft account setup", html);
            Assert.Contains("a=1&amp;b=2&quot;&lt;script&gt;", html);
            Assert.True(html.IndexOf("Microsoft account setup", System.StringComparison.Ordinal) < html.IndexOf("Accept invitation", System.StringComparison.Ordinal));
        }
    }
}
