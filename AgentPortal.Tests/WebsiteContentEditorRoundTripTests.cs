using System.Threading;
using System.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ProtectWebsite.Services;
using System.Text.Json;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Infrastructure.WebsiteEditing.Controllers;
using Infrastructure.WebsitePublishing;
using Xunit;

namespace AgentPortal.Tests;

// In-process controller + JSON + EF InMemory coverage. These tests do not prove
// SQL behavior, HTTP middleware/model binding, or authenticated live deployment.
public sealed class WebsiteContentEditorRoundTripTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string ElementId = "home.h1.title";

    private static WebsiteContentDocument CanonicalDocument(string text = "Heading")
    {
        return new WebsiteContentDocument
        {
            Pages = new(StringComparer.Ordinal)
            {
                ["/"] = new WebsitePageDocument
                {
                    Title = "Home",
                    Description = "Home",
                    Navigation = new WebsitePageNavigation { Label = "Home", ShowInNavigation = true, Order = 0 },
                    Composition =
                    [
                        new WebsiteCompositionNode
                        {
                            Id = ElementId,
                            Type = "heading",
                            Tag = "h1",
                            Text = text
                        }
                    ]
                }
            }
        };
    }

    private static WebsiteCompositionNode Node(WebsiteContentDocument document, string id = ElementId)
    {
        WebsiteCompositionNode? Find(IEnumerable<WebsiteCompositionNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.Id == id) return node;
                var child = Find(node.Children);
                if (child is not null) return child;
            }
            return null;
        }

        foreach (var page in document.Pages.Values)
        {
            var found = Find(page.Composition);
            if (found is not null) return found;
        }
        throw new InvalidOperationException("Expected website node was not found.");
    }


    [Theory]
    [InlineData(WebsiteEditorSiteKeys.Legend)]
    [InlineData(WebsiteEditorSiteKeys.Protect)]
    [InlineData(WebsiteEditorSiteKeys.Business)]
    public async Task NamedDrafts_CreateUpdateLoadDelete_KeepPublishedContentIsolated(string siteKey)
    {
        using var fixture = new Fixture(siteKey);
        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));
        var document = CanonicalDocument("Black variation");
        await SeedCanonicalDraftAsync(fixture, ticket, document, revision: 0);

        Assert.IsType<OkObjectResult>(await fixture.CreateController().ApplyMutations(
            new WebsitePlatformController.WebsiteMutationRequest(ticket, 0, [], null, "Black")));
        fixture.Db.ChangeTracker.Clear();
        var state = Assert.Single(await fixture.Db.Set<WebsiteContentState>().ToListAsync());
        var draft = Assert.Single(JsonSerializer.Deserialize<List<WebsiteNamedDraft>>(state.NamedDraftsJson, JsonOptions)!);
        Assert.Null(state.PublishedVersionId);

        var current = ReadDocument(await fixture.CreateController().Manage(ticket));
        var second = ReplaceNodeOperation(current, node => node.Text = "Second variation");
        Assert.IsType<OkObjectResult>(await fixture.CreateController().ApplyMutations(
            new WebsitePlatformController.WebsiteMutationRequest(ticket, 1, [second], null, "Second")));
        fixture.Db.ChangeTracker.Clear();
        state = Assert.Single(await fixture.Db.Set<WebsiteContentState>().ToListAsync());
        Assert.Equal(2, JsonSerializer.Deserialize<List<WebsiteNamedDraft>>(state.NamedDraftsJson, JsonOptions)!.Count);

        Assert.IsType<ConflictObjectResult>(await fixture.CreateController().LoadDraft(new(ticket, 1, draft.Id)));
        Assert.IsType<OkObjectResult>(await fixture.CreateController().LoadDraft(new(ticket, 2, draft.Id)));
        Assert.Equal("Black variation", Node(ReadDocument(await fixture.CreateController().Manage(ticket))).Text);

        current = ReadDocument(await fixture.CreateController().Manage(ticket));
        var updated = ReplaceNodeOperation(current, node => node.Text = "Updated black");
        Assert.IsType<OkObjectResult>(await fixture.CreateController().ApplyMutations(
            new WebsitePlatformController.WebsiteMutationRequest(ticket, 3, [updated], draft.Id, "Black")));
        Assert.IsType<NotFoundResult>(await fixture.CreateController().DeleteDraft(new(ticket, 4, Guid.NewGuid())));
        Assert.IsType<OkObjectResult>(await fixture.CreateController().DeleteDraft(new(ticket, 4, draft.Id)));
        fixture.Db.ChangeTracker.Clear();
        state = Assert.Single(await fixture.Db.Set<WebsiteContentState>().ToListAsync());
        Assert.Single(JsonSerializer.Deserialize<List<WebsiteNamedDraft>>(state.NamedDraftsJson, JsonOptions)!);
        Assert.Equal("Updated black", Node(ReadDocument(await fixture.CreateController().Manage(ticket))).Text);
        Assert.Null(state.PublishedVersionId);
    }


    [Theory]
    [InlineData(WebsiteEditorSiteKeys.Legend)]
    [InlineData(WebsiteEditorSiteKeys.Protect)]
    [InlineData(WebsiteEditorSiteKeys.Business)]
    public async Task RouteKeyedEditorPage_PreservesContentAndMetadataAcrossSaveAndReload(string siteKey)
    {
        using var fixture = new Fixture(siteKey);
        var document = new WebsiteContentDocument
        {
            FaviconImageDataUrl = "https://cdn.example.test/favicon.png",
            Pages = new(StringComparer.Ordinal)
            {
                ["/"] = new WebsitePageDocument
                {
                    Title = "Our business",
                    Description = "Our services",
                    Navigation = new WebsitePageNavigation { Label = "Home", ShowInNavigation = true, Order = 0 },
                    Composition =
                    [
                        new WebsiteCompositionNode { Id = ElementId, Type = "heading", Tag = "h1", Text = "Saved page content" },
                        new WebsiteCompositionNode { Id = "new-section", Type = "section", Tag = "section" }
                    ]
                },
                ["/about"] = new WebsitePageDocument
                {
                    Title = "About us",
                    Navigation = new WebsitePageNavigation { Label = "About", ShowInNavigation = true, Order = 10 }
                }
            }
        };

        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));
        var saved = await SeedCanonicalDraftAsync(fixture, ticket, document, revision: 1);
        Assert.Equal("Saved page content", Node(saved).Text);
        Assert.Equal(document.FaviconImageDataUrl, saved.FaviconImageDataUrl);

        fixture.Db.ChangeTracker.Clear();
        var reloaded = ReadDocument(await fixture.CreateController().Manage(ticket));
        Assert.Equal("Our business", reloaded.Pages["/"].Title);
        Assert.Equal("Our services", reloaded.Pages["/"].Description);
        Assert.Equal("About us", reloaded.Pages["/about"].Title);
        Assert.Equal(document.FaviconImageDataUrl, reloaded.FaviconImageDataUrl);
        Assert.Equal(2, reloaded.Pages["/"].Composition.Count);
    }


    [Theory]
    [InlineData(WebsiteEditorSiteKeys.Legend)]
    [InlineData(WebsiteEditorSiteKeys.Protect)]
    [InlineData(WebsiteEditorSiteKeys.Business)]
    public async Task LargeFractionalAdjustments_SurviveSaveAndReloadThroughBothReadPaths(string siteKey)
    {
        using var fixture = new Fixture(siteKey);
        var document = CanonicalDocument("Updated title");
        Node(document).Style = new WebsiteVisualStyle
        {
            FontScale = 12.75m,
            WidthPercent = 250.25m,
            PaddingTop = 500.5m,
            PaddingBottom = 800.125m,
            TextAlign = "start"
        };

        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));
        var saved = await SeedCanonicalDraftAsync(fixture, ticket, document, revision: 1);
        AssertLargeStyle(Node(saved).Style);

        fixture.Db.ChangeTracker.Clear();
        var reloaded = fixture.CreateController();
        AssertLargeStyle(Node(ReadDocument(await reloaded.Manage(ticket))).Style);

        var unpublished = await reloaded.Public(
            siteKey,
            siteKey == WebsiteEditorSiteKeys.Protect ? Fixture.AgentSlug : null,
            fixture.BusinessId);
        if (siteKey == WebsiteEditorSiteKeys.Business) Assert.IsType<NotFoundObjectResult>(unpublished);
        else Assert.IsType<OkObjectResult>(unpublished);

        Assert.IsType<OkObjectResult>(await reloaded.Publish(new(ticket, 1)));
        fixture.Db.ChangeTracker.Clear();
        AssertLargeStyle(Node(ReadDocument(await fixture.CreateController().Public(
            siteKey,
            siteKey == WebsiteEditorSiteKeys.Protect ? Fixture.AgentSlug : null,
            fixture.BusinessId))).Style);

        var row = Assert.Single(await fixture.Db.Set<WebsiteContentState>().ToListAsync());
        Assert.Equal(siteKey, row.SiteKey);
        Assert.NotNull(row.PublishedVersionId);
        Assert.Single(await fixture.Db.Set<WebsiteContentVersion>().ToListAsync());
        Assert.Empty(await fixture.Db.AgentFinanceToolStates.ToListAsync());

        var current = ReadDocument(await fixture.CreateController().Manage(ticket));
        var unpublishedEdit = ReplaceNodeOperation(current, node => node.Text = "Unpublished revision");
        Assert.IsType<OkObjectResult>(await fixture.CreateController().ApplyMutations(
            new WebsitePlatformController.WebsiteMutationRequest(ticket, 2, [unpublishedEdit])));
        Assert.IsType<ConflictObjectResult>(await fixture.CreateController().ApplyMutations(
            new WebsitePlatformController.WebsiteMutationRequest(ticket, 2, [
                new WebsiteMutationOperation { Type = "setTheme", Theme = new WebsiteDesignTheme { Navy = "#000000" } }
            ])));

        fixture.Db.ChangeTracker.Clear();
        Assert.Equal("Updated title", Node(ReadDocument(await fixture.CreateController().Public(
            siteKey,
            siteKey == WebsiteEditorSiteKeys.Protect ? Fixture.AgentSlug : null,
            fixture.BusinessId))).Text);
        Assert.Equal("Unpublished revision", Node(ReadDocument(await fixture.CreateController().Manage(ticket))).Text);
    }

    [Fact]
    public async Task BusinessPublication_PreservesUtf8AcrossDotNetNodeCompilerTransport()
    {
        using var fixture = new Fixture(WebsiteEditorSiteKeys.Business);
        const string unicode = "Locally Owned · Lynden, WA — Café ® “clean”";
        var document = new WebsiteContentDocument();
        document.Pages["/"] = new WebsitePageDocument { Title = unicode, Description = unicode };
        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));

        await SeedCanonicalDraftAsync(fixture, ticket, document, revision: 1);
        Assert.IsType<OkObjectResult>(await fixture.CreateController().Publish(new(ticket, 1)));

        fixture.Db.ChangeTracker.Clear();
        var version = Assert.Single(await fixture.Db.Set<WebsiteContentVersion>().AsNoTracking().ToListAsync());
        Assert.False(string.IsNullOrWhiteSpace(version.CompiledPagesJson));
        using var compiled = JsonDocument.Parse(version.CompiledPagesJson!);
        var html = compiled.RootElement.GetProperty("pages").GetProperty("/").GetProperty("html").GetString();
        Assert.NotNull(html);
        Assert.Contains(unicode, version.CompiledPagesJson!, StringComparison.Ordinal);
        Assert.DoesNotContain("Â", version.CompiledPagesJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Ã", version.CompiledPagesJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(WebsiteEditorSiteKeys.Legend)]
    [InlineData(WebsiteEditorSiteKeys.Protect)]
    public async Task PublicFavicon_FollowsPublishedWebsiteOnly(string siteKey)
    {
        using var fixture = new Fixture(siteKey);
        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));
        const string first = "https://masterapp-protect.azurewebsites.net/api/website-content/media/11111111-1111-1111-1111-111111111111";
        const string second = "https://masterapp-protect.azurewebsites.net/api/website-content/media/22222222-2222-2222-2222-222222222222";
        var firstId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        fixture.Db.Set<WebsiteMediaAsset>().AddRange(
            new WebsiteMediaAsset
            {
                Id = firstId,
                OwnerKey = fixture.OwnerKey,
                SourceUrl = "favicon-one.png",
                Sha256 = new string('d', 64),
                StorageKey = "website/favicon-one.png",
                ContentType = "image/png",
                SizeBytes = 1000
            },
            new WebsiteMediaAsset
            {
                Id = secondId,
                OwnerKey = fixture.OwnerKey,
                SourceUrl = "favicon-two.png",
                Sha256 = new string('e', 64),
                StorageKey = "website/favicon-two.png",
                ContentType = "image/png",
                SizeBytes = 1000
            });
        await fixture.Db.SaveChangesAsync();

        var document = new WebsiteContentDocument { FaviconImageDataUrl = first };
        var slug = siteKey == WebsiteEditorSiteKeys.Protect ? Fixture.AgentSlug : null;

        await SeedCanonicalDraftAsync(fixture, ticket, document, revision: 1);
        var unpublished = Assert.IsType<RedirectResult>(await fixture.CreateController().PublicFavicon(siteKey, slug));
        Assert.EndsWith("/images/favicon/legend-favicon.svg", unpublished.Url, StringComparison.Ordinal);

        Assert.IsType<OkObjectResult>(await fixture.Controller.Publish(new(ticket, 1)));
        fixture.Db.ChangeTracker.Clear();
        var published = Assert.IsType<RedirectResult>(await fixture.CreateController().PublicFavicon(siteKey, slug));
        Assert.Equal(first, published.Url);

        Assert.IsType<OkObjectResult>(await fixture.CreateController().ApplyMutations(
            new WebsitePlatformController.WebsiteMutationRequest(ticket, 2, [
                new WebsiteMutationOperation { Type = "setFavicon", FaviconImageDataUrl = second }
            ])));
        fixture.Db.ChangeTracker.Clear();
        var stillPublished = Assert.IsType<RedirectResult>(await fixture.CreateController().PublicFavicon(siteKey, slug));
        Assert.Equal(first, stillPublished.Url);
    }


    [Fact]
    public async Task InvalidNumericDomains_AreDiscardedWithoutInventingReplacementStyles()
    {
        using var fixture = new Fixture(WebsiteEditorSiteKeys.Legend);
        var document = CanonicalDocument();
        Node(document).Style = new WebsiteVisualStyle
        {
            FontScale = 0,
            WidthPercent = -1,
            PaddingTop = -2,
            PaddingBottom = -3
        };

        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));
        await SeedCanonicalDraftAsync(fixture, ticket, document, revision: 1);
        AssertNoAdjustments(Node(ReadDocument(await fixture.CreateController().Manage(ticket))).Style);

        var current = ReadDocument(await fixture.CreateController().Manage(ticket));
        var styleEdit = ReplaceNodeOperation(current, node => node.Style = new WebsiteVisualStyle
        {
            FontScale = 0.05m,
            WidthPercent = 0.25m,
            PaddingTop = 0,
            PaddingBottom = 0
        });
        var accepted = Node(await ApplyMutationsAndReadAsync(fixture, ticket, 1, [styleEdit])).Style;
        Assert.Equal(0.05m, accepted.FontScale);
        Assert.Equal(0.25m, accepted.WidthPercent);
        Assert.Equal(0m, accepted.PaddingTop);
        Assert.Equal(0m, accepted.PaddingBottom);
    }


    [Fact]
    public async Task TextOnlyEdit_LeavesEveryStyleUnsetAfterPersistence()
    {
        using var fixture = new Fixture(WebsiteEditorSiteKeys.Legend);
        var document = CanonicalDocument("New heading without resizing");
        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));

        await SeedCanonicalDraftAsync(fixture, ticket, document, revision: 1);

        var element = Node(ReadDocument(await fixture.CreateController().Manage(ticket)));
        Assert.Equal("New heading without resizing", element.Text);
        Assert.Null(element.Hidden);
        AssertNoAdjustments(element.Style);
        Assert.Null(element.Style.TextAlign);
        Assert.Null(element.Style.ObjectPosition);
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidOrExpiredTicket_CannotReadOrMutateSavedContent(bool expired)
    {
        using var fixture = new Fixture(WebsiteEditorSiteKeys.Legend);
        var initial = CanonicalDocument("Preserved content");
        var validTicket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));

        await SeedCanonicalDraftAsync(fixture, validTicket, initial, revision: 1);
        var originalJson = (await fixture.Db.Set<WebsiteContentState>().SingleAsync()).DraftJson;
        var unauthorizedEdit = ReplaceNodeOperation(
            ReadDocument(await fixture.CreateController().Manage(validTicket)),
            node => node.Text = "Unauthorized replacement");

        var rejectedTicket = expired
            ? fixture.Ticket(DateTime.UtcNow.AddMinutes(-1))
            : "not-a-protected-ticket";

        Assert.IsType<UnauthorizedResult>(await fixture.Controller.Manage(rejectedTicket));
        Assert.IsType<UnauthorizedResult>(await fixture.CreateController().ApplyMutations(
            new WebsitePlatformController.WebsiteMutationRequest(rejectedTicket, 1, [unauthorizedEdit])));

        fixture.Db.ChangeTracker.Clear();
        var row = Assert.Single(await fixture.Db.Set<WebsiteContentState>().ToListAsync());
        Assert.Equal(originalJson, row.DraftJson);
    }

    [Fact]
    public async Task BusinessTicket_AllowsLinkedAgentOnlyWhileClientRemainsShared()
    {
        using var fixture = new Fixture(WebsiteEditorSiteKeys.Business);
        var profile = Assert.Single(await fixture.Db.ClientProfiles.ToListAsync());
        profile.AccountManagementMode = ClientAccountManagementModes.SharedAccount;
        fixture.Db.AgentClients.Add(new AgentClient
        {
            AgentUserId = "agent-shared-website",
            AgentUpn = "agent-shared@mylegnd.com",
            ClientUserId = profile.ClientUserId
        });
        await fixture.Db.SaveChangesAsync();

        var ticket = fixture.TicketForActor(
            "agent-shared-website",
            "agent-shared@mylegnd.com",
            DateTime.UtcNow.AddMinutes(10));
        Assert.IsType<OkObjectResult>(await fixture.Controller.Manage(ticket));

        profile.AccountManagementMode = ClientAccountManagementModes.SelfManaged;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        Assert.IsType<UnauthorizedResult>(await fixture.CreateController().Manage(ticket));
    }


    [Fact]
    public async Task CmsCollectionProjection_ReturnsOnlyActiveProductsFromAuthorizedBusiness()
    {
        using var fixture = new Fixture(WebsiteEditorSiteKeys.Business);
        var businessId = fixture.BusinessId!.Value;
        var foreignBusinessId = Guid.NewGuid();
        fixture.Db.CommerceBusinesses.Add(new CommerceBusiness
        {
            Id = foreignBusinessId,
            Key = "foreign-business",
            DisplayName = "Foreign Business",
            LegalName = "Foreign Business LLC",
            BusinessType = "BusinessClient",
            OwnerEmail = "foreign@example.test",
            Status = "Active",
            IsActive = true
        });
        fixture.Db.CommerceProducts.AddRange(
            new CommerceProduct
            {
                CommerceBusinessId = businessId,
                ExternalProductKey = "own-active",
                Name = "Own Active Product",
                Slug = "own-active",
                Description = "Visible product",
                PriceLabel = "$10",
                PriceCents = 1000,
                IsActive = true,
                DisplayOrder = 1
            },
            new CommerceProduct
            {
                CommerceBusinessId = businessId,
                ExternalProductKey = "own-inactive",
                Name = "Own Inactive Product",
                Slug = "own-inactive",
                Description = "Hidden product",
                PriceLabel = "$20",
                PriceCents = 2000,
                IsActive = false,
                DisplayOrder = 2
            },
            new CommerceProduct
            {
                CommerceBusinessId = foreignBusinessId,
                ExternalProductKey = "foreign-active",
                Name = "Foreign Active Product",
                Slug = "foreign-active",
                Description = "Must never leak",
                PriceLabel = "$30",
                PriceCents = 3000,
                IsActive = true,
                DisplayOrder = 1
            });
        await fixture.Db.SaveChangesAsync();

        var document = new WebsiteContentDocument();
        document.Collections["products"] = new WebsiteCollectionDefinition
        {
            Id = "products",
            Name = "Products",
            Source = "commerce_products",
            Fields = ["slug", "name", "description", "priceCents"]
        };
        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));
        Assert.IsType<OkObjectResult>(await fixture.Controller.Save(new(ticket, document, 0)));

        fixture.Db.ChangeTracker.Clear();
        var manage = Assert.IsType<OkObjectResult>(await fixture.CreateController().Manage(ticket));
        var json = JsonSerializer.SerializeToElement(manage.Value, JsonOptions);
        var projections = json.GetProperty("collections").EnumerateArray().ToArray();
        var projection = Assert.Single(
            projections.Where(value => value.GetProperty("id").GetString() == "commerce_products"));
        Assert.Contains(
            projections,
            value => value.GetProperty("id").GetString() == "business_facts");
        Assert.True(projection.GetProperty("isList").GetBoolean());
        var item = Assert.Single(projection.GetProperty("items").EnumerateArray());
        Assert.Equal("own-active", item.GetProperty("key").GetString());
        Assert.Equal("Own Active Product", item.GetProperty("fields").GetProperty("name").GetString());
        var serialized = projection.GetRawText();
        Assert.DoesNotContain("Own Inactive Product", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Foreign Active Product", serialized, StringComparison.Ordinal);
        Assert.Contains(json.GetProperty("dataCatalog").EnumerateArray(),
            source => source.GetProperty("key").GetString() == "commerce_products");
    }

    [Fact]
    public void MediaReferenceCatalog_UsesOneGraphForAssetIdsCanonicalUrlsReusableShellAndFavicon()
    {
        var direct = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var url = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var shell = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var reusable = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var favicon = Guid.Parse("55555555-5555-5555-5555-555555555555");

        var document = CanonicalDocument();
        document.FaviconImageDataUrl = $"https://masterapp-protect.azurewebsites.net/api/website-content/media/{favicon}";
        document.Shell.Header =
        [
            new WebsiteCompositionNode
            {
                Id = "shell.logo",
                Type = "image",
                Tag = "img",
                MediaAssetId = shell
            }
        ];
        document.Pages["/"].Composition.Add(
            new WebsiteCompositionNode
            {
                Id = "home.photo",
                Type = "image",
                Tag = "img",
                MediaAssetId = direct
            });
        document.Pages["/"].Composition.Add(
            new WebsiteCompositionNode
            {
                Id = "home.video",
                Type = "video",
                Tag = "video",
                MediaUrl = $"/api/website-content/media/{url}"
            });
        document.ReusableComponents["media-card"] = new WebsiteReusableComponentDefinition
        {
            Id = "media-card",
            Name = "Media card",
            Kind = "block",
            Composition =
            [
                new WebsiteCompositionNode
                {
                    Id = "reusable.photo",
                    Type = "image",
                    Tag = "img",
                    MediaAssetId = reusable
                }
            ]
        };

        var ids = WebsiteMediaReferenceCatalog.Collect(document);

        Assert.Equal(5, ids.Count);
        Assert.Contains(direct, ids);
        Assert.Contains(url, ids);
        Assert.Contains(shell, ids);
        Assert.Contains(reusable, ids);
        Assert.Contains(favicon, ids);
        Assert.True(WebsiteMediaReferenceCatalog.References(document, direct));
        Assert.False(WebsiteMediaReferenceCatalog.References(document, Guid.NewGuid()));
    }

    [Fact]
    public async Task Save_RequiresCanonicalMediaReferencesToBelongToWebsiteOwner()
    {
        using var fixture = new Fixture(WebsiteEditorSiteKeys.Business);
        var owned = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var foreign = Guid.Parse("77777777-7777-7777-7777-777777777777");
        fixture.Db.Set<WebsiteMediaAsset>().AddRange(
            new WebsiteMediaAsset
            {
                Id = owned,
                OwnerKey = fixture.OwnerKey,
                SourceUrl = "owned.png",
                Sha256 = new string('f', 64),
                StorageKey = "website/owned.png",
                ContentType = "image/png",
                SizeBytes = 1200
            },
            new WebsiteMediaAsset
            {
                Id = foreign,
                OwnerKey = WebsiteEditorSiteKeys.BusinessOwnerKey(Guid.NewGuid()),
                SourceUrl = "foreign.png",
                Sha256 = new string('1', 64),
                StorageKey = "website/foreign.png",
                ContentType = "image/png",
                SizeBytes = 1200
            });
        await fixture.Db.SaveChangesAsync();

        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));
        var document = CanonicalDocument();
        document.Pages["/"].Composition.Add(new WebsiteCompositionNode
        {
            Id = "home.owned-photo",
            Type = "image",
            Tag = "img",
            MediaAssetId = owned
        });

        Assert.IsType<OkObjectResult>(await fixture.Controller.Save(new(ticket, document, 0)));

        document.Pages["/"].Composition.Add(new WebsiteCompositionNode
        {
            Id = "home.foreign-photo",
            Type = "image",
            Tag = "img",
            MediaAssetId = foreign
        });

        var rejected = Assert.IsType<BadRequestObjectResult>(
            await fixture.CreateController().Save(new(ticket, document, 1)));
        var json = JsonSerializer.SerializeToElement(rejected.Value, JsonOptions);
        Assert.Equal("invalid_website_document", json.GetProperty("error").GetString());
        Assert.False(json.GetProperty("canonicalProtectionViolation").GetBoolean());
    }

    [Fact]
    public async Task MediaLibrary_IsOwnerScopedSearchableAndRejectsInvalidTickets()
    {
        using var fixture = new Fixture(WebsiteEditorSiteKeys.Business);
        var ownerKey = WebsiteEditorSiteKeys.BusinessOwnerKey(fixture.BusinessId!.Value);
        var ownImage = new WebsiteMediaAsset
        {
            OwnerKey = ownerKey,
            SourceUrl = "team-logo.png",
            Sha256 = new string('a', 64),
            StorageKey = "website/team-logo.png",
            ContentType = "image/png",
            SizeBytes = 1200,
            CreatedUtc = DateTime.UtcNow
        };
        var ownVideo = new WebsiteMediaAsset
        {
            OwnerKey = ownerKey,
            SourceUrl = "welcome-video.mp4",
            Sha256 = new string('b', 64),
            StorageKey = "website/welcome-video.mp4",
            ContentType = "video/mp4",
            SizeBytes = 2200,
            CreatedUtc = DateTime.UtcNow.AddMinutes(-1)
        };
        fixture.Db.AddRange(ownImage, ownVideo, new WebsiteMediaAsset
        {
            OwnerKey = WebsiteEditorSiteKeys.BusinessOwnerKey(Guid.NewGuid()),
            SourceUrl = "other-logo.png",
            Sha256 = new string('c', 64),
            StorageKey = "website/other-logo.png",
            ContentType = "image/png",
            SizeBytes = 900
        });
        await fixture.Db.SaveChangesAsync();
        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));

        var imageResult = Assert.IsType<OkObjectResult>(await fixture.Controller.MediaLibrary(ticket, "logo", "image", CancellationToken.None));
        var imageJson = JsonSerializer.SerializeToElement(imageResult.Value, JsonOptions);
        var assets = imageJson.GetProperty("assets").EnumerateArray().ToArray();
        var image = Assert.Single(assets);
        Assert.Equal(ownImage.Id, image.GetProperty("id").GetGuid());
        Assert.Equal("team-logo.png", image.GetProperty("name").GetString());
        Assert.Equal("image/png", image.GetProperty("contentType").GetString());

        var videoResult = Assert.IsType<OkObjectResult>(await fixture.Controller.MediaLibrary(ticket, null, "video", CancellationToken.None));
        var videoJson = JsonSerializer.SerializeToElement(videoResult.Value, JsonOptions);
        Assert.Equal(ownVideo.Id, Assert.Single(videoJson.GetProperty("assets").EnumerateArray()).GetProperty("id").GetGuid());

        Assert.IsType<BadRequestObjectResult>(await fixture.Controller.MediaLibrary(ticket, null, "audio", CancellationToken.None));
        Assert.IsType<UnauthorizedResult>(await fixture.Controller.MediaLibrary("invalid-ticket", null, "all", CancellationToken.None));
    }


    [Fact]
    public async Task Manage_AdvertisesBrowserAgentWorkspaceWithoutExternalAiApi_AndDoesNotMutateDraft()
    {
        using var fixture = new Fixture(WebsiteEditorSiteKeys.Business);
        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));
        var document = CanonicalDocument("Original heading");

        Assert.IsType<OkObjectResult>(await fixture.Controller.Save(new(ticket, document, 0)));
        fixture.Db.ChangeTracker.Clear();

        var before = Assert.Single(await fixture.Db.Set<WebsiteContentState>().AsNoTracking().ToListAsync());
        var result = Assert.IsType<OkObjectResult>(await fixture.CreateController().Manage(ticket));
        var json = JsonSerializer.SerializeToElement(result.Value, JsonOptions);

        Assert.True(json.GetProperty("capabilities").GetProperty("browserAgentWorkspace").GetBoolean());
        Assert.False(json.GetProperty("capabilities").GetProperty("externalAiApi").GetBoolean());

        fixture.Db.ChangeTracker.Clear();
        var after = Assert.Single(await fixture.Db.Set<WebsiteContentState>().AsNoTracking().ToListAsync());
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.DraftJson, after.DraftJson);
        Assert.Null(after.PublishedVersionId);
    }


    [Fact]
    public async Task SignalDryRun_ValidatesMappingWithoutPersistingAnalyticsMetaOrDraftChanges()
    {
        using var fixture = new Fixture(WebsiteEditorSiteKeys.Business);
        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));
        var browserBindingId = Guid.NewGuid().ToString("N");
        var serverBindingId = Guid.NewGuid().ToString("N");
        var document = CanonicalDocument("CTA");
        Node(document).Signals =
        [
            new WebsiteSignalBinding
            {
                Id = browserBindingId,
                Trigger = "click",
                EventName = "LeadFormStart",
                DeliveryMode = "meta",
                OncePerSession = true
            }
        ];

        Assert.IsType<OkObjectResult>(await fixture.Controller.Save(new(ticket, document, 0)));
        fixture.Db.ChangeTracker.Clear();
        var before = Assert.Single(await fixture.Db.Set<WebsiteContentState>().AsNoTracking().ToListAsync());
        var beforeJson = before.DraftJson;

        var browser = Assert.IsType<OkObjectResult>(await fixture.CreateController().SignalDryRun(
            new WebsitePlatformController.WebsiteSignalTestRequest(ticket, before.Revision, "/", ElementId, browserBindingId),
            CancellationToken.None));
        var browserJson = JsonSerializer.SerializeToElement(browser.Value, JsonOptions);

        Assert.Equal("website_signal_private_dry_run", browserJson.GetProperty("source").GetString());
        Assert.True(browserJson.GetProperty("dryRun").GetBoolean());
        Assert.False(browserJson.GetProperty("persisted").GetBoolean());
        Assert.False(browserJson.GetProperty("metaDispatched").GetBoolean());
        Assert.True(browserJson.GetProperty("stages").GetProperty("mappingValidated").GetBoolean());
        Assert.True(browserJson.GetProperty("stages").GetProperty("browserTriggerSupported").GetBoolean());
        Assert.True(browserJson.GetProperty("stages").GetProperty("browserAnalyticsWouldBeAccepted").GetBoolean());
        Assert.False(browserJson.GetProperty("stages").GetProperty("browserPixelWouldInvoke").GetBoolean());

        Assert.Throws<ArgumentException>(() => WebsiteSignalBindingPolicy.Validate(
        [
            new WebsiteSignalBinding
            {
                Id = serverBindingId,
                Trigger = "submission_saved",
                EventName = "Lead",
                DeliveryMode = "destinations"
            }
        ]));

        fixture.Db.ChangeTracker.Clear();
        var after = Assert.Single(await fixture.Db.Set<WebsiteContentState>().AsNoTracking().ToListAsync());
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(beforeJson, after.DraftJson);
        Assert.Empty(await fixture.Db.AnalyticsEvents.AsNoTracking().ToListAsync());
        Assert.Empty(await fixture.Db.MetaSignalEvents.AsNoTracking().ToListAsync());

        Assert.IsType<ConflictObjectResult>(await fixture.CreateController().SignalDryRun(
            new WebsitePlatformController.WebsiteSignalTestRequest(ticket, before.Revision - 1, "/", ElementId, browserBindingId),
            CancellationToken.None));
        Assert.IsType<UnauthorizedResult>(await fixture.CreateController().SignalDryRun(
            new WebsitePlatformController.WebsiteSignalTestRequest("invalid-ticket", before.Revision, "/", ElementId, browserBindingId),
            CancellationToken.None));
    }


    [Fact]
    public async Task SignalHealth_ReadsOnlyCurrentWebsiteVersionAndBindingHistoryWithoutSecrets()
    {
        using var fixture = new Fixture(WebsiteEditorSiteKeys.Business);
        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));
        var bindingId = Guid.NewGuid().ToString("N");
        var otherBindingId = Guid.NewGuid().ToString("N");
        var document = CanonicalDocument("CTA");
        Node(document).Signals =
        [
            new WebsiteSignalBinding
            {
                Id = bindingId,
                Trigger = "click",
                EventName = "LeadFormStart",
                DeliveryMode = "analytics"
            }
        ];

        Assert.IsType<OkObjectResult>(await fixture.Controller.Save(new(ticket, document, 0)));
        var state = Assert.Single(await fixture.Db.Set<WebsiteContentState>().ToListAsync());
        var versionId = Guid.NewGuid();
        state.PublishedVersionId = versionId;

        fixture.Db.AnalyticsEvents.Add(new AnalyticsEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "LeadFormStart",
            EventUtc = DateTime.UtcNow,
            ReceivedUtc = DateTime.UtcNow,
            CommerceBusinessId = fixture.BusinessId,
            WebsiteContentVersionId = versionId,
            WebsiteBindingId = bindingId,
            PageKey = "/"
        });
        fixture.Db.MetaSignalEvents.Add(new MetaSignalEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            EventName = "LeadFormStart",
            CreatedUtc = DateTime.UtcNow,
            CommerceBusinessId = fixture.BusinessId,
            WebsiteContentVersionId = versionId,
            WebsiteBindingId = bindingId,
            MetaBrowserSent = false,
            MetaServerSent = false,
            MetadataJson = "{\"metaServerAttempted\":true,\"metaServerSent\":false,\"metaServerStatus\":\"retry_scheduled\",\"metaServerRetryable\":true,\"metaServerAttemptCount\":2,\"metaServerHttpStatusCode\":503,\"metaServerTraceId\":\"trace-safe\"}"
        });
        fixture.Db.AnalyticsEvents.Add(new AnalyticsEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "LeadFormStart",
            EventUtc = DateTime.UtcNow,
            ReceivedUtc = DateTime.UtcNow,
            CommerceBusinessId = Guid.NewGuid(),
            WebsiteContentVersionId = Guid.NewGuid(),
            WebsiteBindingId = otherBindingId
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = Assert.IsType<OkObjectResult>(await fixture.CreateController().SignalHealth(
            ticket, "/", ElementId, bindingId, null, CancellationToken.None));
        var json = JsonSerializer.SerializeToElement(result.Value, JsonOptions);

        Assert.Equal("website_signal_existing_authorities", json.GetProperty("source").GetString());
        Assert.Equal(versionId, json.GetProperty("publishedVersionId").GetGuid());
        Assert.Single(json.GetProperty("analytics").EnumerateArray());

        var meta = Assert.Single(json.GetProperty("meta").EnumerateArray());
        Assert.Equal("retry_scheduled", meta.GetProperty("dispatch").GetProperty("status").GetString());
        Assert.Equal(2, meta.GetProperty("dispatch").GetProperty("attemptCount").GetInt32());
        Assert.Equal(503, meta.GetProperty("dispatch").GetProperty("httpStatusCode").GetInt32());
        Assert.Equal("trace-safe", meta.GetProperty("dispatch").GetProperty("traceId").GetString());

        var serialized = json.ToString();
        Assert.DoesNotContain("AccessToken", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ciphertext", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(otherBindingId, serialized, StringComparison.Ordinal);

        Assert.IsType<UnauthorizedResult>(await fixture.CreateController().SignalHealth(
            "invalid-ticket", "/", ElementId, bindingId, null, CancellationToken.None));
    }


    [Fact]
    public async Task Collaboration_UsesExistingBusinessMembershipRolesAndNeverMutatesWebsiteDraft()
    {
        using var fixture = new Fixture(WebsiteEditorSiteKeys.Business);
        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));
        var document = CanonicalDocument("Collaborative heading");

        Assert.IsType<OkObjectResult>(await fixture.Controller.Save(new(ticket, document, 0)));
        fixture.Db.ChangeTracker.Clear();
        var before = Assert.Single(await fixture.Db.Set<WebsiteContentState>().AsNoTracking().ToListAsync());

        var collaboration = Assert.IsType<OkObjectResult>(await fixture.CreateController().Collaboration(
            ticket, "/", ElementId, CancellationToken.None));
        var collaborationJson = JsonSerializer.SerializeToElement(collaboration.Value, JsonOptions);

        Assert.Equal("website_studio_collaboration", collaborationJson.GetProperty("source").GetString());
        Assert.Equal("owner", collaborationJson.GetProperty("role").GetProperty("roleKey").GetString());
        Assert.True(collaborationJson.GetProperty("role").GetProperty("canPublish").GetBoolean());

        var collaborator = Assert.Single(collaborationJson.GetProperty("collaborators").EnumerateArray());
        Assert.Equal("owner", collaborator.GetProperty("roleKey").GetString());
        Assert.True(collaborator.GetProperty("canManageStorefront").GetBoolean());
        Assert.True(collaborator.GetProperty("canPublish").GetBoolean());

        var created = Assert.IsType<OkObjectResult>(await fixture.CreateController().CreateCollaborationComment(
            new WebsitePlatformController.WebsiteStudioCommentCreateRequest(
                ticket,
                before.Revision,
                "/",
                ElementId,
                "Tighten this headline before publishing."),
            CancellationToken.None));
        var createdJson = JsonSerializer.SerializeToElement(created.Value, JsonOptions);
        Assert.Equal("website_studio_collaboration", createdJson.GetProperty("source").GetString());

        fixture.Db.ChangeTracker.Clear();
        var comment = Assert.Single(await fixture.Db.Set<WebsiteStudioComment>().AsNoTracking().ToListAsync());
        Assert.Equal(before.Id, comment.WebsiteContentStateId);
        Assert.Equal(before.Revision, comment.AnchorRevision);
        Assert.Equal("/", comment.PagePath);
        Assert.Equal(ElementId, comment.ElementId);
        Assert.Equal("owner", comment.AuthorRole);
        Assert.Equal("open", comment.Status);

        var afterComment = Assert.Single(await fixture.Db.Set<WebsiteContentState>().AsNoTracking().ToListAsync());
        Assert.Equal(before.Revision, afterComment.Revision);
        Assert.Equal(before.DraftJson, afterComment.DraftJson);

        var resolved = Assert.IsType<OkObjectResult>(await fixture.CreateController().SetCollaborationCommentStatus(
            new WebsitePlatformController.WebsiteStudioCommentStatusRequest(ticket, comment.Id, "resolved"),
            CancellationToken.None));
        var resolvedJson = JsonSerializer.SerializeToElement(resolved.Value, JsonOptions);
        Assert.Equal("resolved", resolvedJson.GetProperty("comment").GetProperty("status").GetString());

        fixture.Db.ChangeTracker.Clear();
        var afterResolve = Assert.Single(await fixture.Db.Set<WebsiteContentState>().AsNoTracking().ToListAsync());
        Assert.Equal(before.Revision, afterResolve.Revision);
        Assert.Equal(before.DraftJson, afterResolve.DraftJson);

        var member = Assert.Single(await fixture.Db.CommerceBusinessMembers.ToListAsync());
        member.RoleKey = "member";
        member.CanManageStorefront = true;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        collaboration = Assert.IsType<OkObjectResult>(await fixture.CreateController().Collaboration(
            ticket, "/", null, CancellationToken.None));
        collaborationJson = JsonSerializer.SerializeToElement(collaboration.Value, JsonOptions);
        Assert.Equal("member", collaborationJson.GetProperty("role").GetProperty("roleKey").GetString());
        Assert.False(collaborationJson.GetProperty("role").GetProperty("canPublish").GetBoolean());
        Assert.Equal("Website editor", collaborationJson.GetProperty("role").GetProperty("label").GetString());

        Assert.IsType<UnauthorizedResult>(await fixture.CreateController().Collaboration(
            "invalid-ticket", "/", null, CancellationToken.None));
    }

    [Fact]
    public async Task Collaboration_RepliesStayScopedToCanonicalWebsiteStateAndOneLevelThread()
    {
        using var fixture = new Fixture(WebsiteEditorSiteKeys.Legend);
        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));
        var document = new WebsiteContentDocument();
        document.Pages["/"] = new WebsitePageDocument();
        Assert.IsType<OkObjectResult>(await fixture.Controller.Save(new(ticket, document, 0)));

        var parentResult = Assert.IsType<OkObjectResult>(await fixture.CreateController().CreateCollaborationComment(
            new WebsitePlatformController.WebsiteStudioCommentCreateRequest(
                ticket, 1, "/", null, "Page-level review note."),
            CancellationToken.None));
        var parentJson = JsonSerializer.SerializeToElement(parentResult.Value, JsonOptions);
        var parentId = parentJson.GetProperty("comment").GetProperty("id").GetGuid();

        Assert.IsType<OkObjectResult>(await fixture.CreateController().CreateCollaborationComment(
            new WebsitePlatformController.WebsiteStudioCommentCreateRequest(
                ticket, 1, "/", ElementId, "Reply on the same review thread.", parentId),
            CancellationToken.None));

        fixture.Db.ChangeTracker.Clear();
        var comments = await fixture.Db.Set<WebsiteStudioComment>().AsNoTracking()
            .OrderBy(value => value.CreatedUtc).ToListAsync();
        Assert.Equal(2, comments.Count);
        Assert.Null(comments[0].ParentCommentId);
        Assert.Equal(parentId, comments[1].ParentCommentId);
        Assert.Equal(comments[0].WebsiteContentStateId, comments[1].WebsiteContentStateId);
        Assert.Equal("/", comments[1].PagePath);

        var nested = await fixture.CreateController().CreateCollaborationComment(
            new WebsitePlatformController.WebsiteStudioCommentCreateRequest(
                ticket, 1, "/", null, "Nested reply should be rejected.", comments[1].Id),
            CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(nested);
    }


    [Fact]
    public async Task DraftQuality_ReadsOnlyAuthorizedPersistedDraftAndReportsServerSource()
    {
        using var fixture = new Fixture(WebsiteEditorSiteKeys.Business);
        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));
        var document = new WebsiteContentDocument();
        document.Pages["/"] = new WebsitePageDocument
        {
            Navigation = new WebsitePageNavigation { ShowInNavigation = true },
            DynamicBinding = new WebsiteDynamicPageBinding { CollectionId = "missing", ItemKeyField = "id" },
            Composition =
            [
                new WebsiteCompositionNode
                {
                    Id = "photo",
                    Type = "image",
                    Tag = "img",
                    MediaUrl = "/assets/photo.png"
                }
            ]
        };

        Assert.IsType<OkObjectResult>(await fixture.Controller.Save(new(ticket, document, 0)));

        var quality = Assert.IsType<OkObjectResult>(await fixture.CreateController().DraftQuality(
            ticket,
            CancellationToken.None));
        var json = JsonSerializer.SerializeToElement(quality.Value, JsonOptions);

        Assert.Equal("saved_draft_server", json.GetProperty("source").GetString());
        Assert.Equal(1, json.GetProperty("revision").GetInt64());

        var codes = json.GetProperty("checks").EnumerateArray()
            .Select(value => value.GetProperty("code").GetString())
            .Where(value => value is not null)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("page_title_missing", codes);
        Assert.Contains("dynamic_collection_missing", codes);
        Assert.DoesNotContain("navigation_label_missing", codes);
        Assert.DoesNotContain("image_alt_missing", codes);

        Assert.IsType<UnauthorizedResult>(await fixture.CreateController().DraftQuality(
            "invalid-ticket",
            CancellationToken.None));
    }

    private static async Task<WebsiteContentDocument> SeedCanonicalDraftAsync(
        Fixture fixture,
        string ticket,
        WebsiteContentDocument document,
        long revision = 1)
    {
        _ = await fixture.Controller.Manage(ticket);
        var state = Assert.Single(await fixture.Db.Set<WebsiteContentState>().ToListAsync());
        var canonical = WebsiteContentSanitizer.Sanitize(document);
        WebsiteSystemTemplateAuthority.Apply(fixture.SiteKey, canonical);
        canonical.UpdatedUtc = DateTime.UtcNow;
        state.DraftJson = JsonSerializer.Serialize(canonical, JsonOptions);
        state.Revision = revision;
        state.PublishedVersionId = null;
        state.ScheduledPublishUtc = null;
        state.ScheduledActorJson = null;
        state.ScheduledRevision = null;
        state.ScheduleError = null;
        state.UpdatedUtc = DateTime.UtcNow;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        return canonical;
    }

    private static async Task<WebsiteContentDocument> ApplyMutationsAndReadAsync(
        Fixture fixture,
        string ticket,
        long expectedRevision,
        IReadOnlyList<WebsiteMutationOperation> operations,
        Guid? draftId = null,
        string? draftName = null)
    {
        var result = await fixture.CreateController().ApplyMutations(
            new WebsitePlatformController.WebsiteMutationRequest(
                ticket,
                expectedRevision,
                operations.ToList(),
                draftId,
                draftName));
        Assert.IsType<OkObjectResult>(result);
        fixture.Db.ChangeTracker.Clear();
        return ReadDocument(await fixture.CreateController().Manage(ticket));
    }

    private static WebsiteMutationOperation ReplaceNodeOperation(
        WebsiteContentDocument document,
        Action<WebsiteCompositionNode> mutate)
    {
        var node = Node(document);
        var replacement = JsonSerializer.Deserialize<WebsiteCompositionNode>(
            JsonSerializer.Serialize(node, JsonOptions),
            JsonOptions)!;
        mutate(replacement);
        return new WebsiteMutationOperation
        {
            Type = "replaceNode",
            NodeId = node.Id,
            ExpectedFingerprint = WebsiteCreativeFingerprint.Node(node),
            Node = replacement
        };
    }

    private static WebsiteContentDocument ReadDocument(IActionResult result)
    {
        var value = Assert.IsType<OkObjectResult>(result).Value;
        var envelope = JsonSerializer.SerializeToElement(value, JsonOptions);
        return envelope.GetProperty("document").Deserialize<WebsiteContentDocument>(JsonOptions)!;
    }

    private static void AssertLargeStyle(WebsiteVisualStyle style)
    {
        Assert.Equal(12.75m, style.FontScale);
        Assert.Equal(250.25m, style.WidthPercent);
        Assert.Equal(500.5m, style.PaddingTop);
        Assert.Equal(800.125m, style.PaddingBottom);
        Assert.Equal("start", style.TextAlign);
    }

    private static void AssertNoAdjustments(WebsiteVisualStyle style)
    {
        Assert.Null(style.FontScale);
        Assert.Null(style.WidthPercent);
        Assert.Null(style.PaddingTop);
        Assert.Null(style.PaddingBottom);
    }

    private sealed class Fixture : IDisposable
    {
        public const string AgentSlug = "editor-test-agent";
        public Guid? BusinessId { get; private set; }
        public string OwnerKey => _owner;
        public string SiteKey => _siteKey;
        private readonly string _siteKey;
        private readonly string _owner;
        private readonly WebsiteEditorTicketProtector _tickets = new(new EphemeralDataProtectionProvider());
        private readonly IConfiguration _configuration;
        private readonly string _actor = Guid.NewGuid().ToString();
        private Guid? _clientProfileId;
        private ServiceProvider? _services;
        public MasterAppDbContext Db { get; }
        public WebsitePlatformController Controller { get; }

        public Fixture(string siteKey)
        {
            _configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            {
                ["Founder:Oid"] = _actor,
                ["WebsitePublishing:CompilerRoot"] = Path.GetFullPath(Path.Combine(SourceDirectory(), "..", "Legend-Website"))
            }).Build();
            _siteKey = siteKey;
            _owner = siteKey == WebsiteEditorSiteKeys.Legend ? WebsiteEditorSiteKeys.GlobalOwnerKey : "editor-test-owner";
            Db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
            if (siteKey == WebsiteEditorSiteKeys.Protect) _owner = _actor;
            if (siteKey != WebsiteEditorSiteKeys.Business)
            {
                Db.AgentTrackingProfiles.Add(new AgentTrackingProfile
                {
                    AgentUserId = _actor, AgentUpn = "founder@example.test", Slug = AgentSlug, Status = "Active"
                });
                Db.SaveChanges();
            }
            if (siteKey == WebsiteEditorSiteKeys.Business)
            {
                var businessId = Guid.NewGuid();
                BusinessId = businessId;
                _owner = WebsiteEditorSiteKeys.BusinessOwnerKey(businessId);
                Db.CommerceBusinesses.Add(new CommerceBusiness
                {
                    Id = businessId,
                    Key = "editor-test-business",
                    DisplayName = "Editor Test Business",
                    LegalName = "Editor Test Business LLC",
                    BusinessType = "BusinessClient",
                    OwnerEmail = "owner@example.test",
                    Status = "Active",
                    IsActive = true
                });
                var profile = new ClientProfile { ClientUserId = _actor, CrmNotes = "{\"recordType\":\"BusinessClient\"}" };
                _clientProfileId = profile.Id;
                Db.ClientProfiles.Add(profile);
                Db.CommerceBusinessMembers.Add(new CommerceBusinessMember { CommerceBusinessId = businessId, ClientProfileId = profile.Id, RoleKey = "owner" });
                Db.SaveChanges();
            }
            var environment = Mock.Of<IWebHostEnvironment>(e => e.ContentRootPath == AppContext.BaseDirectory);
            var meta = new Mock<Infrastructure.Analytics.IMetaPixelResolutionService>();
            meta.Setup(service => service.ResolveForOwnerAsync(It.IsAny<Shared.Analytics.MarketingOwnerScope>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Infrastructure.Analytics.ResolvedMetaPixelContext());
            _services = new ServiceCollection()
                .AddSingleton(meta.Object)
                .AddSingleton(new WebsitePageCompiler(environment, _configuration))
                .BuildServiceProvider();
            Controller = CreateController();
        }

        public WebsitePlatformController CreateController() => new(Db, _tickets, _configuration) { ControllerContext = new() { HttpContext = new DefaultHttpContext { RequestServices = _services! } } };
        public string Ticket(DateTime expiresUtc) => TicketForActor(_actor, "founder@example.test", expiresUtc);

        public string TicketForActor(string actorUserId, string actorEmail, DateTime expiresUtc) =>
            _tickets.Protect(new WebsiteEditorTicket(
                _siteKey,
                _owner,
                _siteKey == WebsiteEditorSiteKeys.Protect ? AgentSlug : null,
                true,
                expiresUtc,
                BusinessId,
                ActorUserId: actorUserId,
                ActorEmail: actorEmail,
                ActorClientProfileId: _clientProfileId));
        public void Dispose() { _services?.Dispose(); _tickets.Dispose(); Db.Dispose(); }
        private static string SourceDirectory([CallerFilePath] string sourcePath = "") => Path.GetDirectoryName(sourcePath)!;
    }
}
