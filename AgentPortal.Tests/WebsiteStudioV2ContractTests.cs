using Infrastructure.WebsiteEditing;
using Xunit;

namespace AgentPortal.Tests;

public sealed class WebsiteStudioV2ContractTests
{
    [Fact]
    public void Sanitize_PreservesTypedResponsiveLayoutNavigationReuseAndBusinessFactBindings()
    {
        var animationId = Guid.NewGuid().ToString("N");
        var source = new WebsiteContentDocument
        {
            Breakpoints =
            [
                .. WebsiteStudioContract.DefaultBreakpoints(),
                new WebsiteBreakpointDefinition { Key = "wide", Label = "Wide desktop", MinWidth = 1600, MaxWidth = 2200 },
                new WebsiteBreakpointDefinition { Key = "bad key!", Label = "Rejected", MinWidth = -20 }
            ]
        };
        source.Elements["home.title"] = new WebsiteElementOverride
        {
            Text = "Existing text",
            Style = new WebsiteStyleOverride { WidthPercent = 80 },
            BreakpointStyles = new(StringComparer.Ordinal)
            {
                ["mobile"] = new WebsiteStyleOverride { WidthPercent = 100, FontScale = 0.9m },
                ["wide"] = new WebsiteStyleOverride { WidthPercent = 70 },
                ["unknown"] = new WebsiteStyleOverride { WidthPercent = 1 }
            },
            Layout = new WebsiteLayoutOverride { Mode = "flex", Direction = "row", GapPx = 24, AlignItems = "center", Wrap = "wrap" },
            BreakpointLayouts = new(StringComparer.Ordinal)
            {
                ["mobile"] = new WebsiteLayoutOverride { Mode = "stack", Direction = "column", GapPx = 12 },
                ["unknown"] = new WebsiteLayoutOverride { Mode = "grid", Columns = 99 }
            },
            Animations =
            [
                new WebsiteAnimationBinding { Id = animationId, Trigger = "view", Effect = "fade", DurationMs = 450, DelayMs = 50, Easing = "ease-out" },
                new WebsiteAnimationBinding { Id = Guid.NewGuid().ToString("N"), Trigger = "timer", Effect = "javascript" }
            ],
            SyncSourceId = "shared.hero"
        };
        source.Pages["/about"] = new WebsitePageDocument
        {
            Title = "About",
            Navigation = new WebsitePageNavigation { Label = "About us", ShowInNavigation = true, ParentPath = "/", Order = 2 }
        };
        source.ReusableComponents["hero"] = new WebsiteReusableComponentDefinition
        {
            Id = "hero",
            Name = "Shared hero",
            Kind = "section",
            Elements = new(StringComparer.Ordinal)
            {
                ["hero.title"] = new WebsiteElementOverride { Text = "Reusable", SyncSourceId = "shared.hero" }
            }
        };
        source.Collections["business-profile"] = new WebsiteCollectionDefinition
        {
            Id = "business-profile",
            Name = "Business profile",
            Source = "business_facts",
            Fields = ["services", "hours", "notAllowed"]
        };
        source.Collections["shadow-store"] = new WebsiteCollectionDefinition
        {
            Id = "shadow-store",
            Name = "Parallel data",
            Source = "website_database",
            Fields = ["services"]
        };

        var clean = WebsiteContentSanitizer.Sanitize(source);

        Assert.Equal(WebsiteStudioContract.CurrentDocumentVersion, clean.Version);
        Assert.Contains(clean.Breakpoints, value => value.Key == "mobile" && value.IsSystem);
        Assert.Contains(clean.Breakpoints, value => value.Key == "wide" && value.MinWidth == 1600 && value.MaxWidth == 2200);
        Assert.DoesNotContain(clean.Breakpoints, value => value.Key == "badkey");
        var element = clean.Elements["home.title"];
        Assert.Equal("Existing text", element.Text);
        Assert.Equal(80m, element.Style.WidthPercent);
        Assert.Equal(100m, element.BreakpointStyles["mobile"].WidthPercent);
        Assert.Equal(70m, element.BreakpointStyles["wide"].WidthPercent);
        Assert.DoesNotContain("unknown", element.BreakpointStyles.Keys);
        Assert.Equal("flex", element.Layout.Mode);
        Assert.Equal("stack", element.BreakpointLayouts["mobile"].Mode);
        Assert.DoesNotContain("unknown", element.BreakpointLayouts.Keys);
        Assert.Equal(animationId, Assert.Single(element.Animations).Id);
        Assert.Equal("shared.hero", element.SyncSourceId);
        Assert.Equal("About us", clean.Pages["/about"].Navigation.Label);
        Assert.Equal("/", clean.Pages["/about"].Navigation.ParentPath);
        Assert.True(clean.ReusableComponents.ContainsKey("hero"));
        Assert.Equal(["services", "hours"], clean.Collections["business-profile"].Fields);
        Assert.False(clean.Collections.ContainsKey("shadow-store"));
    }

    [Fact]
    public void Sanitize_UpgradesLegacyDocumentWithoutInventingElementOverrides()
    {
        var source = new WebsiteContentDocument { Version = 1 };
        source.Elements["legacy.heading"] = new WebsiteElementOverride { Text = "Legacy" };

        var clean = WebsiteContentSanitizer.Sanitize(source);

        Assert.Equal(WebsiteStudioContract.CurrentDocumentVersion, clean.Version);
        Assert.Equal("Legacy", clean.Elements["legacy.heading"].Text);
        Assert.Empty(clean.Elements["legacy.heading"].BreakpointStyles);
        Assert.Equal("free", clean.Elements["legacy.heading"].Layout.Mode);
        Assert.Empty(clean.Elements["legacy.heading"].Animations);
        Assert.Equal(3, clean.Breakpoints.Count(value => value.IsSystem));
    }
}
