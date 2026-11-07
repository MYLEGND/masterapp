using ParfaitApp.Services;
using Xunit;
namespace AgentPortal.Tests;
public sealed class CommerceThemeContrastTests
{
    [Theory]
    [InlineData("#6f4b3b", "#1f120d", "#ffffff")]
    [InlineData("#d9b17a", "#081a3a", "#081a3a")]
    [InlineData("#081a3a", "#ffffff", "#ffffff")]
    [InlineData("#ffffff", "#ffffff", "#000000")]
    [InlineData("#000000", "#000000", "#ffffff")]
    public void RetainsReadableBrandForegroundOrChoosesContrastingNeutral(string background, string preferred, string expected) =>
        Assert.Equal(expected, CommerceThemeContrast.Foreground(background, preferred, "#6f4b3b"));
    [Fact]
    public void ExistingCssExpressionPreservesAuthoredForegroundWithoutContrastClaim() =>
        Assert.Equal("#abcdef", CommerceThemeContrast.Foreground("var(--web-custom)", "#abcdef", "#ffffff"));
    [Fact]
    public void OriginalUnconfiguredThemeUsesReadableCocoaForeground() =>
        Assert.Equal("#ffffff", CommerceThemeContrast.Foreground(null, null, "#6f4b3b"));
}
