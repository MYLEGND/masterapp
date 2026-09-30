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
                            Style = new WebsiteVisualStyle { WidthPercent = 80 },
                            BreakpointStyles = new(StringComparer.Ordinal)
                            {
                                ["mobile"] = new WebsiteVisualStyle { WidthPercent = 100, FontScale = 0.9m },
                                ["wide"] = new WebsiteVisualStyle { WidthPercent = 70 },
                                ["unknown"] = new WebsiteVisualStyle { WidthPercent = 1 }
                            },
                            Layout = new WebsiteCompositionLayout { Mode = "flex", Direction = "row", GapPx = 24, AlignItems = "center", Wrap = "wrap" },
                            BreakpointLayouts = new(StringComparer.Ordinal)
                            {
                                ["mobile"] = new WebsiteCompositionLayout { Mode = "stack", Direction = "column", GapPx = 12 },
                                ["unknown"] = new WebsiteCompositionLayout { Mode = "grid", Columns = 99 }
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
    public void Sanitize_CanonicalizesStartupNavigationAndImageAccessibilityDefaults()
    {
        var source = new WebsiteContentDocument();
        source.Pages["/"] = new WebsitePageDocument
        {
            Navigation = new WebsitePageNavigation { ShowInNavigation = true },
            Composition =
            [
                new WebsiteCompositionNode
                {
                    Id = "home.photo",
                    Type = "image",
                    Tag = "img",
                    MediaUrl = "/assets/hero-photo.png"
                },
                new WebsiteCompositionNode
                {
                    Id = "home.decorative",
                    Type = "image",
                    Tag = "img",
                    MediaUrl = "/assets/decorative.png",
                    Alt = ""
                }
            ]
        };
        source.Pages["/about"] = new WebsitePageDocument
        {
            Title = "About Us",
            Navigation = new WebsitePageNavigation { ShowInNavigation = true }
        };

        var clean = WebsiteContentSanitizer.Sanitize(source);

        Assert.Equal("Home", clean.Pages["/"].Navigation.Label);
        Assert.Equal("About Us", clean.Pages["/about"].Navigation.Label);
        Assert.Equal("Hero Photo", clean.Pages["/"].Composition.Single(node => node.Id == "home.photo").Alt);
        Assert.Equal("", clean.Pages["/"].Composition.Single(node => node.Id == "home.decorative").Alt);

        var report = WebsiteDraftQualityInspector.Inspect(clean);
        Assert.DoesNotContain(report.Checks, check => check.Code == "navigation_label_missing");
        Assert.DoesNotContain(report.Checks, check => check.Code == "image_alt_missing");
    }

    [Fact]
    public void Sanitize_RepairsDeadLinksAndCorruptBrandGeometryWithoutBreakingDynamicHrefBindings()
    {
        var source = new WebsiteContentDocument
        {
            Shell = new WebsiteSharedShellDocument
            {
                Header =
                [
                    new WebsiteCompositionNode
                    {
                        Id = "shell.brand",
                        Type = "container",
                        Tag = "div",
                        ClassName = "brand-wordmark",
                        Style = new WebsiteVisualStyle { WidthPercent = 4, OffsetXPercent = 91 },
                        BreakpointStyles = new Dictionary<string, WebsiteVisualStyle>(StringComparer.Ordinal)
                        {
                            ["mobile"] = new WebsiteVisualStyle { WidthPercent = 3, OffsetXPercent = 95 }
                        },
                        Children =
                        [
                            new WebsiteCompositionNode
                            {
                                Id = "shell.brand.copy",
                                Type = "text",
                                Tag = "strong",
                                Text = "Canonical Business",
                                SystemBinding = "business_name"
                            }
                        ]
                    }
                ]
            }
        };
        source.Pages["/"] = new WebsitePageDocument
        {
            Title = "Home",
            Navigation = new WebsitePageNavigation { Label = "Home", ShowInNavigation = true },
            Composition =
            [
                new WebsiteCompositionNode
                {
                    Id = "home.dead-link",
                    Type = "link",
                    Tag = "a",
                    Text = "Placeholder",
                    Href = "#"
                },
                new WebsiteCompositionNode
                {
                    Id = "home.dynamic-link",
                    Type = "link",
                    Tag = "a",
                    Text = "Dynamic",
                    DataBinding = new WebsiteDataBinding
                    {
                        CollectionId = "catalog",
                        Field = "url",
                        Target = "href"
                    }
                }
            ]
        };
        source.Collections["catalog"] = new WebsiteCollectionDefinition
        {
            Id = "catalog",
            Source = "business-profile",
            Fields = ["url"]
        };

        var clean = WebsiteContentSanitizer.Sanitize(source);

        var dead = clean.Pages["/"].Composition.Single(node => node.Id == "home.dead-link");
        Assert.Equal("text", dead.Type);
        Assert.Equal("span", dead.Tag);
        Assert.Null(dead.Href);
        Assert.Null(dead.ActionKey);
        Assert.Empty(dead.Signals);

        var dynamic = clean.Pages["/"].Composition.Single(node => node.Id == "home.dynamic-link");
        Assert.Equal("link", dynamic.Type);
        Assert.Equal("href", dynamic.DataBinding?.Target);

        var brand = Assert.Single(clean.Shell.Header);
        Assert.Null(brand.Style.WidthPercent);
        Assert.Null(brand.Style.OffsetXPercent);
        Assert.Null(brand.BreakpointStyles["mobile"].WidthPercent);
        Assert.Null(brand.BreakpointStyles["mobile"].OffsetXPercent);

        var report = WebsiteDraftQualityInspector.Inspect(clean);
        Assert.DoesNotContain(report.Checks, check =>
            check.Code == "link_destination_missing" &&
            check.ElementId is "home.dead-link" or "home.dynamic-link");
    }

    [Fact]
    public void Sanitize_DeletesOnlySemanticallyEmptyRetiredStarterDecoration()
    {
        var source = new WebsiteContentDocument();
        source.Pages["/"] = new WebsitePageDocument
        {
            Title = "Home",
            Navigation = new WebsitePageNavigation { Label = "Home", ShowInNavigation = true },
            Composition =
            [
                new WebsiteCompositionNode
                {
                    Id = "home.hero",
                    Type = "section",
                    Tag = "section",
                    Children =
                    [
                        new WebsiteCompositionNode
                        {
                            Id = "home.hero.visual",
                            Type = "container",
                            Tag = "div",
                            ClassName = "hero-mark",
                            Children =
                            [
                                new WebsiteCompositionNode { Id = "home.hero.halo", Type = "container", Tag = "div", ClassName = "halo" },
                                new WebsiteCompositionNode { Id = "home.hero.empty", Type = "text", Tag = "span", Text = "" }
                            ]
                        },
                        new WebsiteCompositionNode { Id = "home.card.icon", Type = "text", Tag = "span", ClassName = "icon", Text = "" },
                        new WebsiteCompositionNode { Id = "home.card.copy", Type = "text", Tag = "p", ClassName = "icon", Text = "Meaningful text stays" }
                    ]
                }
            ]
        };

        var clean = WebsiteContentSanitizer.Sanitize(source);
        var hero = Assert.Single(clean.Pages["/"].Composition);
        Assert.DoesNotContain(hero.Children, node => node.Id == "home.hero.visual");
        Assert.DoesNotContain(hero.Children, node => node.Id == "home.card.icon");
        Assert.Contains(hero.Children, node => node.Id == "home.card.copy" && node.Text == "Meaningful text stays");
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
        Assert.DoesNotContain("\"elements\"", canonicalJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"extras\"", canonicalJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"sectionOrder\"", canonicalJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CanonicalV3Contract_HasNoParallelPageOrDesignAuthority()
    {
        static HashSet<string> PublicProperties(Type type) =>
            type.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
                .Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal);

        var documentProperties = PublicProperties(typeof(WebsiteContentDocument));
        Assert.DoesNotContain("Elements", documentProperties);
        Assert.DoesNotContain("Extras", documentProperties);
        Assert.DoesNotContain("SectionOrder", documentProperties);
        Assert.DoesNotContain("CompositionMode", documentProperties);
        Assert.DoesNotContain("LegacyMigration", documentProperties);

        var pageProperties = PublicProperties(typeof(WebsitePageDocument));
        Assert.DoesNotContain("Elements", pageProperties);
        Assert.DoesNotContain("Extras", pageProperties);
        Assert.DoesNotContain("SectionOrder", pageProperties);
        Assert.DoesNotContain("TemplatePath", pageProperties);
        Assert.Contains("Composition", pageProperties);

        var reusableProperties = PublicProperties(typeof(WebsiteReusableComponentDefinition));
        Assert.DoesNotContain("Elements", reusableProperties);
        Assert.DoesNotContain("Extras", reusableProperties);
        Assert.DoesNotContain("SectionOrder", reusableProperties);
        Assert.Contains("Composition", reusableProperties);

        Assert.Equal(typeof(WebsiteVisualStyle), typeof(WebsiteCompositionNode).GetProperty("Style")!.PropertyType);
        Assert.Equal(typeof(WebsiteCompositionLayout), typeof(WebsiteCompositionNode).GetProperty("Layout")!.PropertyType);
        Assert.Equal(typeof(WebsiteDesignTheme), typeof(WebsiteContentDocument).GetProperty("Theme")!.PropertyType);
    }

    [Fact]
    public void CanonicalAssembly_DoesNotExposeRetiredOverrideTypes()
    {
        var assembly = typeof(WebsiteContentDocument).Assembly;
        Assert.Null(assembly.GetType("Infrastructure.WebsiteEditing.WebsiteElementOverride"));
        Assert.Null(assembly.GetType("Infrastructure.WebsiteEditing.WebsiteExtraComponent"));
        Assert.Null(assembly.GetType("Infrastructure.WebsiteEditing.WebsitePlacement"));
        Assert.Null(assembly.GetType("Infrastructure.WebsiteEditing.WebsiteStyleOverride"));
        Assert.Null(assembly.GetType("Infrastructure.WebsiteEditing.WebsiteLayoutOverride"));
        Assert.Null(assembly.GetType("Infrastructure.WebsiteEditing.WebsiteThemeOverride"));

        Assert.NotNull(assembly.GetType("Infrastructure.WebsiteEditing.WebsiteVisualStyle"));
        Assert.NotNull(assembly.GetType("Infrastructure.WebsiteEditing.WebsiteCompositionLayout"));
        Assert.NotNull(assembly.GetType("Infrastructure.WebsiteEditing.WebsiteDesignTheme"));
    }
    [Fact]
    public void PersistedCanonicalDuplicateIds_PreserveProtectedIdentityAndRepairOnlyFreeDuplicate()
    {
        var document = new WebsiteContentDocument
        {
            Pages = new(StringComparer.Ordinal)
            {
                ["/Quote/Dental-Vision-Hearing"] = new WebsitePageDocument
                {
                    Title = "Dental Vision Hearing",
                    Composition =
                    [
                        new WebsiteCompositionNode
                        {
                            Id = "quote_dvh.root.1",
                            Type = "section",
                            Tag = "section",
                            Text = "Presentation duplicate"
                        },
                        new WebsiteCompositionNode
                        {
                            Id = "quote_dvh.root.1",
                            Type = "container",
                            Tag = "div",
                            SystemKey = "protect_runtime_form:quote_dvh_form"
                        }
                    ]
                }
            }
        };

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var repaired = WebsiteContentSanitizer.ReadPersisted(JsonSerializer.Serialize(document, options), options);
        var nodes = repaired.Pages["/Quote/Dental-Vision-Hearing"].Composition;

        var protectedNode = Assert.Single(nodes.Where(node => !string.IsNullOrWhiteSpace(node.SystemKey)));
        var freeNode = Assert.Single(nodes.Where(node => string.IsNullOrWhiteSpace(node.SystemKey)));
        Assert.Equal("quote_dvh.root.1", protectedNode.Id);
        Assert.StartsWith("quote_dvh.root.1.repair.", freeNode.Id);
        Assert.Equal("Presentation duplicate", freeNode.Text);
    }

}
