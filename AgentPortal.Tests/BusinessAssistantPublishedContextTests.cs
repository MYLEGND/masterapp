using System.Linq;
using System.Text.Json;
using Infrastructure.WebsiteEditing;
using Xunit;

namespace AgentPortal.Tests;

public sealed class BusinessAssistantPublishedContextTests
{
    [Fact]
    public void ProjectionExcludesHiddenContentAndBindingsAndReportsTruncation()
    {
        var document = new WebsiteContentDocument();
        document.Pages["/"] = new WebsitePageDocument { Title = "Public offer", Composition = [
            new() { Id = "visible", Type = "text", Text = new string('a', 1500) },
            new() { Id = "hidden", Type = "text", Text = "hidden private text", Hidden = true },
            new() { Id = "link", Type = "text", Text = "Public call to action", Href = "https://example.test/?token=private" }
        ] };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var persisted = JsonSerializer.Serialize(WebsiteContentSanitizer.Sanitize(document), options);
        var result = WebsiteContentStore.ProjectPublishedText(persisted);
        var serialized = JsonSerializer.Serialize(result);
        Assert.Contains("Public offer", serialized);
        Assert.Contains("Public call to action", serialized);
        Assert.DoesNotContain("hidden private text", serialized);
        Assert.DoesNotContain("token=private", serialized);
        Assert.True(result.Partial);
        Assert.True(result.Pages.Sum(x => (x.Title?.Length ?? 0) + (x.Description?.Length ?? 0) + x.Text.Sum(t => t.Length)) <= 6000);
    }
}
