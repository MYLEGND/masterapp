using System;
using System.Collections.Generic;
using System.Linq;
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
            Shell = new WebsiteSharedShellDocument
            {
                Header =
                [
                    new WebsiteCompositionNode
                    {
                        Id = "shell.contact",
                        Type = "cta",
                        Tag = "a",
                        Text = "Contact",
                        ActionKey = "business_contact",
                        Signals = [binding]
                    }
                ]
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
        Assert.Contains("shell.contact", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("cta_click", serialized, StringComparison.Ordinal);

        var parsed = WebsiteSiteSource.Parse(serialized, source, actions);
        var quote = parsed.Document.Pages["/"].Composition[0].Children
            .Single(node => node.Id == "home.hero.quote");
        var shell = Assert.Single(parsed.Document.Shell.Header);

        Assert.Equal("business_quote", quote.ActionKey);
        Assert.Single(quote.Signals);
        Assert.Equal("cta_click", quote.Signals[0].EventName);
        Assert.Equal("business_contact", shell.ActionKey);
        Assert.Single(shell.Signals);
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
        Assert.Throws<ArgumentException>(() =>
            WebsiteSiteSource.Parse(fakeAction, source, BusinessActions()));

        var fakeForm = serialized.Replace(
            "\"systemKey\": \"canonical_inquiry\"",
            "\"systemKey\": \"custom_submit\"",
            StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() =>
            WebsiteSiteSource.Parse(fakeForm, source, BusinessActions()));
    }

    [Fact]
    public void SiteSource_CannotRetargetOrRemoveExistingCanonicalAction()
    {
        var source = CanonicalDocument();
        var serialized = WebsiteSiteSource.Serialize(source);

        var retargeted = serialized.Replace(
            "\"actionKey\": \"business_quote\"",
            "\"actionKey\": \"business_contact\"",
            StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() =>
            WebsiteSiteSource.Parse(retargeted, source, BusinessActions()));

        var model = JsonSerializer.Deserialize<WebsiteSiteSourceDocument>(serialized)!;
        var hero = model.Pages.Single(page => page.Path == "/").Composition
            .Single(node => node.Id == "home.hero");
        hero.Children.RemoveAll(node => node.Id == "home.hero.quote");

        var removed = JsonSerializer.Serialize(
            model,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });

        Assert.Throws<ArgumentException>(() =>
            WebsiteSiteSource.Parse(removed, source, BusinessActions()));
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

        Assert.Throws<ArgumentException>(() =>
            WebsiteSiteSource.Parse(changed, source, BusinessActions()));
    }

    [Fact]
    public void Sanitizer_PreservesExistingRelativeStaticMediaDuringMaterialization()
    {
        var document = CanonicalDocument();
        var image = document.Pages["/"].Composition[0].Children
            .Single(node => node.Id == "home.hero.image");
        image.MediaUrl = "/assets/client-hero.webp";

        var clean = WebsiteContentSanitizer.Sanitize(document);
        var preserved = clean.Pages["/"].Composition[0].Children
            .Single(node => node.Id == "home.hero.image");

        Assert.Equal("/assets/client-hero.webp", preserved.MediaUrl);
    }

    [Fact]
    public void PublishResolution_KeepsActionIdentityAndResolvesDestinationServerSide()
    {
        var document = WebsiteContentSanitizer.Sanitize(CanonicalDocument());
        var error = WebsiteCallToActionCatalog.PrepareForPublish(document, BusinessActions());
        var quote = document.Pages["/"].Composition[0].Children
            .Single(node => node.Id == "home.hero.quote");

        Assert.Null(error);
        Assert.Equal("business_quote", quote.ActionKey);
        Assert.Equal("/contact", quote.Href);
    }

    [Fact]
    public void SiteSource_DoesNotExposeProviderOrServerOutcomeAuthority()
    {
        var serialized = WebsiteSiteSource.Serialize(CanonicalDocument());

        Assert.DoesNotContain("meta", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("openai", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("QualifiedLead", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("PolicyIssued", serialized, StringComparison.Ordinal);
    }
}
