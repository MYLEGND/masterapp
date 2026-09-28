using System.Text.Json;
using Infrastructure.WebsiteEditing;
using Xunit;

namespace AgentPortal.Tests;

public sealed class WebsiteSiteSourceV3Tests
{
    private static IReadOnlyList<WebsiteCallToActionOption> BusinessActions() =>
        WebsiteCallToActionCatalog.Build(
            WebsiteEditorSiteKeys.Business,
            "3605551212",
            "owner@example.test",
            "https://example.test/book",
            "/store");

    private static WebsiteContentDocument CanonicalDocument()
    {
        var binding = new WebsiteSignalBinding
        {
            Id = Guid.NewGuid().ToString("N"),
            Trigger = "click",
            EventName = "cta_click",
            ActionKey = "cta_click",
            DeliveryMode = "analytics"
        };
        return new WebsiteContentDocument
        {
            CompositionMode = "canonical",
            Elements = new(StringComparer.Ordinal)
            {
                ["shell.contact"] = new WebsiteElementOverride
                {
                    Text = "Contact",
                    ActionKey = "business_contact",
                    Signals = [binding]
                }
            },
            Pages = new(StringComparer.Ordinal)
            {
                ["/"] = new WebsitePageDocument
                {
                    Title = "Home",
                    Description = "Home description",
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
                                    Text = "Original headline"
                                },
                                new WebsiteCompositionNode
                                {
                                    Id = "home.hero.quote",
                                    Type = "cta",
                                    Tag = "a",
                                    Text = "Get a quote",
                                    ActionKey = "business_quote",
                                    Signals = [binding]
                                },
                                new WebsiteCompositionNode
                                {
                                    Id = "home.hero.form",
                                    Type = "form",
                                    Tag = "form",
                                    SystemKey = "canonical_inquiry",
                                    Title = "Contact us"
                                },
                                new WebsiteCompositionNode
                                {
                                    Id = "home.hero.image",
                                    Type = "image",
                                    Tag = "img",
                                    Alt = "Exterior project",
                                    MediaUrl = "/assets/default-project.jpg"
                                }
                            ]
                        }
                    ]
                }
            }
        };
    }

    [Fact]
    public void SiteSource_RoundTripsOneCanonicalGraph_AndRestoresProtectedSignals()
    {
        var source = CanonicalDocument();
        var actions = BusinessActions();
        var serialized = WebsiteSiteSource.Serialize(source);

        Assert.Contains("legend-site-source/v1", serialized, StringComparison.Ordinal);
        Assert.Contains("home.hero.quote", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("cta_click", serialized, StringComparison.Ordinal);

        var parsed = WebsiteSiteSource.Parse(serialized, source, actions);
        var quote = parsed.Document.Pages["/"].Composition[0].Children.Single(node => node.Id == "home.hero.quote");

        Assert.Equal("business_quote", quote.ActionKey);
        Assert.Single(quote.Signals);
        Assert.Equal("cta_click", quote.Signals[0].EventName);
        Assert.Equal("canonical", parsed.Document.CompositionMode);
        Assert.Empty(parsed.Document.Pages["/"].Elements);
        Assert.Empty(parsed.Document.Pages["/"].Extras);
        Assert.Equal(serialized, WebsiteSiteSource.Serialize(parsed.Document));
    }

    [Fact]
    public void SiteSource_CannotInventActionOrSystemAuthority()
    {
        var source = CanonicalDocument();
        var serialized = WebsiteSiteSource.Serialize(source);

        var fakeAction = serialized.Replace(
            "\"actionKey\": \"business_quote\"",
            "\"actionKey\": \"fake_backend_action\"",
            StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => WebsiteSiteSource.Parse(fakeAction, source, BusinessActions()));

        var fakeForm = serialized.Replace(
            "\"systemKey\": \"canonical_inquiry\"",
            "\"systemKey\": \"custom_submit\"",
            StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => WebsiteSiteSource.Parse(fakeForm, source, BusinessActions()));
    }

    [Fact]
    public void SiteSource_NewMediaMustUseScopedAssetIdentity()
    {
        var source = CanonicalDocument();
        var serialized = WebsiteSiteSource.Serialize(source);
        var changed = serialized.Replace(
            "\"mediaUrl\": \"/assets/default-project.jpg\"",
            "\"mediaUrl\": \"https://outside.example/image.jpg\"",
            StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => WebsiteSiteSource.Parse(changed, source, BusinessActions()));
    }

    [Fact]
    public void Sanitizer_RetiresLegacyPageStoresWhenCompositionExists()
    {
        var document = CanonicalDocument();
        document.Pages["/"].TemplatePath = "/";
        document.Pages["/"].Elements["legacy"] = new WebsiteElementOverride { Text = "legacy" };
        document.Pages["/"].Extras.Add(new WebsiteExtraComponent
        {
            Id = "legacy-extra",
            SectionId = "legacy-section",
            Type = "text",
            Text = "legacy"
        });

        var clean = WebsiteContentSanitizer.Sanitize(document);

        Assert.Null(clean.Pages["/"].TemplatePath);
        Assert.Empty(clean.Pages["/"].Elements);
        Assert.Empty(clean.Pages["/"].Extras);
        Assert.NotEmpty(clean.Pages["/"].Composition);
    }

    [Fact]
    public void Sanitizer_PreservesExistingRelativeStaticMediaDuringMaterialization()
    {
        var document = CanonicalDocument();
        var image = document.Pages["/"].Composition[0].Children.Single(node => node.Id == "home.hero.image");
        image.MediaUrl = "/assets/client-hero.webp";

        var clean = WebsiteContentSanitizer.Sanitize(document);
        var preserved = clean.Pages["/"].Composition[0].Children.Single(node => node.Id == "home.hero.image");

        Assert.Equal("/assets/client-hero.webp", preserved.MediaUrl);
    }

    [Fact]
    public void PublishResolution_KeepsActionIdentityAndResolvesDestinationServerSide()
    {
        var document = WebsiteContentSanitizer.Sanitize(CanonicalDocument());
        var error = WebsiteCallToActionCatalog.PrepareForPublish(document, BusinessActions());
        var quote = document.Pages["/"].Composition[0].Children.Single(node => node.Id == "home.hero.quote");

        Assert.Null(error);
        Assert.Equal("business_quote", quote.ActionKey);
        Assert.Equal("/contact", quote.Href);
    }

    [Fact]
    public void AiBuild_UsesOnlyAuthorizedActionsAndMedia()
    {
        var document = WebsiteContentSanitizer.Sanitize(CanonicalDocument());
        var mediaId = Guid.NewGuid();
        var actions = BusinessActions().Select(action => action.Key).ToHashSet(StringComparer.Ordinal);

        var result = WebsiteStudioAiProposalPolicy.Apply(
            document,
            "build",
            "Build services",
            "/",
            null,
            null,
            [
                new WebsiteStudioAiOperation
                {
                    Kind = "create_page",
                    PagePath = "/services",
                    Title = "Services",
                    NavigationLabel = "Services"
                },
                new WebsiteStudioAiOperation
                {
                    Kind = "add_section",
                    PagePath = "/services",
                    NodeId = "services.main"
                },
                new WebsiteStudioAiOperation
                {
                    Kind = "add_node",
                    PagePath = "/services",
                    ParentId = "services.main",
                    NodeId = "services.quote",
                    NodeType = "cta",
                    Tag = "a",
                    Text = "Get a free quote",
                    ActionKey = "business_quote"
                },
                new WebsiteStudioAiOperation
                {
                    Kind = "add_node",
                    PagePath = "/services",
                    ParentId = "services.main",
                    NodeId = "services.image",
                    NodeType = "image",
                    Tag = "img",
                    MediaAssetId = mediaId,
                    Alt = "Completed exterior project"
                }
            ],
            actions,
            new HashSet<Guid> { mediaId });

        var page = result.ProposedDocument.Pages["/services"];
        Assert.Equal("Services", page.Navigation.Label);
        var section = Assert.Single(page.Composition);
        Assert.Contains(section.Children, node => node.Id == "services.quote" && node.ActionKey == "business_quote");
        Assert.Contains(section.Children, node => node.Id == "services.image" && node.MediaAssetId == mediaId);
        Assert.False(document.Pages.ContainsKey("/services"));
    }

    [Fact]
    public void AiBuild_CannotDeleteWiredCtaOrInventAction()
    {
        var document = WebsiteContentSanitizer.Sanitize(CanonicalDocument());
        var actions = BusinessActions().Select(action => action.Key).ToHashSet(StringComparer.Ordinal);

        Assert.Throws<ArgumentException>(() => WebsiteStudioAiProposalPolicy.Apply(
            document, "build", "bad", "/", null, null,
            [new WebsiteStudioAiOperation { Kind = "delete_node", NodeId = "home.hero.quote" }],
            actions, new HashSet<Guid>()));

        Assert.Throws<ArgumentException>(() => WebsiteStudioAiProposalPolicy.Apply(
            document, "build", "bad", "/", null, null,
            [new WebsiteStudioAiOperation { Kind = "set_action", NodeId = "home.hero.quote", ActionKey = "invented" }],
            actions, new HashSet<Guid>()));
    }

    [Fact]
    public void SelectionAi_CannotEscapeSelectedSubtree()
    {
        var document = WebsiteContentSanitizer.Sanitize(CanonicalDocument());
        var actions = BusinessActions().Select(action => action.Key).ToHashSet(StringComparer.Ordinal);

        Assert.Throws<ArgumentException>(() => WebsiteStudioAiProposalPolicy.Apply(
            document, "selection", "edit selection", "/", "home.hero.title", "home.hero",
            [new WebsiteStudioAiOperation { Kind = "set_text", NodeId = "home.hero.quote", Text = "Changed" }],
            actions, new HashSet<Guid>()));
    }
}
