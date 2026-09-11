using System;
using System.Reflection;
using System.Text.Json;
using AgentPortal.Services;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendFounderDiagnosticLanguageScopeTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("EN")]
    [InlineData(" en ")]
    public void AcceptedLanguageCaseAndTrim_UseTheSameBackendScope(string language)
    {
        var arguments = Arguments(language);
        Assert.True(IsAccepted(arguments));
        Assert.Equal(Identity(Arguments("en")), Identity(arguments));
    }

    [Theory]
    [InlineData("fr", "en")]
    [InlineData("en_US", "en-US")]
    public void DifferentBackendLanguages_RemainDifferentScopes(string left, string right) =>
        Assert.NotEqual(Identity(Arguments(left)), Identity(Arguments(right)));

    [Theory]
    [InlineData("{\"section\":\"machine-learning-lifecycle\",\"language\":\"\\ten\\t\"}")]
    [InlineData("{\"section\":\"machine-learning-lifecycle\",\"language\":\"EN\",\"extra\":true}")]
    [InlineData("{\"section\":\"machine-learning-lifecycle\",\"language\":\"EN\",\"language\":\"en\"}")]
    [InlineData("{\"section\":null,\"language\":\"EN\"}")]
    public void InvalidRawArguments_AreNotNormalizedIntoAcceptedArguments(string arguments)
    {
        using var document = JsonDocument.Parse(arguments);
        Assert.Equal(document.RootElement.GetRawText(),
            LegendFounderToolAuthority.NormalizeOperationalDiagnosticArguments(document.RootElement).GetRawText());
        Assert.False(IsAccepted(arguments));
    }

    [Fact]
    public void RawLanguageOverFortyCharacters_CannotBecomeValidByTrimming()
    {
        var arguments = Arguments(new string(' ', 40) + "en");
        using var document = JsonDocument.Parse(arguments);
        Assert.Equal(document.RootElement.GetRawText(),
            LegendFounderToolAuthority.NormalizeOperationalDiagnosticArguments(document.RootElement).GetRawText());
        Assert.False(IsAccepted(arguments));
        Assert.NotEqual(Identity(Arguments("en")), Identity(arguments));
    }

    [Fact]
    public void AggregateNullDefaults_StillShareScope() => Assert.Equal(
        Identity("{}"), Identity("{\"section\":null,\"language\":null}"));

    private static string Arguments(string language) => JsonSerializer.Serialize(new
    { section = "machine-learning-lifecycle", language });
    private static string Identity(string arguments) =>
        LegendFounderAiConversationService.ReadScopeIdentity("legend_operational_diagnostics", arguments);
    private static bool IsAccepted(string arguments)
    {
        var parser = typeof(LegendFounderToolAuthority).GetMethod("TryReadOperationalDiagnosticArguments",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(parser);
        return Assert.IsType<bool>(parser.Invoke(null, new object?[] { arguments, null, null }));
    }
}
