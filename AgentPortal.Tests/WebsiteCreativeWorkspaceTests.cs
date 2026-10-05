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
            document,
            Actions());

    [Theory]
    [InlineData(WebsiteEditorSiteKeys.Legend)]
    [InlineData(WebsiteEditorSiteKeys.Protect)]
    [InlineData(WebsiteEditorSiteKeys.Business)]
    public void CapabilityManifest_ExposesCanonicalInquiryAcrossWebsiteScopes(string siteKey)
    {
        var document = Baseline();
        var actions = WebsiteCallToActionCatalog.Build(siteKey);
        var manifest = WebsiteCreativeCapabilityResolver.Resolve(
            siteKey,
            document,
            actions);

        Assert.Contains(manifest.Capabilities, value =>
            value.Key == "contact.inquiry.submit" &&
            value.Kind == "protected_form" &&
            value.Protected);
        Assert.Contains(manifest.Capabilities, value =>
            value.Key == "experience.lead_capture" &&
            value.Kind == "experience_submit" &&
            value.Protected);
    }

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
    public void Mutation_ParentContainingProtectedCapability_CannotBeRemovedOrMovedAcrossPage()
    {
        var document = Baseline();
        document.Pages["/"].Composition.Add(new WebsiteCompositionNode
        {
            Id = "home.protected-section",
            Type = "section",
            Tag = "section",
            Children =
            [
                new WebsiteCompositionNode
                {
                    Id = "home.inquiry",
                    Type = "form",
                    Tag = "form",
                    SystemKey = "canonical_inquiry"
                }
            ]
        });
        document.Pages["/other"] = new WebsitePageDocument
        {
            Title = "Other",
            Navigation = new WebsitePageNavigation { Label = "Other", Order = 20 },
            Composition = []
        };
        var manifest = Capabilities(document);

        Assert.Throws<WebsiteSiteSourceProtectionException>(() =>
            WebsiteDocumentMutationService.Apply(
                document,
                WebsiteEditorSiteKeys.Business,
                Actions(),
                manifest,
                [new WebsiteMutationOperation
                {
                    Type = "removeNode",
                    NodeId = "home.protected-section"
                }]));

        Assert.Throws<WebsiteSiteSourceProtectionException>(() =>
            WebsiteDocumentMutationService.Apply(
                document,
                WebsiteEditorSiteKeys.Business,
                Actions(),
                manifest,
                [new WebsiteMutationOperation
                {
                    Type = "moveNode",
                    NodeId = "home.protected-section",
                    Scope = "page",
                    PagePath = "/other"
                }]));
    }

    [Fact]
    public void Mutation_ProtectedSignalNode_CannotBeRetargetedToAnotherCapability()
    {
        var document = Baseline();
        var action = Actions().First(value => value.Key == "business_contact");
        document.Pages["/"].Composition[0].Children.Add(new WebsiteCompositionNode
        {
            Id = "home.tracked-cta",
            Type = "cta",
            Tag = "a",
            Text = "Contact",
            ActionKey = action.Key,
            Href = action.Href,
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
        });
        var manifest = Capabilities(document);
        var other = manifest.Capabilities.First(value =>
            value.Kind == "action" && value.ActionKey != action.Key);

        Assert.Throws<WebsiteSiteSourceProtectionException>(() =>
            WebsiteDocumentMutationService.Apply(
                document,
                WebsiteEditorSiteKeys.Business,
                Actions(),
                manifest,
                [new WebsiteMutationOperation
                {
                    Type = "setApprovedCapability",
                    NodeId = "home.tracked-cta",
                    CapabilityKey = other.Key
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
        Assert.Equal(
            new[] { "home.hero.new", "home.close" },
            result.Document.Pages["/"].Composition.Select(value => value.Id).ToArray());
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
        var inserts = operations.Where(value => value.Type == "insertRecipe").ToArray();
        Assert.Equal(Enumerable.Range(0, inserts.Length), inserts.Select(value => value.Index!.Value));
        Assert.Equal("action.business_schedule", inserts.First().CapabilityKey);
        Assert.Equal("action.business_schedule", inserts.Last().CapabilityKey);
        Assert.DoesNotContain(inserts, value => value.CapabilityKey == "contact.inquiry.submit");
        Assert.DoesNotContain(operations, value => value.Type == "insertNode" && value.Node?.SystemKey is not null);
    }

    [Fact]
    public void ContactPageRecipe_DefaultsInquiryOnlyIntoTheInquirySection()
    {
        var sections = WebsitePageRecipeCatalog.Build(
            "contact",
            "contact",
            "action.business_schedule");

        Assert.Equal("action.business_schedule", sections.First().CapabilityKey);
        Assert.Single(sections.Where(value => value.CapabilityKey == "contact.inquiry.submit"));
        Assert.Equal(
            "contact.inquiry",
            sections.Single(value => value.CapabilityKey == "contact.inquiry.submit").Recipe);
    }

    [Fact]
    public void PremiumSplitRecipes_UseCoherentCopyAndMediaColumns_AndCollapseWithoutMedia()
    {
        var mediaId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var split = WebsiteRecipeCatalog.Build(
            "hero.split",
            "hero",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["headline"] = "Precision at first glance.",
                ["body"] = "A focused premium opening."
            },
            mediaId);

        Assert.Equal("free", split.Layout.Mode);
        Assert.Null(split.Layout.Columns);
        Assert.Equal(2, split.Children.Count);
        Assert.Equal("hero.copy", split.Children[0].Id);
        Assert.Equal("container", split.Children[0].Type);
        Assert.Equal(mediaId, split.Children[1].MediaAssetId);

        var featureWithMedia = WebsiteRecipeCatalog.Build(
            "feature.split",
            "feature",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["headline"] = "Asymmetric by design."
            },
            mediaId);
        Assert.Equal("free", featureWithMedia.Layout.Mode);
        Assert.Null(featureWithMedia.Layout.Columns);
        Assert.Contains("legend-recipe-feature-split", featureWithMedia.ClassName ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(2, featureWithMedia.Children.Count);

        var withoutMedia = WebsiteRecipeCatalog.Build(
            "hero.split",
            "hero-no-media",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["headline"] = "Precision without filler."
            });
        Assert.Equal("free", withoutMedia.Layout.Mode);
        Assert.DoesNotContain("legend-recipe-hero-split", withoutMedia.ClassName ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("legend-recipe-hero-cinematic", withoutMedia.ClassName ?? string.Empty, StringComparison.Ordinal);

        var featureWithoutMedia = WebsiteRecipeCatalog.Build(
            "feature.split",
            "feature-no-media",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["headline"] = "One clean column until media exists."
            });
        Assert.Equal("stack", featureWithoutMedia.Layout.Mode);
        Assert.DoesNotContain("legend-recipe-feature-split", featureWithoutMedia.ClassName ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Testimonials_RequireVerifiedQuoteContent()
    {
        Assert.Throws<ArgumentException>(() =>
            WebsiteRecipeCatalog.Build(
                "testimonials",
                "proof",
                new Dictionary<string, string>(StringComparer.Ordinal)));

        var section = WebsiteRecipeCatalog.Build(
            "testimonials",
            "proof",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["headline"] = "What clients say",
                ["quote1"] = "Verified customer quote."
            });

        var quotes = section.Children.Single(value => value.Id == "proof.quotes");
        Assert.Single(quotes.Children);
        Assert.Equal("Verified customer quote.", quotes.Children[0].Text);
    }

    [Fact]
    public void DesignPlan_MovesProtectedRuntimeIntoNewComposition_ThenRemovesStaleWrapper()
    {
        var path = "/Quote/Life";
        var runtimeKey = WebsiteSystemTemplateAuthority.RuntimeFormKey(path)!;
        var document = new WebsiteContentDocument
        {
            Pages = new(StringComparer.Ordinal)
            {
                [path] = new WebsitePageDocument
                {
                    Title = "Life Quote",
                    Navigation = new WebsitePageNavigation { Label = "Life Quote", Order = 0 },
                    Composition =
                    [
                        new WebsiteCompositionNode
                        {
                            Id = "legacy.wrapper",
                            Type = "section",
                            Tag = "section",
                            Children =
                            [
                                new WebsiteCompositionNode { Id = "legacy.copy", Type = "text", Tag = "p", Text = "Old wrapper copy" },
                                new WebsiteCompositionNode
                                {
                                    Id = "runtime.life",
                                    Type = "container",
                                    Tag = "div",
                                    SystemKey = runtimeKey
                                }
                            ]
                        }
                    ]
                }
            }
        };
        WebsiteSystemTemplateAuthority.Apply(WebsiteEditorSiteKeys.Protect, document);
        var actions = WebsiteCallToActionCatalog.Build(WebsiteEditorSiteKeys.Protect);
        var manifest = WebsiteCreativeCapabilityResolver.Resolve(
            WebsiteEditorSiteKeys.Protect,
            document,
            actions);
        var plan = new WebsiteDesignPlan
        {
            Pages =
            [
                new WebsiteDesignPlanPage
                {
                    Path = path,
                    Title = "Life Protection",
                    NavigationLabel = "Life",
                    ReplaceFreeComposition = true,
                    Sections =
                    [
                        new WebsiteDesignPlanSection
                        {
                            Recipe = "hero.cinematic",
                            Key = "life.hero",
                            Content = new(StringComparer.Ordinal) { ["headline"] = "Protect what matters." }
                        },
                        new WebsiteDesignPlanSection
                        {
                            Recipe = "feature.split",
                            Key = "life.quote",
                            CapabilityNodeId = "runtime.life",
                            Content = new(StringComparer.Ordinal) { ["headline"] = "Build your plan." }
                        }
                    ]
                }
            ]
        };

        var operations = WebsiteDesignPlanResolver.Resolve(
            document,
            WebsiteEditorSiteKeys.Protect,
            manifest,
            plan);
        var moveIndex = operations.ToList().FindIndex(value => value.Type == "moveNode" && value.NodeId == "runtime.life");
        var removeIndex = operations.ToList().FindIndex(value => value.Type == "removeNode" && value.NodeId == "legacy.wrapper");
        Assert.True(moveIndex >= 0);
        Assert.True(removeIndex > moveIndex);

        var result = WebsiteDocumentMutationService.Apply(
            document,
            WebsiteEditorSiteKeys.Protect,
            actions,
            manifest,
            operations);

        Assert.DoesNotContain(result.Document.Pages[path].Composition, value => value.Id == "legacy.wrapper");
        var runtime = WebsiteSiteSource.Flatten(result.Document)
            .Single(value => value.Node.Id == "runtime.life");
        Assert.Equal(path, runtime.PagePath);
        Assert.Equal(runtimeKey, runtime.Node.SystemKey);
        Assert.Contains(result.Document.Pages[path].Composition, value => value.Id == "life.hero");
        Assert.Contains(result.Document.Pages[path].Composition, value => value.Id == "life.quote");
    }

    [Fact]
    public void DesignPlan_FailsFastForMalformedWholeSiteInputs()
    {
        var document = Baseline();
        var manifest = Capabilities(document);

        var duplicateRoute = new WebsiteDesignPlan
        {
            Pages =
            [
                new WebsiteDesignPlanPage { Path = "/", Sections = [new WebsiteDesignPlanSection { Recipe = "hero.cinematic", Key = "one" }] },
                new WebsiteDesignPlanPage { Path = "/", Sections = [new WebsiteDesignPlanSection { Recipe = "cta.closing", Key = "two" }] }
            ]
        };
        Assert.Throws<ArgumentException>(() =>
            WebsiteDesignPlanResolver.Resolve(document, WebsiteEditorSiteKeys.Business, manifest, duplicateRoute));

        var duplicateSection = new WebsiteDesignPlan
        {
            Pages =
            [
                new WebsiteDesignPlanPage
                {
                    Path = "/",
                    Sections =
                    [
                        new WebsiteDesignPlanSection { Recipe = "hero.cinematic", Key = "same" },
                        new WebsiteDesignPlanSection { Recipe = "cta.closing", Key = "same" }
                    ]
                }
            ]
        };
        Assert.Throws<ArgumentException>(() =>
            WebsiteDesignPlanResolver.Resolve(document, WebsiteEditorSiteKeys.Business, manifest, duplicateSection));

        var unknownDirection = new WebsiteDesignPlan
        {
            ArtDirection = "roadster-typo",
            Pages = [new WebsiteDesignPlanPage { Path = "/" }]
        };
        Assert.Throws<ArgumentException>(() =>
            WebsiteDesignPlanResolver.Resolve(document, WebsiteEditorSiteKeys.Business, manifest, unknownDirection));

        var tooMany = new WebsiteDesignPlan
        {
            Pages = Enumerable.Range(0, 25)
                .Select(index => new WebsiteDesignPlanPage { Path = index == 0 ? "/" : "/p-" + index })
                .ToList()
        };
        Assert.Throws<ArgumentException>(() =>
            WebsiteDesignPlanResolver.Resolve(document, WebsiteEditorSiteKeys.Business, manifest, tooMany));

        foreach (var invalidRoute in new[]
        {
            "/Bad-Route",
            "/has space",
            "/slash\\escape",
            "/" + new string('a', 161)
        })
        {
            var invalid = new WebsiteDesignPlan
            {
                Pages = [new WebsiteDesignPlanPage { Path = invalidRoute }]
            };
            Assert.Throws<ArgumentException>(() =>
                WebsiteDesignPlanResolver.Resolve(document, WebsiteEditorSiteKeys.Business, manifest, invalid));
        }

        var protect = new WebsiteContentDocument
        {
            Pages = new(StringComparer.Ordinal)
            {
                ["/Quote/Life"] = new WebsitePageDocument
                {
                    Title = "Life",
                    Navigation = new WebsitePageNavigation { Label = "Life", Order = 0 }
                }
            }
        };
        WebsiteSystemTemplateAuthority.Apply(WebsiteEditorSiteKeys.Protect, protect);
        Assert.True(protect.Pages.ContainsKey("/Quote/Life"));
    }

    [Fact]
    public void FastRoadsterBuild_ResolvesWholeSiteInOneBoundedPlan_WithProtectedConversionPath()
    {
        var document = Baseline();
        var actions = Actions();
        var manifest = WebsiteCreativeCapabilityResolver.Resolve(
            WebsiteEditorSiteKeys.Business,
            document,
            actions);

        WebsiteDesignPlanSection Section(
            string recipe,
            string key,
            Dictionary<string, string> content,
            string? capability = null) => new()
            {
                Recipe = recipe,
                Key = key,
                Content = content,
                CapabilityKey = capability
            };

        var plan = new WebsiteDesignPlan
        {
            ArtDirection = "roadster-precision",
            ReplaceBusinessPages = true,
            PrimaryCapabilityKey = "action.business_schedule",
            Pages =
            [
                new WebsiteDesignPlanPage
                {
                    Path = "/",
                    Title = "Precision Home",
                    Description = "A premium, conversion-focused home page.",
                    NavigationLabel = "Home",
                    NavigationOrder = 0,
                    Sections =
                    [
                        Section("hero.cinematic","home.hero",new(StringComparer.Ordinal)
                        {
                            ["eyebrow"]="Built for decisive growth",
                            ["headline"]="A cleaner path from attention to action.",
                            ["body"]="Premium positioning, fast decisions, and one clear next move."
                        },"action.business_schedule"),
                        Section("proof.stats","home.proof",new(StringComparer.Ordinal)
                        {
                            ["headline"]="Clarity you can measure.",
                            ["body"]="Every section earns its place in the journey.",
                            ["stat1"]="Focused message",
                            ["stat2"]="Protected measurement",
                            ["stat3"]="Fast follow-through"
                        }),
                        Section("services.grid","home.services",new(StringComparer.Ordinal)
                        {
                            ["eyebrow"]="Capabilities",
                            ["headline"]="Everything important. Nothing ornamental.",
                            ["body"]="A focused system built to move the right visitor forward.",
                            ["item1"]="Strategy",
                            ["item2"]="Execution",
                            ["item3"]="Optimization"
                        }),
                        Section("cta.closing","home.close",new(StringComparer.Ordinal)
                        {
                            ["eyebrow"]="Next move",
                            ["headline"]="Turn intent into a real conversation.",
                            ["body"]="Choose a time that works and keep momentum moving."
                        },"action.business_schedule")
                    ]
                },
                new WebsiteDesignPlanPage
                {
                    Path = "/services",
                    Title = "Services",
                    Description = "Premium services.",
                    NavigationLabel = "Services",
                    NavigationOrder = 10,
                    Sections =
                    [
                        Section("hero.split","services.hero",new(StringComparer.Ordinal)
                        {
                            ["eyebrow"]="Services",
                            ["headline"]="Built around the outcome, not the noise.",
                            ["body"]="A clear service architecture makes the decision easier."
                        },"action.business_schedule"),
                        Section("services.grid","services.grid",new(StringComparer.Ordinal)
                        {
                            ["eyebrow"]="Core services",
                            ["headline"]="Focused expertise from first move to finish.",
                            ["body"]="Each service supports one coherent growth system.",
                            ["item1"]="Positioning",
                            ["item2"]="Digital execution",
                            ["item3"]="Conversion improvement"
                        }),
                        Section("process.steps","services.process",new(StringComparer.Ordinal)
                        {
                            ["headline"]="Move from idea to execution without drift.",
                            ["step1"]="01 · Clarify the objective",
                            ["step2"]="02 · Build the right system",
                            ["step3"]="03 · Measure and refine"
                        }),
                        Section("cta.closing","services.close",new(StringComparer.Ordinal)
                        {
                            ["eyebrow"]="Start",
                            ["headline"]="Choose the next move.",
                            ["body"]="Book a focused conversation and move forward."
                        },"action.business_schedule")
                    ]
                },
                new WebsiteDesignPlanPage
                {
                    Path = "/about",
                    Title = "About",
                    Description = "How the business works.",
                    NavigationLabel = "About",
                    NavigationOrder = 20,
                    Sections =
                    [
                        Section("hero.cinematic","about.hero",new(StringComparer.Ordinal)
                        {
                            ["eyebrow"]="About",
                            ["headline"]="Precision is a business advantage.",
                            ["body"]="The experience is designed around clarity, trust, and decisive execution."
                        }),
                        Section("feature.split","about.method",new(StringComparer.Ordinal)
                        {
                            ["eyebrow"]="Method",
                            ["headline"]="Simple systems. High standards.",
                            ["body"]="Every decision connects brand, experience, and measurable action."
                        }),
                        Section("cta.closing","about.close",new(StringComparer.Ordinal)
                        {
                            ["eyebrow"]="Work together",
                            ["headline"]="See what a sharper system changes.",
                            ["body"]="Start with one focused conversation."
                        },"action.business_schedule")
                    ]
                },
                new WebsiteDesignPlanPage
                {
                    Path = "/contact",
                    Title = "Contact",
                    Description = "Start a conversation.",
                    NavigationLabel = "Contact",
                    NavigationOrder = 30,
                    PrimaryCapabilityKey = "contact.inquiry.submit",
                    Sections =
                    [
                        Section("hero.cinematic","contact.hero",new(StringComparer.Ordinal)
                        {
                            ["eyebrow"]="Contact",
                            ["headline"]="Start with what you need.",
                            ["body"]="Share the goal and the right next step can follow."
                        }),
                        Section("contact.inquiry","contact.form",new(StringComparer.Ordinal)
                        {
                            ["eyebrow"]="Send a note",
                            ["headline"]="Tell us where you want to go next.",
                            ["body"]="Use the protected inquiry experience below.",
                            ["title"]="Start the conversation",
                            ["submit"]="Send inquiry"
                        },"contact.inquiry.submit")
                    ]
                }
            ]
        };

        var operations = WebsiteDesignPlanResolver.Resolve(
            document,
            WebsiteEditorSiteKeys.Business,
            manifest,
            plan);

        Assert.InRange(operations.Count, 1, 80);
        var result = WebsiteDocumentMutationService.Apply(
            document,
            WebsiteEditorSiteKeys.Business,
            actions,
            manifest,
            operations);

        Assert.Equal(4, result.Document.Pages.Values.Count(page => page.Navigation?.IsDeleted != true));
        Assert.Equal("#07152d", result.Document.Theme.Navy);
        Assert.Equal(72m, result.Document.Theme.DisplaySize);
        Assert.Equal(
            new[] { "home.hero", "home.proof", "home.services", "home.close" },
            result.Document.Pages["/"].Composition.Select(node => node.Id).ToArray());

        var inquiry = WebsiteSiteSource.Flatten(result.Document)
            .Single(entry => entry.Node.Type == "form" &&
                             entry.Node.SystemKey == "canonical_inquiry");
        Assert.Equal("/contact", inquiry.PagePath);

        var quality = WebsiteDesignQualityInspector.Inspect(result.Document, manifest);
        var home = quality.ConversionPaths.Single(path => path.PagePath == "/");
        Assert.True(home.HasEarlyConversionPoint);
        Assert.True(home.TotalConversionPoints >= 2);
        Assert.DoesNotContain(quality.Checks, check =>
            check.Code == "design_placeholder_copy");
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
    public void SafeQualityRepairPlanner_FixesHeadingHierarchy_AndSkipsProtectedSubtrees()
    {
        var document = Baseline();
        var hero = document.Pages["/"].Composition[0];
        var title = hero.Children.Single(value => value.Id == "home.hero.title");
        title.Tag = "h3";

        hero.Children.Add(new WebsiteCompositionNode
        {
            Id = "home.hero.second",
            Type = "heading",
            Tag = "h5",
            Text = "Second heading"
        });
        hero.Children.Add(new WebsiteCompositionNode
        {
            Id = "home.protected",
            Type = "form",
            Tag = "form",
            SystemKey = "canonical_inquiry",
            Children =
            [
                new WebsiteCompositionNode
                {
                    Id = "home.protected.heading",
                    Type = "heading",
                    Tag = "h6",
                    Text = "Runtime-owned heading"
                }
            ]
        });

        var plan = WebsiteDesignQualityRepairPlanner.Plan(document);

        Assert.Contains(plan.Repairs, value =>
            value.ElementId == "home.hero.title" &&
            value.Operation.Node!.Tag == "h1");
        Assert.Contains(plan.Repairs, value =>
            value.ElementId == "home.hero.second" &&
            value.Operation.Node!.Tag == "h2");
        Assert.DoesNotContain(plan.Repairs, value =>
            value.ElementId == "home.protected.heading");
        Assert.All(plan.Operations, operation =>
        {
            Assert.Equal("replaceNode", operation.Type);
            Assert.False(string.IsNullOrWhiteSpace(operation.ExpectedFingerprint));
        });
    }

    [Fact]
    public void DesignQuality_FlagsOrphanedFormActions_AndContactPagesWithoutCapture()
    {
        var document = Baseline();
        document.Pages["/contact"] = new WebsitePageDocument
        {
            Title = "Contact",
            Navigation = new WebsitePageNavigation { Label = "Contact", Order = 20 },
            Composition =
            [
                new WebsiteCompositionNode
                {
                    Id = "contact.hero",
                    Type = "section",
                    Tag = "section",
                    Children =
                    [
                        new WebsiteCompositionNode
                        {
                            Id = "contact.title",
                            Type = "heading",
                            Tag = "h1",
                            Text = "Contact us"
                        },
                        new WebsiteCompositionNode
                        {
                            Id = "contact.start",
                            Type = "cta",
                            Tag = "a",
                            Text = "Get started",
                            ActionKey = "form_start",
                            Href = "#website-form"
                        }
                    ]
                }
            ]
        };
        var manifest = Capabilities(document);

        var report = WebsiteDesignQualityInspector.Inspect(document, manifest);

        Assert.Contains(report.Checks, value =>
            value.Code == "conversion_orphan_form_action" &&
            value.PagePath == "/contact");
        Assert.Contains(report.Checks, value =>
            value.Code == "conversion_contact_capture_missing" &&
            value.PagePath == "/contact");
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
