using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Infrastructure.WebsiteEditing.Controllers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentPortal.Tests;

public sealed class WebsitePublishingAuthorityTests
{
    private sealed class Fixture : IDisposable
    {
        public MasterAppDbContext Db { get; } = new(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public WebsiteEditorTicketProtector Tickets { get; } = new(new EphemeralDataProtectionProvider());
        public IConfiguration Config { get; } = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new System.Collections.Generic.KeyValuePair<string,string?>(
                    "Founder:Oid",
                    "1d43fa52-e36d-4522-9d21-40b3ac260aed")
            })
            .Build();

        public WebsitePlatformController Controller => new(Db, Tickets, Config);
        public string Token => Tickets.Protect(new(
            WebsiteEditorSiteKeys.Legend,
            WebsiteEditorSiteKeys.GlobalOwnerKey,
            null,
            true,
            DateTime.UtcNow.AddMinutes(10),
            ActorUserId: "1d43fa52-e36d-4522-9d21-40b3ac260aed"));

        public void Dispose()
        {
            Db.Dispose();
            Tickets.Dispose();
        }
    }

    private static JsonElement Body(IActionResult result) =>
        JsonSerializer.SerializeToElement(
            Assert.IsType<OkObjectResult>(result).Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static WebsiteContentDocument Document(string text) => new()
    {
        Pages = new(StringComparer.Ordinal)
        {
            ["/"] = new WebsitePageDocument
            {
                Title = "Home",
                Description = "Home",
                Navigation = new WebsitePageNavigation
                {
                    Label = "Home",
                    ShowInNavigation = true,
                    Order = 0
                },
                Composition =
                [
                    new WebsiteCompositionNode
                    {
                        Id = "home.title",
                        Type = "heading",
                        Tag = "h1",
                        Text = text
                    }
                ]
            }
        }
    };

    private static JsonElement Page(JsonElement envelope, string path = "/") =>
        envelope.GetProperty("document").GetProperty("pages").GetProperty(path);

    private static string? TitleText(JsonElement envelope) =>
        Page(envelope).GetProperty("composition")[0].GetProperty("text").GetString();

    [Fact]
    public async Task AgentContractUsesCompleteScopedCatalogs_WithoutDependingOnPlacedInstances()
    {
        using var f = new Fixture();
        var payload = Body(await f.Controller.Manage(f.Token));
        var contract = payload.GetProperty("agentContract");
        var actions = payload.GetProperty("ctaCatalog").GetProperty("options");
        Assert.True(actions.GetArrayLength() > 0);
        Assert.Equal(actions.GetRawText(), contract.GetProperty("availableActions").GetRawText());
        Assert.Equal(payload.GetProperty("signalCatalog").GetRawText(), contract.GetProperty("signalCatalog").GetRawText());
        var prompt = contract.GetProperty("promptTemplate").GetString()!;
        foreach (var action in actions.EnumerateArray())
            Assert.Contains(action.GetProperty("key").GetString()!, prompt, StringComparison.Ordinal);
        Assert.Contains("removing an instance never deletes the catalog capability", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProtectRepairSurvivesCanonicalSave_AndRejectsAnotherRoutesRuntime()
    {
        using var f = new Fixture();
        f.Db.AgentTrackingProfiles.Add(new Domain.Entities.AgentTrackingProfile
        {
            AgentUserId = "1d43fa52-e36d-4522-9d21-40b3ac260aed", AgentUpn = "founder@example.test",
            Slug = "repair-agent", Status = "Active"
        });
        await f.Db.SaveChangesAsync();
        var token = f.Tickets.Protect(new(WebsiteEditorSiteKeys.Protect,
            "1d43fa52-e36d-4522-9d21-40b3ac260aed", "repair-agent", true, DateTime.UtcNow.AddMinutes(10),
            ActorUserId: "1d43fa52-e36d-4522-9d21-40b3ac260aed"));
        var draft = Document("Home");
        draft.Pages["/Quote/Life"] = new WebsitePageDocument
        {
            Composition = [new WebsiteCompositionNode { Id = "life.runtime", Type = "container", Tag = "div",
                SystemKey = "protect_runtime_form:quote_life",
                FieldLabels = new() { ["FirstName"] = "Your first name" } }]
        };
        var saved = Body(await f.Controller.Save(new(token, draft, 0)));
        Assert.Equal("protect_runtime_form:quote_life", Page(saved, "/Quote/Life").GetProperty("composition")[0].GetProperty("systemKey").GetString());
        Body(await f.Controller.Publish(new(token, 1)));
        draft.Pages["/Quote/Life"].Composition[0].SystemKey = "protect_runtime_form:quote_home_form";
        Assert.IsType<BadRequestObjectResult>(await f.Controller.Save(new(token, draft, 2)));
        Assert.Equal(2, Body(await f.Controller.Manage(token)).GetProperty("revision").GetInt64());
    }

    [Fact]
    public async Task RollbackRunsCurrentPreflight_AndCannotRepublishBrokenHistoricalContent()
    {
        using var f = new Fixture();
        var token = f.Token;
        Body(await f.Controller.Save(new(token, Document("good"), 0)));
        var published = Body(await f.Controller.Publish(new(token, 1))).GetProperty("versionId").GetGuid();
        var state = await f.Db.Set<Domain.Entities.WebsiteContentState>().SingleAsync();
        var broken = Document("broken");
        broken.Pages["/"].Composition.Add(new WebsiteCompositionNode { Id = "bad.action", Type = "cta", Tag = "a", ActionKey = "not_in_catalog" });
        var old = new Domain.Entities.WebsiteContentVersion { StateId = state.Id,
            DocumentJson = JsonSerializer.Serialize(broken, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CompiledPagesJson = "stale compiled output", Revision = 0 };
        f.Db.Add(old);
        await f.Db.SaveChangesAsync();
        Assert.IsType<BadRequestObjectResult>(await f.Controller.Rollback(new(token, 2, old.Id)));
        Assert.Equal(published, state.PublishedVersionId);
        Assert.Equal(2, state.Revision);
        Assert.Equal("good", TitleText(Body(await f.Controller.Public("legend"))));
    }

    [Fact]
    public async Task DraftRemainsPrivateUntilPublish_AndStaleWriterCannotOverwrite()
    {
        using var f = new Fixture();
        var token = f.Token;

        Assert.Equal(0, Body(await f.Controller.Manage(token)).GetProperty("revision").GetInt64());
        Assert.Equal(1, Body(await f.Controller.Save(new(token, Document("draft"), 0))).GetProperty("revision").GetInt64());

        var beforePublish = Body(await f.Controller.Public("legend"))
            .GetProperty("document")
            .GetProperty("pages");
        Assert.False(beforePublish.TryGetProperty("/", out _));

        Assert.IsType<ConflictObjectResult>(await f.Controller.Save(new(token, Document("stale"), 0)));

        Body(await f.Controller.Publish(new(token, 1)));
        Assert.Equal("draft", TitleText(Body(await f.Controller.Public("legend"))));
    }

    [Fact]
    public async Task RollbackRestoresPublishedSnapshot_AndMakesItCurrentDraft()
    {
        using var f = new Fixture();
        var token = f.Token;

        Body(await f.Controller.Save(new(token, Document("first"), 0)));
        var first = Body(await f.Controller.Publish(new(token, 1))).GetProperty("versionId").GetGuid();
        Body(await f.Controller.Save(new(token, Document("second"), 2)));
        Body(await f.Controller.Publish(new(token, 3)));
        Body(await f.Controller.Save(new(token, Document("unfinished"), 4)));
        Body(await f.Controller.Rollback(new(token, 5, first)));

        Assert.Equal("first", TitleText(Body(await f.Controller.Public("legend"))));
        Assert.Equal("first", TitleText(Body(await f.Controller.Manage(token))));
    }

    [Fact]
    public async Task ForgedFounderFlagAndLegacyActorlessTicketCannotAuthorize()
    {
        using var f = new Fixture();
        var legacy = f.Tickets.Protect(new(
            "legend",
            WebsiteEditorSiteKeys.GlobalOwnerKey,
            null,
            true,
            DateTime.UtcNow.AddMinutes(5)));
        Assert.IsType<UnauthorizedResult>(await f.Controller.Manage(legacy));

        var other = f.Tickets.Protect(new(
            "legend",
            WebsiteEditorSiteKeys.GlobalOwnerKey,
            null,
            true,
            DateTime.UtcNow.AddMinutes(5),
            ActorUserId: Guid.NewGuid().ToString()));
        Assert.IsType<UnauthorizedResult>(await f.Controller.Manage(other));
    }

    [Fact]
    public void EditorContentPreservesIntentionalWhitespaceAndStructuredCardCopy()
    {
        var doc = Document("  First\tline\r\nSecond  line  ");
        doc.Pages["/"].Composition.Add(new WebsiteCompositionNode
        {
            Id = "service-one",
            Type = "container",
            Tag = "article",
            Title = "  Window\tCleaning  ",
            Text = "Line one\r\n\tLine two"
        });

        var result = WebsiteContentSanitizer.Sanitize(doc);
        var title = result.Pages["/"].Composition.Single(node => node.Id == "home.title");
        var card = result.Pages["/"].Composition.Single(node => node.Id == "service-one");

        Assert.Equal("  First\tline\nSecond  line  ", title.Text);
        Assert.Equal("container", card.Type);
        Assert.Equal("  Window\tCleaning  ", card.Title);
        Assert.Equal("Line one\n\tLine two", card.Text);
    }

    [Fact]
    public async Task CanonicalSaveIsExactReplacement_AndNeverResurrectsOmittedNodes()
    {
        using var f = new Fixture();
        var token = f.Token;

        Body(await f.Controller.Save(new(token, Document("persisted"), 0)));

        var replacement = new WebsiteContentDocument
        {
            Pages = new(StringComparer.Ordinal)
            {
                ["/"] = new WebsitePageDocument
                {
                    Title = "Home",
                    Navigation = new WebsitePageNavigation
                    {
                        Label = "Home",
                        ShowInNavigation = true
                    },
                    Composition = []
                }
            }
        };

        var saved = Body(await f.Controller.Save(new(token, replacement, 1)));
        Assert.Empty(Page(saved).GetProperty("composition").EnumerateArray());
        Assert.Equal(2, saved.GetProperty("revision").GetInt64());
    }

    [Fact]
    public void CanonicalInquiryRejectsManualVerifiedOutcome_AndLegacyJsonIsReadOnlyOnly()
    {
        var bindingId = Guid.NewGuid().ToString("N");
        var doc = new WebsiteContentDocument
        {
            Pages = new(StringComparer.Ordinal)
            {
                ["/"] = new WebsitePageDocument
                {
                    Composition =
                    [
                        new WebsiteCompositionNode
                        {
                            Id = "contact-form",
                            Type = "form",
                            Tag = "form",
                            SystemKey = "canonical_inquiry",
                            Title = "Contact us",
                            Text = "Send inquiry",
                            Signals =
                            [
                                new WebsiteSignalBinding
                                {
                                    Id = bindingId,
                                    Trigger = "submission_saved",
                                    EventName = "Lead",
                                    DeliveryMode = "meta"
                                }
                            ]
                        }
                    ]
                }
            }
        };

        Assert.Throws<ArgumentException>(() => WebsiteContentSanitizer.Sanitize(doc));

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var legacyJson =
            $$"""
            {
              "version": 2,
              "pages": {
                "/": {
                  "title": "Home",
                  "navigation": { "label": "Home", "showInNavigation": true },
                  "elements": {},
                  "sectionOrder": {},
                  "extras": [{
                    "id": "contact-form",
                    "sectionId": "contact",
                    "type": "form",
                    "title": "Contact us",
                    "text": "Send inquiry",
                    "signals": [{
                      "id": "{{bindingId}}",
                      "trigger": "submission_saved",
                      "eventName": "Lead",
                      "deliveryMode": "meta"
                    }]
                  }]
                }
              }
            }
            """;

        var historical = WebsiteContentSanitizer.ReadPersisted(legacyJson, options);
        Assert.Equal(WebsiteStudioContract.CurrentDocumentVersion, historical.Version);
        Assert.Throws<InvalidOperationException>(() => WebsiteContentSanitizer.Sanitize(historical));
        Assert.Contains(bindingId, legacyJson, StringComparison.Ordinal);
    }

    [Fact]
    public void VisibleCopyCannotChangeManagedActionOrCanonicalBehavior()
    {
        var options = WebsiteCallToActionCatalog.Build(
            WebsiteEditorSiteKeys.Business,
            bookingUrl: "https://book.example.test/meeting");
        var action = Assert.Single(options.Where(x => x.Key == "business_schedule"));

        Assert.Equal("cta_click", action.BehaviorKey);
        Assert.Equal("cta_click", action.AnalyticsEventName);

        var document = new WebsiteContentDocument
        {
            Pages = new(StringComparer.Ordinal)
            {
                ["/"] = new WebsitePageDocument
                {
                    Composition =
                    [
                        new WebsiteCompositionNode
                        {
                            Id = "stable-button",
                            Type = "cta",
                            Tag = "a",
                            ActionKey = action.Key,
                            Text = "Payment completed",
                            Href = "https://untrusted.example"
                        }
                    ]
                }
            }
        };

        Assert.Null(WebsiteCallToActionCatalog.PrepareForPublish(document, options));
        var button = document.Pages["/"].Composition.Single();

        Assert.Equal(action.Key, button.ActionKey);
        Assert.Equal(action.Href, button.Href);
        Assert.Equal("Payment completed", button.Text);
        Assert.Contains(options, x => x.Key == "form_start" && x.RuntimeAction == "focus_form");
        Assert.Contains(options, x => x.Key == "submit" && x.RuntimeAction == "submit_form");
        Assert.All(options, x => Assert.False(
            Shared.Analytics.AnalyticsEventCatalog.TryGetBehavior(x.BehaviorKey, out var behavior) &&
            behavior.RequiresServerAuthority));
    }

    [Fact]
    public void ScopeCtaCatalogOnlyOffersConfiguredDynamicActions()
    {
        var business = WebsiteCallToActionCatalog.Build(
            WebsiteEditorSiteKeys.Business,
            phone: "(602) 555-0199",
            email: "hello@example.test",
            bookingUrl: "https://book.example.test/meeting");

        Assert.Contains(business, option => option.Key == "business_call" && option.Href == "tel:6025550199" &&
            option.AnalyticsEventName == "cta_click" && option.MetaIntentEventName == null);
        Assert.Contains(business, option => option.Key == "business_email" && option.Href == "mailto:hello@example.test" &&
            option.MetaIntentEventName == null);
        Assert.Contains(business, option => option.Key == "business_schedule" &&
            option.Href.StartsWith("https://book.example.test/", StringComparison.Ordinal) &&
            option.MetaIntentEventName == null);
        Assert.Contains(business, option => option.Key == "business_quote" && option.Href == "/contact" &&
            option.AnalyticsEventName == "cta_click" && option.MetaIntentEventName == null);

        var contact = Assert.Single(business.Where(option => option.Key == "business_contact"));
        Assert.Equal("Contact", contact.Group);
        Assert.Contains("Contact Us", contact.TextVariants!);
        Assert.Contains("Get in Touch", contact.TextVariants!);

        var quote = Assert.Single(business.Where(option => option.Key == "business_quote"));
        Assert.Equal("Quote", quote.Group);
        Assert.Contains("Free Quote", quote.TextVariants!);
        Assert.Contains("Get a Free Quote", quote.TextVariants!);

        var call = Assert.Single(business.Where(option => option.Key == "business_call"));
        Assert.Equal("Call", call.Group);
        Assert.Contains("Call Now", call.TextVariants!);

        var schedule = Assert.Single(business.Where(option => option.Key == "business_schedule"));
        Assert.Equal("Schedule", schedule.Group);
        Assert.Contains("Book Now", schedule.TextVariants!);

        var protect = WebsiteCallToActionCatalog.Build(WebsiteEditorSiteKeys.Protect);
        Assert.Contains(protect, option => option.Key == "protect_quote" && option.Href == "/Quote" &&
            option.AnalyticsEventName == "quote_click");
        Assert.Contains(protect, option => option.Href == "/Quote/Life" &&
            option.AnalyticsEventName == "quote_click");
        Assert.DoesNotContain(protect, option => option.Key == "protect_call");
        Assert.DoesNotContain(protect, option => option.Key == "protect_schedule");
    }

    [Fact]
    public async Task PublishRejectsDeadCta_AndResolvesManagedAction()
    {
        using var f = new Fixture();
        var token = f.Token;

        var dead = Document("Home");
        dead.Pages["/"].Composition.Add(new WebsiteCompositionNode
        {
            Id = "dead",
            Type = "cta",
            Tag = "a",
            Text = "Dead",
            Href = "#"
        });

        Assert.Equal(1, Body(await f.Controller.Save(new(token, dead, 0))).GetProperty("revision").GetInt32());
        Assert.IsType<BadRequestObjectResult>(await f.Controller.Publish(new(token, 1)));

        var managed = Document("Home");
        managed.Pages["/"].Composition.Add(new WebsiteCompositionNode
        {
            Id = "managed",
            Type = "cta",
            Tag = "a",
            Text = "Talk",
            ActionKey = "legend_contact",
            Href = "#"
        });

        Assert.Equal(2, Body(await f.Controller.Save(new(token, managed, 1))).GetProperty("revision").GetInt32());
        Assert.IsType<OkObjectResult>(await f.Controller.Publish(new(token, 2)));

        var published = Page(Body(await f.Controller.Public("legend")));
        var button = published.GetProperty("composition").EnumerateArray()
            .Single(value => value.GetProperty("id").GetString() == "managed");

        Assert.Equal("/contact", button.GetProperty("href").GetString());
        Assert.Equal("legend_contact", button.GetProperty("actionKey").GetString());
    }

    [Fact]
    public void ControlsRejectExecutableUrlsAndUnsafeStylePayloads()
    {
        var doc = Document("safe");
        var node = doc.Pages["/"].Composition.Single();
        node.Type = "link";
        node.Tag = "a";
        node.Href = "javascript:alert(1)";
        node.Style.BackgroundColor = "url(https://evil.invalid)";

        var result = WebsiteContentSanitizer.Sanitize(doc).Pages["/"].Composition.Single();

        Assert.Null(result.Href);
        Assert.Null(result.Style.BackgroundColor);
        Assert.Null(WebsiteContentSanitizer.SanitizeUrl("https://example.com/?legendEdit=secret"));
    }
}
