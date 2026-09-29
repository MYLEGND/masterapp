using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Infrastructure.WebsiteEditing;
using Xunit;

namespace AgentPortal.Tests;

public sealed class WebsiteStudioV3ContractTests
{
    [Fact]
    public void Sanitize_PreservesTypedResponsiveCompositionNavigationReuseAndBusinessFactBindings()
    {
        var animationId = Guid.NewGuid().ToString("N");
        var source = new WebsiteContentDocument
        {
            Breakpoints =
            [
                .. WebsiteStudioContract.DefaultBreakpoints(),
                new WebsiteBreakpointDefinition { Key = "wide", Label = "Wide desktop", MinWidth = 1600, MaxWidth = 2200 },
                new WebsiteBreakpointDefinition { Key = "bad key!", Label = "Rejected", MinWidth = -20 }
            ],
            Pages = new(StringComparer.Ordinal)
            {
                ["/"] = new WebsitePageDocument
                {
                    Title = "Home",
                    Navigation = new WebsitePageNavigation { Label = "Home", ShowInNavigation = true, Order = 0 },
                    Composition =
                    [
                        new WebsiteCompositionNode
                        {
                            Id = "home.title",
                            Type = "heading",
                            Tag = "h1",
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
                            ]
                        }
                    ]
                },
                ["/about"] = new WebsitePageDocument
                {
                    Title = "About",
                    Navigation = new WebsitePageNavigation { Label = "About us", ShowInNavigation = true, ParentPath = "/", Order = 2 }
                }
            }
        };
        source.ReusableComponents["hero"] = new WebsiteReusableComponentDefinition
        {
            Id = "hero",
            Name = "Shared hero",
            Kind = "section",
            Composition =
            [
                new WebsiteCompositionNode
                {
                    Id = "hero.title",
                    Type = "heading",
                    Tag = "h2",
                    Text = "Reusable"
                }
            ]
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

        var node = clean.Pages["/"].Composition.Single(value => value.Id == "home.title");
        Assert.Equal("Existing text", node.Text);
        Assert.Equal(80m, node.Style.WidthPercent);
        Assert.Equal(100m, node.BreakpointStyles["mobile"].WidthPercent);
        Assert.Equal(70m, node.BreakpointStyles["wide"].WidthPercent);
        Assert.DoesNotContain("unknown", node.BreakpointStyles.Keys);
        Assert.Equal("flex", node.Layout.Mode);
        Assert.Equal("stack", node.BreakpointLayouts["mobile"].Mode);
        Assert.DoesNotContain("unknown", node.BreakpointLayouts.Keys);
        Assert.Equal(animationId, Assert.Single(node.Animations).Id);

        Assert.Equal("About us", clean.Pages["/about"].Navigation.Label);
        Assert.Equal("/", clean.Pages["/about"].Navigation.ParentPath);
        Assert.Single(clean.ReusableComponents["hero"].Composition);
        Assert.Equal(["services", "hours"], clean.Collections["business-profile"].Fields);
        Assert.False(clean.Collections.ContainsKey("shadow-store"));
    }

    [Fact]
    public void ReusableDefinitions_AreCompositionOnly_AndMissingInstancesFailQuality()
    {
        var source = new WebsiteContentDocument();
        source.ReusableComponents["shared-section"] = new WebsiteReusableComponentDefinition
        {
            Id = "shared-section",
            Name = "Shared section",
            Kind = "section",
            Composition =
            [
                new WebsiteCompositionNode
                {
                    Id = "component.root",
                    Type = "section",
                    Tag = "section",
                    Children =
                    [
                        new WebsiteCompositionNode { Id = "component.copy", Type = "text", Tag = "p", Text = "Shared copy" }
                    ]
                }
            ]
        };
        source.Pages["/"] = new WebsitePageDocument
        {
            Title = "Home",
            Navigation = new WebsitePageNavigation { Label = "Home", ShowInNavigation = true },
            Composition =
            [
                new WebsiteCompositionNode { Id = "valid-instance", Type = "reusable", Tag = "div", SyncSourceId = "shared-section" },
                new WebsiteCompositionNode { Id = "missing-instance", Type = "reusable", Tag = "div", SyncSourceId = "missing-component" }
            ]
        };

        var clean = WebsiteContentSanitizer.Sanitize(source);
        Assert.Single(clean.ReusableComponents["shared-section"].Composition);
        Assert.Contains(clean.Pages["/"].Composition, node => node.Id == "valid-instance" && node.SyncSourceId == "shared-section");
        Assert.Contains(clean.Pages["/"].Composition, node => node.Id == "missing-instance" && node.SyncSourceId == "missing-component");

        var report = WebsiteDraftQualityInspector.Inspect(clean);
        Assert.DoesNotContain(report.Checks, check => check.ElementId == "valid-instance" && check.Code == "reusable_component_missing");
        Assert.Contains(report.Checks, check => check.ElementId == "missing-instance" && check.Code == "reusable_component_missing" && check.Severity == "error");
    }

    [Fact]
    public void CanonicalSchema_RejectsUnknownLegacyAuthorityFields()
    {
        var source = new WebsiteContentDocument
        {
            UnexpectedFields = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["elements"] = JsonSerializer.SerializeToElement(new { legacy = true })
            }
        };

        var error = Assert.Throws<ArgumentException>(() => WebsiteContentSanitizer.Sanitize(source));
        Assert.Contains("elements", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyPersistedJson_IsReadOnlyMigrationInputAndCannotBeSavedAsCanonical()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var legacyJson =
            """
            {
              "version": 2,
              "elements": {
                "home.title": { "text": "Legacy" }
              },
              "pages": {
                "/": {
                  "title": "Home",
                  "navigation": { "label": "Home", "showInNavigation": true },
                  "elements": {
                    "home.title": { "text": "Legacy" }
                  },
                  "extras": [],
                  "sectionOrder": {}
                }
              }
            }
            """;

        var migrationView = WebsiteContentSanitizer.ReadPersisted(legacyJson, options);

        Assert.Equal(WebsiteStudioContract.CurrentDocumentVersion, migrationView.Version);
        Assert.True(migrationView.Pages.ContainsKey("/"));
        Assert.Throws<InvalidOperationException>(() => WebsiteContentSanitizer.Sanitize(migrationView));
        var canonicalJson = JsonSerializer.Serialize(migrationView, options);
        Assert.DoesNotContain(""elements"", canonicalJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(""extras"", canonicalJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(""sectionOrder"", canonicalJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CanonicalAssembly_DoesNotExposeRetiredOverrideTypes()
    {
        var assembly = typeof(WebsiteContentDocument).Assembly;
        Assert.Null(assembly.GetType("Infrastructure.WebsiteEditing.WebsiteElementOverride"));
        Assert.Null(assembly.GetType("Infrastructure.WebsiteEditing.WebsiteExtraComponent"));
        Assert.Null(assembly.GetType("Infrastructure.WebsiteEditing.WebsitePlacement"));
    }
}
