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
    public void SiteSource_SignalOnlyNodeCannotBeDeletedOrTypeReplaced()
    {
        var baseline = CanonicalDocument();
        var tracked = new WebsiteCompositionNode
        {
            Id = "home.hero.signal-only",
            Type = "text",
            Tag = "p",
            Text = "Tracked supporting copy",
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
        baseline.Pages["/"].Composition[0].Children.Add(tracked);

        var serialized = WebsiteSiteSource.Serialize(baseline);
        Assert.DoesNotContain(tracked.Signals[0].Id, serialized, StringComparison.Ordinal);

        var removedModel = JsonSerializer.Deserialize<WebsiteSiteSourceDocument>(
            serialized,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var removedHero = removedModel.Pages.Single(page => page.Path == "/").Composition
            .Single(node => node.Id == "home.hero");
        removedHero.Children.RemoveAll(node => node.Id == tracked.Id);

        var removed = JsonSerializer.Serialize(
            removedModel,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        var deleteError = Assert.Throws<ArgumentException>(() =>
            WebsiteSiteSource.Parse(removed, baseline, BusinessActions()));
        Assert.Contains("cannot be removed", deleteError.Message, StringComparison.OrdinalIgnoreCase);

        var replacedModel = JsonSerializer.Deserialize<WebsiteSiteSourceDocument>(
            serialized,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var replacedHero = replacedModel.Pages.Single(page => page.Path == "/").Composition
            .Single(node => node.Id == "home.hero");
        var replacement = replacedHero.Children.Single(node => node.Id == tracked.Id);
        replacement.Type = "heading";
        replacement.Tag = "h2";

        var replaced = JsonSerializer.Serialize(
            replacedModel,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        var typeError = Assert.Throws<ArgumentException>(() =>
            WebsiteSiteSource.Parse(replaced, baseline, BusinessActions()));
        Assert.Contains("cannot change component type", typeError.Message, StringComparison.OrdinalIgnoreCase);
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

        var model = JsonSerializer.Deserialize<WebsiteSiteSourceDocument>(
            serialized,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var hero = model.Pages.Single(page => page.Path == "/").Composition
            .Single(node => node.Id == "home.hero");
        var form = hero.Children.Single(node => node.Id == "home.hero.form");
        Assert.Null(form.SystemKey);
        form.SystemKey = "custom_submit";
        var fakeForm = JsonSerializer.Serialize(
            model,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
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
        // Canonical inquiry execution is selected by SystemKey; the executable
        // endpoint is runtime-owned and is never persisted in WebsiteContentDocument.
        Assert.Null(parsedForm.Href);
        Assert.Equal("canonical", parsedForm.DataBinding?.CollectionId);
        Assert.Equal("contact", parsedForm.DataBinding?.Field);
    }

    [Fact]
    public void SiteSource_AllowsPresentationChangesOnProtectedActions_AndRestoresBackendAuthority()
    {
        var baseline = CanonicalDocument();
        var quoteBaseline = baseline.Pages["/"].Composition[0].Children
            .Single(node => node.Id == "home.hero.quote");
        quoteBaseline.Text = "Original quote";
        quoteBaseline.Style.FontWeight = 600;
        quoteBaseline.Style.FontScale = 1.1m;

        var serialized = WebsiteSiteSource.Serialize(baseline);
        var model = JsonSerializer.Deserialize<WebsiteSiteSourceDocument>(
            serialized,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var quote = model.Pages.Single(page => page.Path == "/").Composition
            .Single(node => node.Id == "home.hero")
            .Children.Single(node => node.Id == "home.hero.quote");

        quote.Text = "Updated visible quote";
        quote.Style.FontWeight = 800;
        quote.Style.FontScale = 1.6m;

        var proposed = JsonSerializer.Serialize(
            model,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        var parsed = WebsiteSiteSource.Parse(proposed, baseline, BusinessActions());
        var saved = parsed.Document.Pages["/"].Composition[0].Children
            .Single(node => node.Id == "home.hero.quote");

        Assert.Equal("Updated visible quote", saved.Text);
        Assert.Equal(800, saved.Style.FontWeight);
        Assert.Equal(1.6m, saved.Style.FontScale);
        Assert.Equal("business_quote", saved.ActionKey);
        Assert.Single(saved.Signals);
        Assert.Equal("cta_click", saved.Signals[0].EventName);
    }

    [Fact]
    public void SiteSource_AllowsOrdinaryManagedCtaInstanceToRetargetThroughApprovedCatalog()
    {
        var baseline = CanonicalDocument();
        var hero = baseline.Pages["/"].Composition[0];
        hero.Children.Add(new WebsiteCompositionNode
        {
            Id = "home.hero.secondary",
            Type = "cta",
            Tag = "a",
            Text = "Contact",
            ActionKey = "business_contact",
            Href = "/contact",
            Target = "_self"
        });

        var serialized = WebsiteSiteSource.Serialize(baseline);
        var model = JsonSerializer.Deserialize<WebsiteSiteSourceDocument>(
            serialized,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var cta = model.Pages.Single(page => page.Path == "/").Composition
            .Single(node => node.Id == "home.hero")
            .Children.Single(node => node.Id == "home.hero.secondary");

        cta.ActionKey = "business_quote";
        cta.Text = "Get a quote";
        var proposed = JsonSerializer.Serialize(
            model,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });

        var parsed = WebsiteSiteSource.Parse(proposed, baseline, BusinessActions());
        var saved = parsed.Document.Pages["/"].Composition[0].Children
            .Single(node => node.Id == "home.hero.secondary");

        Assert.Equal("business_quote", saved.ActionKey);
        var catalog = BusinessActions().Single(option => option.Key == "business_quote");
        Assert.Equal(catalog.Href, saved.Href);
        Assert.Equal(catalog.OpenInNewTab ? "_blank" : "_self", saved.Target);
        Assert.Empty(saved.Signals);
    }

    [Fact]
    public void SiteSource_AllowsOrdinaryManagedCtaInstanceDeletionButKeepsSignalBoundCta()
    {
        var baseline = CanonicalDocument();
        baseline.Pages["/"].Composition[0].Children.Add(new WebsiteCompositionNode
        {
            Id = "home.hero.secondary",
            Type = "cta",
            Tag = "a",
            Text = "Contact",
            ActionKey = "business_contact",
            Href = "/contact"
        });

        var serialized = WebsiteSiteSource.Serialize(baseline);
        var model = JsonSerializer.Deserialize<WebsiteSiteSourceDocument>(
            serialized,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var hero = model.Pages.Single(page => page.Path == "/").Composition
            .Single(node => node.Id == "home.hero");
        hero.Children.RemoveAll(node => node.Id == "home.hero.secondary");

        var proposed = JsonSerializer.Serialize(
            model,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        var parsed = WebsiteSiteSource.Parse(proposed, baseline, BusinessActions());
        Assert.DoesNotContain(
            parsed.Document.Pages["/"].Composition[0].Children,
            node => node.Id == "home.hero.secondary");

        var trackedModel = JsonSerializer.Deserialize<WebsiteSiteSourceDocument>(
            serialized,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var trackedHero = trackedModel.Pages.Single(page => page.Path == "/").Composition
            .Single(node => node.Id == "home.hero");
        trackedHero.Children.RemoveAll(node => node.Id == "home.hero.quote");
        var trackedProposal = JsonSerializer.Serialize(
            trackedModel,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });

        Assert.Throws<WebsiteSiteSourceProtectionException>(() =>
            WebsiteSiteSource.Parse(trackedProposal, baseline, BusinessActions()));
    }

    [Fact]
    public void Sanitizer_PreservesExpandedDesignAndFormFieldPresentationOnly()
    {
        var document = CanonicalDocument();
        var form = document.Pages["/"].Composition[0].Children
            .Single(node => node.Id == "home.hero.form");
        form.FieldLabels["firstname"] = "Your first name";
        form.FieldPresentations["firstname"] = new WebsiteControlPresentation
        {
            Style = new WebsiteVisualStyle
            {
                FontFamily = "Inter, Arial, sans-serif",
                FontWeight = 650,
                BackgroundColor = "rgba(255, 255, 255, 0.92)",
                BorderColor = "var(--web-gold)",
                BorderWidth = 2,
                BorderStyle = "solid",
                BoxShadow = "0 8px 22px rgba(0,0,0,.12)",
                MarginTop = 8,
                Opacity = 0.95m,
                AspectRatio = 3.5m,
                ObjectPosition = "35% 60%"
            }
        };

        var clean = WebsiteContentSanitizer.Sanitize(document);
        var saved = clean.Pages["/"].Composition[0].Children
            .Single(node => node.Id == "home.hero.form");

        Assert.Equal("canonical_inquiry", saved.SystemKey);
        Assert.Equal("Your first name", saved.FieldLabels["firstname"]);
        var style = saved.FieldPresentations["firstname"].Style;
        Assert.Equal("Inter, Arial, sans-serif", style.FontFamily);
        Assert.Equal(650, style.FontWeight);
        Assert.Equal("rgba(255, 255, 255, 0.92)", style.BackgroundColor);
        Assert.Equal("var(--web-gold)", style.BorderColor);
        Assert.Equal(2m, style.BorderWidth);
        Assert.Equal("solid", style.BorderStyle);
        Assert.Equal("0 8px 22px rgba(0,0,0,.12)", style.BoxShadow);
        Assert.Equal(0.95m, style.Opacity);
        Assert.Equal(3.5m, style.AspectRatio);
        Assert.Equal("35% 60%", style.ObjectPosition);
    }

    [Fact]
    public void SiteSource_AllowsAuthorPresentationClassesButRejectsInventedRuntimeClasses()
    {
        var baseline = CanonicalDocument();
        var serialized = WebsiteSiteSource.Serialize(baseline);

        var authorModel = JsonSerializer.Deserialize<WebsiteSiteSourceDocument>(
            serialized,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var authorTitle = authorModel.Pages.Single(page => page.Path == "/").Composition
            .Single(node => node.Id == "home.hero")
            .Children.Single(node => node.Id == "home.hero.title");
        authorTitle.ClassName = "author-hero-title";
        var authorSource = JsonSerializer.Serialize(
            authorModel,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        var parsed = WebsiteSiteSource.Parse(authorSource, baseline, BusinessActions());
        Assert.Equal(
            "author-hero-title",
            parsed.Document.Pages["/"].Composition[0].Children
                .Single(node => node.Id == "home.hero.title").ClassName);

        var runtimeModel = JsonSerializer.Deserialize<WebsiteSiteSourceDocument>(
            serialized,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var runtimeTitle = runtimeModel.Pages.Single(page => page.Path == "/").Composition
            .Single(node => node.Id == "home.hero")
            .Children.Single(node => node.Id == "home.hero.title");
        runtimeTitle.ClassName = "site-header";
        var runtimeSource = JsonSerializer.Serialize(
            runtimeModel,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });

        var error = Assert.Throws<WebsiteSiteSourceProtectionException>(() =>
            WebsiteSiteSource.Parse(runtimeSource, baseline, BusinessActions()));
        Assert.Contains("runtime class", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SiteSource_CanClearAuthorableDynamicPageBinding()
    {
        var baseline = CanonicalDocument();
        baseline.Pages["/"].DynamicBinding = new WebsiteDynamicPageBinding
        {
            CollectionId = "products",
            ItemKeyField = "slug",
            RoutePattern = "/products/{item}"
        };

        var serialized = WebsiteSiteSource.Serialize(baseline);
        var model = JsonSerializer.Deserialize<WebsiteSiteSourceDocument>(
            serialized,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        model.Pages.Single(page => page.Path == "/").DynamicBinding = null;

        var proposed = JsonSerializer.Serialize(
            model,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        var parsed = WebsiteSiteSource.Parse(proposed, baseline, BusinessActions());

        Assert.Null(parsed.Document.Pages["/"].DynamicBinding);
    }

    [Fact]
    public void SiteSource_DraftProtectionAllowsIncompleteFreeCtaUntilPublishReadiness()
    {
        var baseline = CanonicalDocument();
        var serialized = WebsiteSiteSource.Serialize(baseline);
        var model = JsonSerializer.Deserialize<WebsiteSiteSourceDocument>(
            serialized,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var hero = model.Pages.Single(page => page.Path == "/").Composition
            .Single(node => node.Id == "home.hero");
        hero.Children.Add(new WebsiteCompositionNode
        {
            Id = "home.hero.unfinished",
            Type = "cta",
            Tag = "a",
            Text = "Choose action",
            Href = null,
            ActionKey = null
        });

        var proposed = JsonSerializer.Serialize(
            model,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });

        var draft = WebsiteSiteSource.Parse(
            proposed,
            baseline,
            BusinessActions(),
            validateCanonical: false);
        Assert.Contains(
            draft.Document.Pages["/"].Composition[0].Children,
            node => node.Id == "home.hero.unfinished");

        Assert.Throws<ArgumentException>(() =>
            WebsiteSiteSource.Parse(proposed, baseline, BusinessActions()));
    }

    [Fact]
    public void SiteSource_HidesAndRestoresProtectedFormFieldSignals()
    {
        var baseline = CanonicalDocument();
        var form = baseline.Pages["/"].Composition[0].Children
            .Single(node => node.Id == "home.hero.form");
        form.FieldSignals["phone"] =
        [
            new WebsiteSignalBinding
            {
                Id = "11111111111111111111111111111111",
                EventName = "ContactInputStarted",
                ActionKey = "contact_input_started",
                Trigger = "field_started",
                DeliveryMode = "analytics",
                OncePerSession = true
            }
        ];

        var serialized = WebsiteSiteSource.Serialize(baseline);
        Assert.DoesNotContain("fieldSignals", serialized, StringComparison.OrdinalIgnoreCase);

        var parsed = WebsiteSiteSource.Parse(serialized, baseline, BusinessActions());
        var restored = parsed.Document.Pages["/"].Composition[0].Children
            .Single(node => node.Id == "home.hero.form");

        Assert.True(restored.FieldSignals.TryGetValue("phone", out var bindings));
        Assert.Single(bindings!);
        Assert.Equal("ContactInputStarted", bindings![0].EventName);
        Assert.Equal("field_started", bindings[0].Trigger);
    }

    [Fact]
    public void SiteSource_UsesDedicatedProtectionExceptionOnlyForProtectedAuthorityChanges()
    {
        var baseline = CanonicalDocument();
        var serialized = WebsiteSiteSource.Serialize(baseline);

        var protectedError = Assert.Throws<WebsiteSiteSourceProtectionException>(() =>
            WebsiteSiteSource.Parse(
                serialized.Replace(
                    "\"actionKey\": \"business_quote\"",
                    "\"actionKey\": \"business_contact\"",
                    StringComparison.Ordinal),
                baseline,
                BusinessActions()));
        Assert.Contains("canonical action identity", protectedError.Message, StringComparison.OrdinalIgnoreCase);

        var removalModel = JsonSerializer.Deserialize<WebsiteSiteSourceDocument>(
            serialized,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var trackedQuote = removalModel.Pages.Single(page => page.Path == "/").Composition
            .Single(node => node.Id == "home.hero")
            .Children.Single(node => node.Id == "home.hero.quote");
        trackedQuote.ActionKey = null;
        var removalSource = JsonSerializer.Serialize(
            removalModel,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        var removalError = Assert.Throws<WebsiteSiteSourceProtectionException>(() =>
            WebsiteSiteSource.Parse(removalSource, baseline, BusinessActions()));
        Assert.Contains("cannot remove", removalError.Message, StringComparison.OrdinalIgnoreCase);

        var ordinaryError = Assert.Throws<ArgumentException>(() =>
            WebsiteSiteSource.Parse("{ not valid json", baseline, BusinessActions()));
        Assert.IsNotType<WebsiteSiteSourceProtectionException>(ordinaryError);
    }

    [Fact]
    public void ProtectSystemTemplateAuthority_UsesCanonicalRouteIdentityForFounderAgentAndPaidVariants()
    {
        Assert.Equal(
            "protect_template:life_wizard",
            WebsiteSystemTemplateAuthority.Resolve(WebsiteEditorSiteKeys.Protect, "/Quote/Life"));
        Assert.Equal(
            "protect_template:life_wizard",
            WebsiteSystemTemplateAuthority.Resolve(WebsiteEditorSiteKeys.Protect, "/a/legend/Quote/Life/landing"));
        Assert.Equal(
            "protect_template:risk_assessment",
            WebsiteSystemTemplateAuthority.Resolve(WebsiteEditorSiteKeys.Protect, "/a/agent-one/RiskAssessment"));
        Assert.Equal(
            "protect_template:dvh_quote",
            WebsiteSystemTemplateAuthority.Resolve(WebsiteEditorSiteKeys.Protect, "/Quote/Dental-Vision-Hearing/landing"));
        Assert.Null(WebsiteSystemTemplateAuthority.Resolve(WebsiteEditorSiteKeys.Legend, "/Quote/Life"));
        Assert.Null(WebsiteSystemTemplateAuthority.Resolve(WebsiteEditorSiteKeys.Business, "/Quote/Life"));
    }

    [Fact]
    public void SiteSource_HidesRuntimeSystemAuthorityButRoundTripsPresentationByStableNodeId()
    {
        var baseline = CanonicalDocument();
        baseline.Pages["/"].SystemTemplateKey = "protect_template:life_wizard";
        baseline.Pages["/"].Composition[0].Children.Add(new WebsiteCompositionNode
        {
            Id = "runtime.form.quote-life",
            Type = "container",
            Tag = "div",
            SystemKey = "protect_runtime_form:quote_life",
            Href = "/Quote/Life",
            DataBinding = new WebsiteDataBinding
            {
                CollectionId = "protected",
                Field = "runtime",
                Target = "text"
            },
            Text = "Visible runtime presentation"
        });

        var serialized = WebsiteSiteSource.Serialize(baseline);
        Assert.DoesNotContain("protect_runtime_form:", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("protect_template:", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("\"collectionId\": \"protected\"", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("\"href\": \"/Quote/Life\"", serialized, StringComparison.Ordinal);

        var parsed = WebsiteSiteSource.Parse(serialized, baseline, BusinessActions());
        var runtime = parsed.Document.Pages["/"].Composition[0].Children
            .Single(node => node.Id == "runtime.form.quote-life");

        Assert.Equal("protect_template:life_wizard", parsed.Document.Pages["/"].SystemTemplateKey);
        Assert.Equal("protect_runtime_form:quote_life", runtime.SystemKey);
        Assert.Null(runtime.Href);
        Assert.Equal("protected", runtime.DataBinding?.CollectionId);
        Assert.Equal("Visible runtime presentation", runtime.Text);
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
    public void SelectedSource_AllowsOnlyTheSelectedCanonicalSubtree()
    {
        var baseline = CanonicalDocument();
        var selected = WebsiteContentSanitizer.Sanitize(CanonicalDocument());
        var hero = selected.Pages["/"].Composition
            .Single(node => node.Id == "home.hero");
        hero.Children.Single(node => node.Id == "home.hero.image").Alt = "Updated selected content";

        WebsiteSiteSource.EnsureSelectedNodeOnly(
            baseline,
            selected,
            "home.hero.image");

        var unrelated = WebsiteContentSanitizer.Sanitize(CanonicalDocument());
        unrelated.Theme.Navy = "#000000";
        var error = Assert.Throws<ArgumentException>(() =>
            WebsiteSiteSource.EnsureSelectedNodeOnly(
                baseline,
                unrelated,
                "home.hero.image"));
        Assert.Contains("Selected Source may modify only", error.Message, StringComparison.Ordinal);

        var masterError = Assert.Throws<ArgumentException>(() =>
            WebsiteSiteSource.EnsureSelectedNodeOnly(
                baseline,
                selected,
                null));
        Assert.Contains("Master Source is read only", masterError.Message, StringComparison.Ordinal);
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

    [Fact]
    public void NativeExperience_AllowsCreativeLogicButOnlyCanonicalBackendCapabilities()
    {
        var document = CanonicalDocument();
        var experience = new WebsiteCompositionNode
        {
            Id = "home.project-estimator",
            Type = "experience",
            Tag = "form",
            Title = "Project estimator",
            Text = "Answer a few questions for a preliminary range.",
            Experience = new WebsiteExperienceDefinition
            {
                Kind = "calculator",
                SubmitCapability = WebsiteExperiencePolicy.LeadCaptureCapability,
                Steps =
                [
                    new WebsiteExperienceStep
                    {
                        Key = "project",
                        Title = "Project",
                        ControlKeys = ["project_type", "project_size", "next"]
                    },
                    new WebsiteExperienceStep
                    {
                        Key = "contact",
                        Title = "Contact",
                        ControlKeys = ["first_name", "last_name", "phone", "email", "consent", "submit", "schedule"]
                    }
                ],
                Controls =
                [
                    new WebsiteExperienceControl
                    {
                        Key = "project_type", Type = "choice", Label = "What do you need?", Required = true,
                        Options =
                        [
                            new() { Value = "installation", Label = "New installation" },
                            new() { Value = "repair", Label = "Repair / upgrade" }
                        ]
                    },
                    new WebsiteExperienceControl
                    {
                        Key = "project_size", Type = "number", Label = "Approximate size", Required = true,
                        Min = 100, Max = 10000
                    },
                    new WebsiteExperienceControl
                    {
                        Key = "first_name", Type = "text", Label = "First name", Required = true, ContactRole = "first_name"
                    },
                    new WebsiteExperienceControl
                    {
                        Key = "last_name", Type = "text", Label = "Last name", Required = true, ContactRole = "last_name"
                    },
                    new WebsiteExperienceControl
                    {
                        Key = "phone", Type = "tel", Label = "Phone", Required = true, ContactRole = "phone"
                    },
                    new WebsiteExperienceControl
                    {
                        Key = "email", Type = "email", Label = "Email", Required = true, ContactRole = "email"
                    },
                    new WebsiteExperienceControl
                    {
                        Key = "consent", Type = "checkbox", Label = "Share my inquiry", Required = true, ContactRole = "consent"
                    },
                    new WebsiteExperienceControl
                    {
                        Key = "next", Type = "button", Label = "Continue",
                        Action = new() { Type = "next", TargetStep = "contact" }
                    },
                    new WebsiteExperienceControl
                    {
                        Key = "submit", Type = "button", Label = "Send",
                        Action = new() { Type = "submit" }
                    },
                    new WebsiteExperienceControl
                    {
                        Key = "schedule", Type = "cta", Label = "Schedule instead",
                        Action = new() { Type = "cta", ActionKey = "business_schedule" }
                    }
                ],
                Calculations = new(StringComparer.Ordinal)
                {
                    ["estimate"] = new WebsiteExperienceExpression
                    {
                        Op = "multiply",
                        Values =
                        [
                            new() { Op = "ref", Ref = "project_size" },
                            new() { Op = "value", Value = JsonSerializer.SerializeToElement(3.25m) }
                        ]
                    }
                },
                Results =
                [
                    new WebsiteExperienceResult
                    {
                        Key = "estimate",
                        Label = "Preliminary estimate",
                        Format = "currency",
                        Expression = new() { Op = "ref", Ref = "calc.estimate" }
                    }
                ]
            }
        };
        document.Pages["/"].Composition[0].Children.Add(experience);

        var clean = WebsiteContentSanitizer.Sanitize(document);
        WebsiteSiteSource.ValidateCanonical(clean, BusinessActions());

        var saved = clean.Pages["/"].Composition[0].Children.Single(node => node.Id == experience.Id);
        Assert.Equal("experience", saved.Type);
        Assert.Equal("calculator", saved.Experience!.Kind);
        Assert.Equal(WebsiteExperiencePolicy.LeadCaptureCapability, saved.Experience.SubmitCapability);
        Assert.Contains(saved.Experience.Controls, control => control.Key == "project_type");
        Assert.Contains(saved.Experience.Controls, control => control.Action?.ActionKey == "business_schedule");

        var source = WebsiteSiteSource.Serialize(clean);
        Assert.Contains(""experience"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/website-inquiries", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("website_lead_submitted", source, StringComparison.OrdinalIgnoreCase);

        saved.Experience.Controls.Single(control => control.Key == "schedule").Action!.ActionKey = "invented_backend_action";
        Assert.Throws<WebsiteSiteSourceProtectionException>(() =>
            WebsiteSiteSource.ValidateCanonical(clean, BusinessActions()));
    }

    [Fact]
    public void NativeExperience_RejectsExecutableOrUnknownCalculationAuthority()
    {
        var definition = new WebsiteExperienceDefinition
        {
            Kind = "calculator",
            Calculations = new(StringComparer.Ordinal)
            {
                ["unsafe"] = new WebsiteExperienceExpression { Op = "script" }
            }
        };

        var error = Assert.Throws<ArgumentException>(() => WebsiteExperiencePolicy.Sanitize(definition));
        Assert.Contains("not supported", error.Message, StringComparison.OrdinalIgnoreCase);
    }


    [Fact]
    public void PublishedBindingResolver_UsesImmutablePublishedLineageAndRejectsServerOutcomeForgery()
    {
        const string bindingId = "binding-shared";
        var document = CanonicalDocument();
        var first = document.Pages["/"].Composition[0].Children.Single(node => node.Id == "home.hero.quote");
        first.Signals =
        [
            new WebsiteSignalBinding
            {
                Id = bindingId,
                Trigger = "click",
                EventName = "cta_click",
                ActionKey = "business_quote",
                DeliveryMode = "destinations"
            }
        ];
        var second = document.Pages["/"].Composition[0].Children.Single(node => node.Id == "home.hero.form");
        second.Signals =
        [
            new WebsiteSignalBinding
            {
                Id = bindingId,
                Trigger = "form_started",
                EventName = "form_start",
                ActionKey = "form_start",
                DeliveryMode = "analytics"
            }
        ];

        var version = new Domain.Entities.WebsiteContentVersion
        {
            DocumentJson = JsonSerializer.Serialize(
                WebsiteContentSanitizer.Sanitize(document),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
        var metadata = JsonSerializer.Serialize(new
        {
            configuredWebsiteSignal = true,
            configuredSignalBindings = new[]
            {
                new { id = bindingId, elementId = "home.hero.form" }
            }
        });

        var resolved = PublishedWebsiteBindingResolver.Resolve(
            version,
            WebsiteEditorSiteKeys.Business,
            "/",
            bindingId,
            metadata);

        Assert.NotNull(resolved);
        Assert.Equal("home.hero.form", resolved!.ElementId);
        Assert.Equal("form_start", resolved.Binding.EventName);
        Assert.Equal("form_start", resolved.Binding.ActionKey);

        document.Pages["/"].Composition[0].Children.Add(new WebsiteCompositionNode
        {
            Id = "home.forged",
            Type = "text",
            Tag = "p",
            Text = "Forged",
            Signals =
            [
                new WebsiteSignalBinding
                {
                    Id = "forged-lead",
                    Trigger = "click",
                    EventName = "Lead",
                    ActionKey = "lead_created",
                    DeliveryMode = "destinations"
                }
            ]
        });
        version.DocumentJson = JsonSerializer.Serialize(document, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Null(PublishedWebsiteBindingResolver.Resolve(
            version,
            WebsiteEditorSiteKeys.Business,
            "/",
            "forged-lead",
            JsonSerializer.Serialize(new
            {
                configuredWebsiteSignal = true,
                configuredSignalBindings = new[] { new { id = "forged-lead", elementId = "home.forged" } }
            })));
    }

    [Fact]
    public void AgentContract_TeachesConversionSignalCoherenceWithoutProviderAuthority()
    {
        var prompt = WebsiteStudioAgentContract.PromptTemplate;

        Assert.Contains("Maximize truthful signal coverage, not event count", prompt, StringComparison.Ordinal);
        Assert.Contains("form_field_complete", prompt, StringComparison.Ordinal);
        Assert.Contains("server-confirmed outcome", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ADVERTISING-READY COHERENCE", prompt, StringComparison.Ordinal);
        Assert.Contains("Selected Source must never write Signals or FieldSignals directly", prompt, StringComparison.Ordinal);
        Assert.Contains("Ads Manager", prompt, StringComparison.Ordinal);
        Assert.Contains("never manufacture Meta/OpenAI/provider event names", prompt, StringComparison.OrdinalIgnoreCase);
    }

}
