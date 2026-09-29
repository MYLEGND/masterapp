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
                        Id = "shell.primary-nav",
                        Type = "container",
                        Tag = "nav",
                        ClassName = "nav",
                        SystemKey = "primary_navigation"
                    },
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
        var navigation = parsed.Document.Shell.Header
            .Single(node => node.Id == "shell.primary-nav");
        var shellContact = parsed.Document.Shell.Header
            .Single(node => node.Id == "shell.contact");

        Assert.Equal("business_quote", quote.ActionKey);
        Assert.Single(quote.Signals);
        Assert.Equal("cta_click", quote.Signals[0].EventName);
        Assert.Equal("primary_navigation", navigation.SystemKey);
        Assert.Equal("nav", navigation.Tag);
        Assert.Equal("business_contact", shellContact.ActionKey);
        Assert.Single(shellContact.Signals);
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

        var model = JsonSerializer.Deserialize<WebsiteSiteSourceDocument>(serialized, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
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
    public void SiteSource_ProtectedActionAndFormBackendSemanticsRemainServerOwned()
    {
        var baseline = CanonicalDocument();
        var quoteBaseline = baseline.Pages["/"].Composition[0].Children
            .Single(node => node.Id == "home.hero.quote");
        quoteBaseline.Href = "/contact";
        quoteBaseline.Target = "_self";
        quoteBaseline.DataBinding = new WebsiteDataBinding
        {
            CollectionId = "canonical",
            Field = "name",
            Target = "text"
        };

        var formBaseline = baseline.Pages["/"].Composition[0].Children
            .Single(node => node.Id == "home.hero.form");
        formBaseline.Href = "/api/website-inquiries/public";
        formBaseline.DataBinding = new WebsiteDataBinding
        {
            CollectionId = "canonical",
            Field = "contact",
            Target = "text"
        };

        var serialized = WebsiteSiteSource.Serialize(baseline);
        var model = JsonSerializer.Deserialize<WebsiteSiteSourceDocument>(
            serialized,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var hero = model.Pages.Single(page => page.Path == "/").Composition
            .Single(node => node.Id == "home.hero");
        var quote = hero.Children.Single(node => node.Id == "home.hero.quote");
        var form = hero.Children.Single(node => node.Id == "home.hero.form");

        Assert.Null(quote.Href);
        Assert.Null(quote.Target);
        Assert.Null(quote.DataBinding);
        Assert.Null(form.Href);
        Assert.Null(form.DataBinding);
        Assert.DoesNotContain("/api/website-inquiries/public", serialized, StringComparison.Ordinal);

        quote.Href = "https://untrusted.example/changed";
        quote.Target = "_blank";
        quote.DataBinding = new WebsiteDataBinding
        {
            CollectionId = "forged",
            Field = "forged",
            Target = "href"
        };

        form.Href = "https://untrusted.example/intake";
        form.DataBinding = new WebsiteDataBinding
        {
            CollectionId = "forged",
            Field = "owner",
            Target = "href"
        };

        var proposed = JsonSerializer.Serialize(
            model,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        var parsed = WebsiteSiteSource.Parse(proposed, baseline, BusinessActions());

        var parsedHero = parsed.Document.Pages["/"].Composition
            .Single(node => node.Id == "home.hero");
        var parsedQuote = parsedHero.Children.Single(node => node.Id == "home.hero.quote");
        var parsedForm = parsedHero.Children.Single(node => node.Id == "home.hero.form");

        Assert.Equal("business_quote", parsedQuote.ActionKey);
        Assert.Equal("/contact", parsedQuote.Href);
        Assert.Equal("_self", parsedQuote.Target);
        Assert.Equal("canonical", parsedQuote.DataBinding?.CollectionId);
        Assert.Equal("name", parsedQuote.DataBinding?.Field);

        Assert.Equal("canonical_inquiry", parsedForm.SystemKey);
        Assert.Equal("/api/website-inquiries/public", parsedForm.Href);
        Assert.Equal("canonical", parsedForm.DataBinding?.CollectionId);
        Assert.Equal("contact", parsedForm.DataBinding?.Field);
    }

    [Fact]
    public void CanonicalV3Serialization_ContainsNoRetiredOverrideAuthorities()
    {
        var documentJson = JsonSerializer.Serialize(
            CanonicalDocument(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var sourceJson = WebsiteSiteSource.Serialize(CanonicalDocument());

        foreach (var retired in new[] { "\"elements\"", "\"extras\"", "\"sectionOrder\"", "\"templatePath\"", "\"legacyMigration\"" })
        {
            Assert.DoesNotContain(retired, documentJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(retired, sourceJson, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void SiteSource_CannotMoveRemoveOrInventPrimaryNavigationAuthority()
    {
        var baseline = CanonicalDocument();
        var source = WebsiteSiteSource.Serialize(baseline);
        var model = JsonSerializer.Deserialize<WebsiteSiteSourceDocument>(source, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var navigation = model.Shell.Header.Single(node => node.Id == "shell.primary-nav");
        model.Shell.Header.Remove(navigation);
        model.Pages.Single(page => page.Path == "/").Composition.Add(navigation);
        var moved = JsonSerializer.Serialize(
            model,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        Assert.Throws<ArgumentException>(() =>
            WebsiteSiteSource.Parse(moved, baseline, BusinessActions()));

        model = JsonSerializer.Deserialize<WebsiteSiteSourceDocument>(source, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        model.Shell.Header.RemoveAll(node => node.Id == "shell.primary-nav");
        var removed = JsonSerializer.Serialize(
            model,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        Assert.Throws<ArgumentException>(() =>
            WebsiteSiteSource.Parse(removed, baseline, BusinessActions()));

        model = JsonSerializer.Deserialize<WebsiteSiteSourceDocument>(source, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        model.Pages.Single(page => page.Path == "/").Composition.Add(new WebsiteCompositionNode
        {
            Id = "invented-nav",
            Type = "container",
            Tag = "nav",
            SystemKey = "primary_navigation"
        });
        var invented = JsonSerializer.Serialize(
            model,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        Assert.Throws<ArgumentException>(() =>
            WebsiteSiteSource.Parse(invented, baseline, BusinessActions()));
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
