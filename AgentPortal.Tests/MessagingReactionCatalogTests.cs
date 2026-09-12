using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Infrastructure.Messaging;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MessagingReactionCatalogTests
{
    [Fact]
    public void EveryCanonicalUnicode16EmojiIsAcceptedByExistingReactionAuthority()
    {
        using var catalog = JsonDocument.Parse(File.ReadAllText(CatalogPath()));
        Assert.Equal("16.0", catalog.RootElement.GetProperty("unicodeVersion").GetString());
        var entries = catalog.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(3781, entries.Length);
        var rejected = entries.Where(entry => !MessagingService.IsSupportedReactionEmoji(entry.GetProperty("emoji").GetString()!))
            .Select(entry => entry.GetProperty("name").GetString()).ToArray();
        Assert.True(rejected.Length == 0, "Rejected canonical emoji: " + string.Join(", ", rejected));
    }

    [Theory]
    [InlineData("\U0001F3F4\U000E0067\U000E0062\U000E0065\U000E006E\U000E0067")]
    [InlineData("\U0001F3F4\U000E0067\U000E0062\U000E0061\U000E0062\U000E0063\U000E007F")]
    [InlineData("©️®️")]
    [InlineData("a\U000E0067\U000E007F")]
    [InlineData("\U0001F004\n")]
    public void ExtendedCatalogDoesNotAdmitMalformedTagsTextOrMultipleGraphemes(string value)
        => Assert.False(MessagingService.IsSupportedReactionEmoji(value));

    private static string CatalogPath([CallerFilePath] string source = "")
    {
        var root = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Path.GetDirectoryName(Path.GetDirectoryName(source))!;
        return Path.Combine(root, "Legend-Design", "legend-reaction-emoji.json");
    }
}
