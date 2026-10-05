using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Infrastructure.WebsiteEditing;
using Xunit;

namespace AgentPortal.Tests;

public sealed class WebsiteCreativeWorkspaceTests
{
    private static IReadOnlyList<WebsiteCallToActionOption> Actions() =>
        WebsiteCallToActionCatalog.Build(
            WebsiteEditorSiteKeys.Business,
            "3605551212",
            "owner@example.test",
            "https://example.test/book",
            "/store");

    private static WebsiteContentDocument Baseline()
    {
        return new WebsiteContentDocument
        {
            Shell = new WebsiteSharedShellDocument
            {
                Header =
                [
                    new WebsiteCompositionNode
                    {
                        Id = "shell.primary-nav",
                        Type = "container",
                        Tag = "nav",
                        SystemKey = "primary_navigation",
                        ClassName = "nav"
                    }
                ]
            },
            Pages = new(StringComparer.Ordinal)
            {
                ["/"] = new WebsitePageDocument
                {
                    Title = "Home",
                    Description = "Home",
                    Navigation = new WebsitePageNavigation { Label = "Home", Order = 0 },
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
                                    Id = "home.hero.title",
                                    Type = "heading",
                                    Tag = "h1",
                                    Text = "Original"
                                }
                            ]
                        }
                    ]
                },
                ["/old"] = new WebsitePageDocument
                {
                    Title = "Old",
                    Description = "Old",
                    Navigation = new WebsitePageNavigation { Label = "Old", Order = 10 },
                    Composition =
                    [
                        new WebsiteCompositionNode
                        {
                            Id = "old.section",
                            Type = "section",
                            Tag = "section",
                            Children =
                            [
                                new WebsiteCompositionNode
                                {
                                    Id = "old.title",
                                    Type = "heading",
                                    Tag = "h1",
                                    Text = "Old page"
                                }
                            ]
                        }
                    ]
                }
            }
        };
    }

    private static WebsiteCapabilityManifest Capabilities(WebsiteContentDocument document) =>
        WebsiteCreativeCapabilityResolver.Resolve(
            WebsiteEditorSiteKeys.Business,
            WebsiteEditorSiteKeys.BusinessOwnerKey(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            document,
            Actions());

    [Fact]
    public void CapabilityManifest_ExposesSemanticReferences_NotWritableProviderConfiguration()
    {
        var document = Baseline();
        var manifest = Capabilities(document);

        Assert.Contains(manifest.Capabilities, value => value.Key == "contact.inquiry.submit" && value.Protected);
        Assert.Contains(manifest.Capabilities, value => value.Key == "experience.lead_capture" && value.Protected);
        Assert.Contains(manifest.Capabilities, value => value.Kind == "action" && value.ActionKey is not null);

        var serialized = System.Text.Json.JsonSerializer.Serialize(manifest);
        Assert.DoesNotContain("accessToken", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pixelId", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("endpoint", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fieldSignals", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Mutation_ProtectedNavigation_PreservesAuthorityWhileAllowingPresentation()
    {
        var document = Baseline();
        var manifest = Capabilities(document);
        var replacement = new WebsiteCompositionNode
        {
            Id = "shell.primary-nav",
            Type = "text",
            Tag = "p",
            Text = "Visible navigation treatment",
            ClassName = "premium-nav",
            Style = new WebsiteVisualStyle { FontSize = 18, Color = "#ffffff" }
        };

        var result = WebsiteDocumentMutationService.Apply(
            document,
            WebsiteEditorSiteKeys.Business,
            Actions(),
            manifest,
            [new WebsiteMutationOperation
            {
                Type = "replaceNode",
                NodeId = "shell.primary-nav",
                Node = replacement
            }]);

        var navigation = result.Document.Shell.Header.Single();
        Assert.Equal("container", navigation.Type);
        Assert.Equal("nav", navigation.Tag);
        Assert.Equal("primary_navigation", navigation.SystemKey);
        Assert.Equal("premium-nav", navigation.ClassName);
        Assert.Equal(18m, navigation.Style.FontSize);
        Assert.Equal("#ffffff", navigation.Style.Color);
    }

    [Fact]
    public void Mutation_FreeNode_CannotSmuggleSystemOrSignalAuthority()
    {
        var document = Baseline();
        var manifest = Capabilities(document);

        var systemNode = new WebsiteCompositionNode
        {
            Id = "home.bad-system",
            Type = "container",
            Tag = "div",
            SystemKey = "primary_navigation"
        };
        Assert.Throws<WebsiteSiteSourceProtectionException>(() =>
            WebsiteDocumentMutationService.Apply(
                document,
                WebsiteEditorSiteKeys.Business,
                Actions(),
                manifest,
                [new WebsiteMutationOperation
                {
                    Type = "insertNode",
                    Scope = "page",
                    PagePath = "/",
                    Node = systemNode
                }]));

        var signalNode = new WebsiteCompositionNode
        {
            Id = "home.bad-signal",
            Type = "text",
            Tag = "p",
            Text = "Tracked",
            Signals =
            [
                new WebsiteSignalBinding
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Trigger = "click",
                    EventName = "cta_click",
                    ActionKey = "cta_click",
                    DeliveryMode = "analytics"
                }
            ]
        };
        Assert.Throws<WebsiteSiteSourceProtectionException>(() =>
            WebsiteDocumentMutationService.Apply(
                document,
                WebsiteEditorSiteKeys.Business,
                Actions(),
                manifest,
                [new WebsiteMutationOperation
                {
                    Type = "insertNode",
                    Scope = "page",
                    PagePath = "/",
                    Node = signalNode
                }]));
    }

    [Fact]
    public void Mutation_CanonicalInquiry_IsCreatedOnlyThroughCapabilityAuthority()
    {
        var document = Baseline();
        var manifest = Capabilities(document);

        var result = WebsiteDocumentMutationService.Apply(
            document,
            WebsiteEditorSiteKeys.Business,
            Actions(),
            manifest,
            [new WebsiteMutationOperation
            {
                Type = "insertCapability",
                Scope = "page",
                PagePath = "/",
                ParentId = "home.hero",
                CapabilityKey = "contact.inquiry.submit",
                InstanceKey = "home.hero.inquiry",
                Content = new Dictionary<string, string>
                {
                    ["title"] = "Tell us what you need",
                    ["submit"] = "Start the conversation"
                }
            }]);

        var form = result.Document.Pages["/"].Composition[0].Children
            .Single(value => value.Id == "home.hero.inquiry");
        Assert.Equal("form", form.Type);
        Assert.Equal("canonical_inquiry", form.SystemKey);
        Assert.Equal("Tell us what you need", form.Title);
        Assert.Equal("Start the conversation", form.Text);
        Assert.Empty(form.Signals);
    }

    [Fact]
    public void Mutation_NativeLeadCapture_RequiresCapabilitySelection()
    {
        var document = Baseline();
        var manifest = Capabilities(document);
        var experience = new WebsiteCompositionNode
        {
            Id = "home.estimator",
            Type = "experience",
            Tag = "form",
            Experience = new WebsiteExperienceDefinition
            {
                Kind = "calculator",
                SubmitCapability = WebsiteExperiencePolicy.LeadCaptureCapability
            }
        };

        Assert.Throws<WebsiteSiteSourceProtectionException>(() =>
            WebsiteDocumentMutationService.Apply(
                document,
                WebsiteEditorSiteKeys.Business,
                Actions(),
                manifest,
                [new WebsiteMutationOperation
                {
                    Type = "insertNode",
                    Scope = "page",
                    PagePath = "/",
                    ParentId = "home.hero",
                    Node = experience
                }]));

        experience.Experience!.SubmitCapability = null;
        var result = WebsiteDocumentMutationService.Apply(
            document,
            WebsiteEditorSiteKeys.Business,
            Actions(),
            manifest,
            [
                new WebsiteMutationOperation
                {
                    Type = "insertNode",
                    Scope = "page",
                    PagePath = "/",
                    ParentId = "home.hero",
                    Node = experience
                },
                new WebsiteMutationOperation
                {
                    Type = "setApprovedCapability",
                    NodeId = "home.estimator",
                    CapabilityKey = "experience.lead_capture"
                }
            ]);

        var saved = result.Document.Pages["/"].Composition[0].Children
            .Single(value => value.Id == "home.estimator");
        Assert.Equal(WebsiteExperiencePolicy.LeadCaptureCapability, saved.Experience!.SubmitCapability);
    }

    [Fact]
    public void DesignPlan_BuildsWholeBusinessSite_AndRetiresUnplannedRoutes()
    {
        var document = Baseline();
        var manifest = Capabilities(document);
        var plan = new WebsiteDesignPlan
        {
            ArtDirection = "roadster-precision",
            ReplaceBusinessPages = true,
            Pages =
            [
                new WebsiteDesignPlanPage
                {
                    Path = "/",
                    Title = "Precision Home",
                    Description = "Premium home",
                    NavigationLabel = "Home",
                    NavigationOrder = 0,
                    Sections =
                    [
                        new WebsiteDesignPlanSection
                        {
                            Recipe = "hero.cinematic",
                            Key = "home.hero.new",
                            Content = new(StringComparer.Ordinal)
                            {
                                ["headline"] = "Built for a sharper next move.",
                                ["body"] = "Business-specific supporting copy."
                            }
                        },
                        new WebsiteDesignPlanSection
                        {
                            Recipe = "cta.closing",
                            Key = "home.close",
                            CapabilityKey = "contact.inquiry.submit",
                            Content = new(StringComparer.Ordinal)
                            {
                                ["headline"] = "Start here.",
                                ["title"] = "Tell us what you need",
                                ["submit"] = "Get started"
                            }
                        }
                    ]
                },
                new WebsiteDesignPlanPage
                {
                    Path = "/services",
                    Title = "Services",
                    Description = "Services",
                    NavigationLabel = "Services",
                    NavigationOrder = 10,
                    Sections =
                    [
                        new WebsiteDesignPlanSection
                        {
                            Recipe = "services.grid",
                            Key = "services.grid",
                            Content = new(StringComparer.Ordinal)
                            {
                                ["headline"] = "Focused services.",
                                ["item1"] = "Strategy",
                                ["item2"] = "Execution",
                                ["item3"] = "Optimization"
                            }
                        }
                    ]
                }
            ]
        };

        var operations = WebsiteDesignPlanResolver.Resolve(
            document,
            WebsiteEditorSiteKeys.Business,
            manifest,
            plan);
        var result = WebsiteDocumentMutationService.Apply(
            document,
            WebsiteEditorSiteKeys.Business,
            Actions(),
            manifest,
            operations);

        Assert.Equal("Precision Home", result.Document.Pages["/"].Title);
        Assert.True(result.Document.Pages["/old"].Navigation.IsDeleted);
        Assert.True(result.Document.Pages.ContainsKey("/services"));
        Assert.Contains(result.Document.Pages["/"].Composition, value => value.Id == "home.hero.new");
        Assert.Contains(result.Document.Pages["/"].Composition, value => value.Id == "home.close");
        Assert.Contains(WebsiteSiteSource.Flatten(result.Document),
            value => value.Node.Type == "form" && value.Node.SystemKey == "canonical_inquiry");
        Assert.Equal("#07152d", result.Document.Theme.Navy);
        Assert.Equal(72m, result.Document.Theme.DisplaySize);
    }

    [Fact]
    public void PageRecipe_ExpandsIntoOrdinarySectionMutations()
    {
        var document = Baseline();
        var manifest = Capabilities(document);
        var plan = new WebsiteDesignPlan
        {
            Pages =
            [
                new WebsiteDesignPlanPage
                {
                    Path = "/",
                    Recipe = "home",
                    Title = "Home",
                    NavigationLabel = "Home"
                }
            ]
        };

        var operations = WebsiteDesignPlanResolver.Resolve(
            document,
            WebsiteEditorSiteKeys.Business,
            manifest,
            plan);

        Assert.Contains(operations, value => value.Type == "insertRecipe" && value.RecipeKey == "hero.cinematic");
        Assert.Contains(operations, value => value.Type == "insertRecipe" && value.RecipeKey == "services.grid");
        Assert.Contains(operations, value => value.Type == "insertRecipe" && value.RecipeKey == "cta.closing");
        Assert.DoesNotContain(operations, value => value.Type == "insertNode" && value.Node?.SystemKey is not null);
    }

    [Fact]
    public async Task MediaVisualMetadata_ReadsIntrinsicPngDimensionsWithoutTrustingFilename()
    {
        var bytes = new byte[24];
        bytes[0] = 0x89; bytes[1] = 0x50; bytes[2] = 0x4e; bytes[3] = 0x47;
        bytes[16] = 0x00; bytes[17] = 0x00; bytes[18] = 0x07; bytes[19] = 0x80; // 1920
        bytes[20] = 0x00; bytes[21] = 0x00; bytes[22] = 0x04; bytes[23] = 0x38; // 1080

        await using var stream = new MemoryStream(bytes, writable: false);
        var metadata = await WebsiteMediaVisualMetadataInspector.InspectAsync(stream, "image/png");

        Assert.Equal(1920, metadata.WidthPx);
        Assert.Equal(1080, metadata.HeightPx);
        Assert.Equal(1.7778m, metadata.AspectRatio);
        Assert.Equal("landscape", metadata.Orientation);
    }

    [Fact]
    public async Task MediaVisualMetadata_ReadsWebpExtendedCanvas_AndClassifiesPortrait()
    {
        var bytes = new byte[30];
        bytes[0] = (byte)'R'; bytes[1] = (byte)'I'; bytes[2] = (byte)'F'; bytes[3] = (byte)'F';
        bytes[8] = (byte)'W'; bytes[9] = (byte)'E'; bytes[10] = (byte)'B'; bytes[11] = (byte)'P';
        bytes[12] = (byte)'V'; bytes[13] = (byte)'P'; bytes[14] = (byte)'8'; bytes[15] = (byte)'X';
        var widthMinusOne = 799;
        var heightMinusOne = 1199;
        bytes[24] = (byte)(widthMinusOne & 0xff);
        bytes[25] = (byte)((widthMinusOne >> 8) & 0xff);
        bytes[26] = (byte)((widthMinusOne >> 16) & 0xff);
        bytes[27] = (byte)(heightMinusOne & 0xff);
        bytes[28] = (byte)((heightMinusOne >> 8) & 0xff);
        bytes[29] = (byte)((heightMinusOne >> 16) & 0xff);

        await using var stream = new MemoryStream(bytes, writable: false);
        var metadata = await WebsiteMediaVisualMetadataInspector.InspectAsync(stream, "image/webp");

        Assert.Equal(800, metadata.WidthPx);
        Assert.Equal(1200, metadata.HeightPx);
        Assert.Equal(0.6667m, metadata.AspectRatio);
        Assert.Equal("portrait", metadata.Orientation);
    }

    [Fact]
    public void CompactSource_OmitsDefaultNodeBoilerplate()
    {
        var document = Baseline();
        var serialized = WebsiteSiteSource.Serialize(document);

        Assert.DoesNotContain("\"breakpointStyles\": {}", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("\"breakpointLayouts\": {}", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("\"animations\": []", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("\"fieldPresentations\": {}", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("\"fieldLabels\": {}", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task MediaMetadata_UsesVerifiedBytesForIntrinsicDimensions()
    {
        var png = new byte[24];
        png[0] = 0x89; png[1] = 0x50; png[2] = 0x4e; png[3] = 0x47;
        png[16] = 0x00; png[17] = 0x00; png[18] = 0x07; png[19] = 0x80; // 1920
        png[20] = 0x00; png[21] = 0x00; png[22] = 0x04; png[23] = 0x38; // 1080

        await using var stream = new System.IO.MemoryStream(png, writable: false);
        var metadata = await WebsiteMediaVisualMetadataInspector.InspectAsync(stream, "image/png");

        Assert.Equal(1920, metadata.WidthPx);
        Assert.Equal(1080, metadata.HeightPx);
        Assert.Equal("landscape", metadata.Orientation);
        Assert.Equal(1.7778m, metadata.AspectRatio);
    }

    [Fact]
    public void DesignQuality_FindsPlaceholderCopy_AndReportsConversionPath()
    {
        var document = Baseline();
        document.Pages["/"].Composition[0].Children[0].Text = "A sharper way forward.";
        var manifest = Capabilities(document);

        var report = WebsiteDesignQualityInspector.Inspect(document, manifest);

        Assert.Contains(report.Checks, value => value.Code == "design_placeholder_copy");
        Assert.Contains(report.Checks, value => value.Code == "conversion_home_missing");
        Assert.Contains(report.ConversionPaths, value => value.PagePath == "/" && value.TotalConversionPoints == 0);
    }
}
