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
using ProtectWebsite.Controllers;
using Xunit;

namespace AgentPortal.Tests;

// In-process controller + JSON + EF InMemory coverage. These tests do not prove
// SQL behavior, HTTP middleware/model binding, or authenticated live deployment.
public sealed class WebsiteContentEditorRoundTripTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string ElementId = "home.h1.title";

    [Theory]
    [InlineData(WebsiteEditorSiteKeys.Legend)]
    [InlineData(WebsiteEditorSiteKeys.Protect)]
    [InlineData(WebsiteEditorSiteKeys.Business)]
    public async Task NamedDrafts_CreateUpdateLoadDelete_KeepPublishedContentIsolated(string siteKey)
    {
        using var fixture = new Fixture(siteKey);
        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));
        var document = new WebsiteContentDocument();
        document.Elements[ElementId] = new() { Text = "Black variation" };
        Assert.IsType<OkObjectResult>(await fixture.Controller.Save(new(ticket, document, 0, null, "Black")));
        var state = Assert.Single(await fixture.Db.Set<WebsiteContentState>().ToListAsync());
        var draft = Assert.Single(JsonSerializer.Deserialize<List<WebsiteNamedDraft>>(state.NamedDraftsJson, JsonOptions)!);
        Assert.Null(state.PublishedVersionId);
        document.Elements[ElementId].Text = "Second variation";
        Assert.IsType<OkObjectResult>(await fixture.Controller.Save(new(ticket, document, 1, null, "Second")));
        Assert.Equal(2, JsonSerializer.Deserialize<List<WebsiteNamedDraft>>(state.NamedDraftsJson, JsonOptions)!.Count);
        Assert.IsType<ConflictObjectResult>(await fixture.Controller.LoadDraft(new(ticket, 1, draft.Id)));
        Assert.IsType<OkObjectResult>(await fixture.Controller.LoadDraft(new(ticket, 2, draft.Id)));
        Assert.Equal("Black variation", ReadDocument(await fixture.Controller.Manage(ticket)).Elements[ElementId].Text);
        document.Elements[ElementId].Text = "Updated black";
        Assert.IsType<OkObjectResult>(await fixture.Controller.Save(new(ticket, document, 3, draft.Id, "Black")));
        Assert.IsType<NotFoundResult>(await fixture.Controller.DeleteDraft(new(ticket, 4, Guid.NewGuid())));
        Assert.IsType<OkObjectResult>(await fixture.Controller.DeleteDraft(new(ticket, 4, draft.Id)));
        Assert.Single(JsonSerializer.Deserialize<List<WebsiteNamedDraft>>(state.NamedDraftsJson, JsonOptions)!);
        Assert.Equal("Updated black", ReadDocument(await fixture.Controller.Manage(ticket)).Elements[ElementId].Text);
        Assert.Null(state.PublishedVersionId);
    }

    [Theory]
    [InlineData(WebsiteEditorSiteKeys.Legend)]
    [InlineData(WebsiteEditorSiteKeys.Protect)]
    [InlineData(WebsiteEditorSiteKeys.Business)]
    public async Task RouteKeyedEditorPage_PreservesContentAndMetadataAcrossSaveAndReload(string siteKey)
    {
        using var fixture = new Fixture(siteKey);
        var document = JsonSerializer.Deserialize<WebsiteContentDocument>("""
            {"pages":{"/":{"title":"Our business","description":"Our services",
              "elements":{"home.h1.title":{"text":"Saved page content"}},
              "extras":[{"id":"new-section","type":"section","sectionId":"home.root"}]},
              "/about":{"title":"About us","elements":{}}}}
            """, JsonOptions)!;
        document.FaviconImageDataUrl = "https://masterapp-protect.azurewebsites.net/api/website-content/media/11111111-1111-1111-1111-111111111111";
        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));
        var saved = ReadDocument(await fixture.Controller.Save(new(ticket, document, 0)));
        Assert.Equal("Saved page content", saved.Pages["/"].Elements[ElementId].Text);
        Assert.Equal(document.FaviconImageDataUrl, saved.FaviconImageDataUrl);
        fixture.Db.ChangeTracker.Clear();
        var reloaded = ReadDocument(await fixture.CreateController().Manage(ticket));
        Assert.Equal("Our business", reloaded.Pages["/"].Title);
        Assert.Equal("Our services", reloaded.Pages["/"].Description);
        Assert.Equal("About us", reloaded.Pages["/about"].Title);
        Assert.Equal(document.FaviconImageDataUrl, reloaded.FaviconImageDataUrl);
        Assert.Single(reloaded.Pages["/"].Extras);
    }

    [Theory]
    [InlineData(WebsiteEditorSiteKeys.Legend)]
    [InlineData(WebsiteEditorSiteKeys.Protect)]
    [InlineData(WebsiteEditorSiteKeys.Business)]
    public async Task LargeFractionalAdjustments_SurviveSaveAndReloadThroughBothReadPaths(string siteKey)
    {
        using var fixture = new Fixture(siteKey);
        // Deserialize the actual camel-case payload shape; old integer contract
        // properties would reject the fractional dimensions at this boundary.
        var document = JsonSerializer.Deserialize<WebsiteContentDocument>("""
            {"elements":{"home.h1.title":{"text":"Updated title","style":{
              "fontScale":12.75,"widthPercent":250.25,
              "paddingTop":500.5,"paddingBottom":800.125,"textAlign":"start"}}}}
            """, JsonOptions)!;
        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));
        var saved = ReadDocument(await fixture.Controller.Save(new(ticket, document, 0)));
        AssertLargeStyle(saved.Elements[ElementId].Style);

        fixture.Db.ChangeTracker.Clear();
        // A fresh controller and database reads prevent tracked entity state
        // from standing in for a persisted JSON round trip.
        var reloaded = fixture.CreateController();
        AssertLargeStyle(ReadDocument(await reloaded.Manage(ticket)).Elements[ElementId].Style);
        var unpublished = await reloaded.Public(siteKey, siteKey == WebsiteEditorSiteKeys.Protect ? Fixture.AgentSlug : null, fixture.BusinessId);
        if (siteKey == WebsiteEditorSiteKeys.Business) Assert.IsType<NotFoundObjectResult>(unpublished);
        else Assert.IsType<OkObjectResult>(unpublished);
        Assert.IsType<OkObjectResult>(await reloaded.Publish(new(ticket, 1)));
        fixture.Db.ChangeTracker.Clear();
        AssertLargeStyle(ReadDocument(await fixture.CreateController().Public(
            siteKey,
            siteKey == WebsiteEditorSiteKeys.Protect ? Fixture.AgentSlug : null,
            fixture.BusinessId)).Elements[ElementId].Style);
        var row = Assert.Single(await fixture.Db.Set<WebsiteContentState>().ToListAsync());
        Assert.Equal(siteKey, row.SiteKey);
        Assert.NotNull(row.PublishedVersionId);
        Assert.Single(await fixture.Db.Set<WebsiteContentVersion>().ToListAsync());
        Assert.Empty(await fixture.Db.AgentFinanceToolStates.ToListAsync());
        document.Elements[ElementId].Text = "Unpublished revision";
        ReadDocument(await fixture.CreateController().Save(new(ticket, document, 2)));
        Assert.IsType<ConflictObjectResult>(await fixture.CreateController().Save(new(ticket, new WebsiteContentDocument(), 2)));
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal("Updated title", ReadDocument(await fixture.CreateController().Public(
            siteKey, siteKey == WebsiteEditorSiteKeys.Protect ? Fixture.AgentSlug : null, fixture.BusinessId)).Elements[ElementId].Text);
        Assert.Equal("Unpublished revision", ReadDocument(await fixture.CreateController().Manage(ticket)).Elements[ElementId].Text);
    }

    [Fact]
    public async Task BusinessPublication_PreservesUtf8AcrossDotNetNodeCompilerTransport()
    {
        using var fixture = new Fixture(WebsiteEditorSiteKeys.Business);
        const string unicode = "Locally Owned · Lynden, WA — Café ® “clean”";
        var document = new WebsiteContentDocument();
        document.Pages["/"] = new WebsitePageDocument { Title = unicode, Description = unicode };
        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));

        Assert.IsType<OkObjectResult>(await fixture.Controller.Save(new(ticket, document, 0)));
        Assert.IsType<OkObjectResult>(await fixture.Controller.Publish(new(ticket, 1)));

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
        var document = new WebsiteContentDocument { FaviconImageDataUrl = first };
        var slug = siteKey == WebsiteEditorSiteKeys.Protect ? Fixture.AgentSlug : null;

        Assert.IsType<OkObjectResult>(await fixture.Controller.Save(new(ticket, document, 0)));
        var unpublished = Assert.IsType<RedirectResult>(await fixture.Controller.PublicFavicon(siteKey, slug));
        Assert.EndsWith("/images/favicon/legend-favicon.svg", unpublished.Url, StringComparison.Ordinal);

        Assert.IsType<OkObjectResult>(await fixture.Controller.Publish(new(ticket, 1)));
        fixture.Db.ChangeTracker.Clear();
        var published = Assert.IsType<RedirectResult>(await fixture.CreateController().PublicFavicon(siteKey, slug));
        Assert.Equal(first, published.Url);

        document.FaviconImageDataUrl = second;
        Assert.IsType<OkObjectResult>(await fixture.CreateController().Save(new(ticket, document, 2)));
        fixture.Db.ChangeTracker.Clear();
        var stillPublished = Assert.IsType<RedirectResult>(await fixture.CreateController().PublicFavicon(siteKey, slug));
        Assert.Equal(first, stillPublished.Url);
    }

    [Theory]
    [InlineData(WebsiteEditorSiteKeys.Legend)]
    [InlineData(WebsiteEditorSiteKeys.Protect)]
    [InlineData(WebsiteEditorSiteKeys.Business)]
    public async Task ResponsiveLayoutContract_RoundTripsThroughTheSameDocumentForEveryScope(string siteKey)
    {
        using var fixture = new Fixture(siteKey);
        var document = new WebsiteContentDocument
        {
            Breakpoints =
            [
                new() { Id = "tablet", Label = "Tablet", MaxWidthPx = 1024 },
                new() { Id = "mobile", Label = "Mobile", MaxWidthPx = 640 }
            ]
        };
        document.Elements[ElementId] = new WebsiteElementOverride
        {
            Style = new() { WidthPercent = 88, MaxWidthPx = 1200 },
            Layout = new()
            {
                Mode = "grid", Columns = 12, ColumnGap = 24, RowGap = 18,
                AlignItems = "stretch", JustifyContent = "space-between", Wrap = true
            },
            Responsive = new(StringComparer.Ordinal)
            {
                ["tablet"] = new()
                {
                    Style = new() { WidthPercent = 72, RotationDeg = 4, ZIndex = 7 },
                    Layout = new() { Mode = "flex", Direction = "row", Wrap = true, ColumnGap = 16 }
                },
                ["mobile"] = new()
                {
                    Hidden = false,
                    Style = new() { WidthPercent = 100, MarginTop = 12, AspectRatio = 1.5m },
                    Layout = new() { Mode = "stack", RowGap = 12, AlignItems = "stretch" }
                }
            }
        };

        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));
        var saved = ReadDocument(await fixture.Controller.Save(new(ticket, document, 0)));
        var element = saved.Elements[ElementId];

        Assert.Equal(["tablet", "mobile"], saved.Breakpoints.Select(x => x.Id));
        Assert.Equal("grid", element.Layout.Mode);
        Assert.Equal(12, element.Layout.Columns);
        Assert.Equal(72m, element.Responsive["tablet"].Style.WidthPercent);
        Assert.Equal("flex", element.Responsive["tablet"].Layout.Mode);
        Assert.Equal(100m, element.Responsive["mobile"].Style.WidthPercent);
        Assert.Equal("stack", element.Responsive["mobile"].Layout.Mode);
        Assert.Equal(1.5m, element.Responsive["mobile"].Style.AspectRatio);

        fixture.Db.ChangeTracker.Clear();
        var reloaded = ReadDocument(await fixture.CreateController().Manage(ticket)).Elements[ElementId];
        Assert.Equal(7, reloaded.Responsive["tablet"].Style.ZIndex);
        Assert.Equal(12m, reloaded.Responsive["mobile"].Style.MarginTop);
    }

    [Fact]
    public async Task ResponsiveAnchors_RoundTripAndRejectUnknownAnchorValues()
    {
        using var fixture = new Fixture(WebsiteEditorSiteKeys.Legend);
        var document = new WebsiteContentDocument();
        document.Elements[ElementId] = new WebsiteElementOverride
        {
            Style = new()
            {
                PositionMode = "absolute",
                HorizontalAnchor = "right",
                VerticalAnchor = "bottom",
                InsetRightPx = 24,
                InsetBottomPx = 36
            },
            Responsive = new(StringComparer.Ordinal)
            {
                ["mobile"] = new()
                {
                    Style = new()
                    {
                        HorizontalAnchor = "center",
                        VerticalAnchor = "top",
                        InsetTopPx = 18
                    }
                }
            }
        };

        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));
        var saved = ReadDocument(await fixture.Controller.Save(new(ticket, document, 0)));
        Assert.Equal("right", saved.Elements[ElementId].Style.HorizontalAnchor);
        Assert.Equal(24m, saved.Elements[ElementId].Style.InsetRightPx);
        Assert.Equal("center", saved.Elements[ElementId].Responsive["mobile"].Style.HorizontalAnchor);
        Assert.Equal(18m, saved.Elements[ElementId].Responsive["mobile"].Style.InsetTopPx);

        document.Elements[ElementId].Style.HorizontalAnchor = "somewhere";
        document.Elements[ElementId].Style.VerticalAnchor = "floating";
        document.Elements[ElementId].Style.InsetLeftPx = 50001;
        var sanitized = ReadDocument(await fixture.Controller.Save(new(ticket, document, 1))).Elements[ElementId].Style;
        Assert.Null(sanitized.HorizontalAnchor);
        Assert.Null(sanitized.VerticalAnchor);
        Assert.Null(sanitized.InsetLeftPx);
    }

    [Fact]
    public async Task ResponsiveLayoutSanitizer_DropsUnknownVariantsAndBoundsUnsafeGeometry()
    {
        using var fixture = new Fixture(WebsiteEditorSiteKeys.Legend);
        var document = new WebsiteContentDocument
        {
            Breakpoints =
            [
                new() { Id = "tablet", Label = "Tablet", MaxWidthPx = 50000 },
                new() { Id = "mobile", Label = "Mobile", MaxWidthPx = 200 }
            ]
        };
        document.Elements[ElementId] = new WebsiteElementOverride
        {
            Layout = new()
            {
                Mode = "invalid", Columns = 99, Rows = -5, ColumnGap = -1,
                Direction = "sideways", AlignItems = "wrong", JustifyContent = "wrong"
            },
            Responsive = new(StringComparer.Ordinal)
            {
                ["mobile"] = new()
                {
                    Style = new()
                    {
                        MaxWidthPx = 50001, Opacity = 2, ScaleX = 0,
                        RotationDeg = 50000, ZIndex = 50000, AspectRatio = 100
                    },
                    Layout = new() { Mode = "grid", Columns = 4, RowGap = 12 }
                },
                ["unknown"] = new() { Style = new() { WidthPercent = 55 } }
            }
        };

        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));
        var saved = ReadDocument(await fixture.Controller.Save(new(ticket, document, 0)));
        Assert.Equal(WebsiteBreakpointCatalog.MaxBreakpointWidth, saved.Breakpoints[0].MaxWidthPx);
        Assert.Equal(WebsiteBreakpointCatalog.MinBreakpointWidth, saved.Breakpoints[1].MaxWidthPx);

        var element = saved.Elements[ElementId];
        Assert.Null(element.Layout.Mode);
        Assert.Null(element.Layout.Columns);
        Assert.Null(element.Layout.Rows);
        Assert.Null(element.Layout.ColumnGap);
        Assert.False(element.Responsive.ContainsKey("unknown"));
        var mobile = element.Responsive["mobile"];
        Assert.Equal("grid", mobile.Layout.Mode);
        Assert.Equal(4, mobile.Layout.Columns);
        Assert.Null(mobile.Style.MaxWidthPx);
        Assert.Null(mobile.Style.Opacity);
        Assert.Null(mobile.Style.ScaleX);
        Assert.Null(mobile.Style.RotationDeg);
        Assert.Null(mobile.Style.ZIndex);
        Assert.Null(mobile.Style.AspectRatio);
    }

    [Fact]
    public async Task Manage_ExposesOneServerOwnedComponentCapabilityCatalog()
    {
        using var fixture = new Fixture(WebsiteEditorSiteKeys.Legend);
        var result = Assert.IsType<OkObjectResult>(await fixture.Controller.Manage(fixture.Ticket(DateTime.UtcNow.AddMinutes(10))));
        var json = JsonSerializer.Serialize(result.Value, JsonOptions);
        Assert.Contains("\"componentCatalog\"", json, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"section\"", json, StringComparison.Ordinal);
        Assert.Contains("\"free\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("componentCatalogV2", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComponentCapabilityRegistry_IsTheServerSanitizerAuthority()
    {
        using var fixture = new Fixture(WebsiteEditorSiteKeys.Legend);
        var document = new WebsiteContentDocument();
        document.Extras.Add(new WebsiteExtraComponent
        {
            Id = "text-one",
            Type = "text",
            SectionId = "home.section.1",
            Layout = new() { Mode = WebsiteLayoutModeCatalog.Free }
        });
        document.Extras.Add(new WebsiteExtraComponent
        {
            Id = "section-one",
            Type = "section",
            SectionId = "home.root",
            Layout = new() { Mode = WebsiteLayoutModeCatalog.Free }
        });
        document.Extras.Add(new WebsiteExtraComponent
        {
            Id = "unknown-one",
            Type = "invented-component",
            SectionId = "home.section.1"
        });

        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));
        var saved = ReadDocument(await fixture.Controller.Save(new(ticket, document, 0)));

        Assert.Equal(2, saved.Extras.Count);
        Assert.Null(saved.Extras.Single(x => x.Id == "text-one").Layout.Mode);
        Assert.Equal(WebsiteLayoutModeCatalog.Free, saved.Extras.Single(x => x.Id == "section-one").Layout.Mode);
        Assert.DoesNotContain(saved.Extras, x => x.Id == "unknown-one");
        Assert.Contains(WebsiteComponentCatalog.Options, x => x.Type == "group" && x.CanContainChildren);
        Assert.Contains(WebsiteLayoutModeCatalog.Free, WebsiteComponentCatalog.Find("group")!.LayoutModes);
    }

    [Fact]
    public async Task SyncedComponents_RoundTripThroughTheSameDocumentAndKeepInstancesLocal()
    {
        using var fixture = new Fixture(WebsiteEditorSiteKeys.Legend);
        var document = new WebsiteContentDocument();
        document.ReusableComponents["hero-shared"] = new WebsiteReusableComponentDefinition
        {
            Id = "hero-shared",
            Name = "Shared hero",
            RootType = "group",
            Style = new() { BackgroundColor = "#102b62", BorderRadius = 18 },
            Layout = new() { Mode = WebsiteLayoutModeCatalog.Stack, RowGap = 12 },
            Components =
            [
                new()
                {
                    Id = "headline",
                    Type = "heading",
                    SectionId = "__reusable__",
                    Text = "Shared headline",
                    Style = new() { FontSize = 52 }
                },
                new()
                {
                    Id = "copy",
                    Type = "text",
                    SectionId = "__reusable__",
                    Text = "Shared copy",
                    Placement = new() { SectionId = "__reusable__", BeforeId = null, ContainerId = null, Flow = true, Column = 1, Span = 12 }
                }
            ]
        };
        document.Extras.Add(new()
        {
            Id = "hero-instance-a",
            Type = "reusable",
            ReusableDefinitionId = "hero-shared",
            SectionId = "home.section.1",
            Style = new() { WidthPercent = 80, MarginTop = 12 }
        });
        document.Extras.Add(new()
        {
            Id = "hero-instance-b",
            Type = "reusable",
            ReusableDefinitionId = "hero-shared",
            SectionId = "home.section.2",
            Style = new() { WidthPercent = 55, MarginTop = 40 }
        });

        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));
        var saved = ReadDocument(await fixture.Controller.Save(new(ticket, document, 0)));
        var definition = Assert.Single(saved.ReusableComponents).Value;

        Assert.Equal("Shared hero", definition.Name);
        Assert.Equal("group", definition.RootType);
        Assert.Equal(2, definition.Components.Count);
        Assert.Equal("Shared headline", definition.Components.Single(x => x.Id == "headline").Text);
        Assert.Equal(WebsiteLayoutModeCatalog.Stack, definition.Layout.Mode);

        var instances = saved.Extras.Where(x => x.Type == "reusable").OrderBy(x => x.Id).ToArray();
        Assert.Equal(2, instances.Length);
        Assert.All(instances, x => Assert.Equal("hero-shared", x.ReusableDefinitionId));
        Assert.Equal(80m, instances[0].Style.WidthPercent);
        Assert.Equal(55m, instances[1].Style.WidthPercent);

        fixture.Db.ChangeTracker.Clear();
        var reloaded = ReadDocument(await fixture.CreateController().Manage(ticket));
        Assert.Equal("Shared headline", reloaded.ReusableComponents["hero-shared"].Components.Single(x => x.Id == "headline").Text);
        Assert.Equal(12m, reloaded.Extras.Single(x => x.Id == "hero-instance-a").Style.MarginTop);
        Assert.Equal(40m, reloaded.Extras.Single(x => x.Id == "hero-instance-b").Style.MarginTop);
    }

    [Fact]
    public async Task SyncedComponentSanitizer_RejectsMissingNestedAndPageSectionDefinitions()
    {
        using var fixture = new Fixture(WebsiteEditorSiteKeys.Legend);
        var document = new WebsiteContentDocument();
        document.ReusableComponents["safe"] = new WebsiteReusableComponentDefinition
        {
            Id = "safe",
            Name = "Safe",
            RootType = "container",
            Components =
            [
                new() { Id = "copy", Type = "text", SectionId = "__reusable__", Text = "Allowed" },
                new() { Id = "nested", Type = "reusable", ReusableDefinitionId = "other", SectionId = "__reusable__" },
                new() { Id = "section", Type = "section", SectionId = "__reusable__" }
            ]
        };
        document.Extras.Add(new()
        {
            Id = "valid-instance", Type = "reusable", ReusableDefinitionId = "safe", SectionId = "home.section.1"
        });
        document.Extras.Add(new()
        {
            Id = "missing-instance", Type = "reusable", ReusableDefinitionId = "does-not-exist", SectionId = "home.section.1"
        });

        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));
        var saved = ReadDocument(await fixture.Controller.Save(new(ticket, document, 0)));

        var definition = saved.ReusableComponents["safe"];
        var component = Assert.Single(definition.Components);
        Assert.Equal("copy", component.Id);
        Assert.Equal("text", component.Type);
        Assert.Single(saved.Extras);
        Assert.Equal("valid-instance", saved.Extras[0].Id);
        Assert.Equal("safe", saved.Extras[0].ReusableDefinitionId);

        var reusableCapability = WebsiteComponentCatalog.Find("reusable");
        Assert.NotNull(reusableCapability);
        Assert.False(reusableCapability!.DirectAdd);
    }

    [Fact]
    public async Task CoreComponentRegistry_AcceptsProfessionalPrimitivesWithoutASecondSchema()
    {
        using var fixture = new Fixture(WebsiteEditorSiteKeys.Legend);
        var document = new WebsiteContentDocument();
        foreach (var type in new[] { "heading", "quote", "divider", "spacer", "shape", "container" })
        {
            document.Extras.Add(new WebsiteExtraComponent
            {
                Id = type + "-one",
                Type = type,
                SectionId = "home.section.1",
                Text = type is "heading" or "quote" ? "Sample" : null,
                Style = type == "spacer" ? new() { HeightPx = 48 } : new(),
                Layout = type == "container" ? new() { Mode = WebsiteLayoutModeCatalog.Grid, Columns = 4 } : new()
            });
        }

        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));
        var saved = ReadDocument(await fixture.Controller.Save(new(ticket, document, 0)));
        Assert.Equal(6, saved.Extras.Count);
        Assert.Equal("Sample", saved.Extras.Single(x => x.Type == "heading").Text);
        Assert.Equal(48m, saved.Extras.Single(x => x.Type == "spacer").Style.HeightPx);
        Assert.Equal(WebsiteLayoutModeCatalog.Grid, saved.Extras.Single(x => x.Type == "container").Layout.Mode);
        Assert.Equal(4, saved.Extras.Single(x => x.Type == "container").Layout.Columns);
    }

    [Fact]
    public async Task MotionInteractions_RoundTripThroughTheCanonicalDocumentAndClampUnsafeValues()
    {
        using var fixture = new Fixture(WebsiteEditorSiteKeys.Legend);
        var document = new WebsiteContentDocument();
        document.Elements[ElementId] = new WebsiteElementOverride
        {
            Interactions =
            [
                new()
                {
                    Id = "motion-one",
                    Trigger = "enter-view",
                    Effect = "slide",
                    DurationMs = 650,
                    DelayMs = 120,
                    Easing = "ease-out",
                    Once = true,
                    Direction = "left",
                    DistancePx = 48
                },
                new()
                {
                    Id = "bad-motion",
                    Trigger = "invented-trigger",
                    Effect = "fade",
                    DurationMs = 999999,
                    Easing = "ease-out"
                }
            ]
        };

        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));
        var saved = ReadDocument(await fixture.Controller.Save(new(ticket, document, 0)));
        var motion = Assert.Single(saved.Elements[ElementId].Interactions);
        Assert.Equal("motion-one", motion.Id);
        Assert.Equal("enter-view", motion.Trigger);
        Assert.Equal("slide", motion.Effect);
        Assert.Equal(650, motion.DurationMs);
        Assert.Equal("left", motion.Direction);
        Assert.Equal(48m, motion.DistancePx);

        document.Elements[ElementId].Interactions =
        [
            new()
            {
                Id = "bounded",
                Trigger = "click",
                Effect = "rotate",
                DurationMs = 1,
                DelayMs = 99999,
                Easing = "linear",
                Direction = "diagonal",
                Amount = 999
            }
        ];
        var bounded = Assert.Single(ReadDocument(await fixture.Controller.Save(new(ticket, document, 1))).Elements[ElementId].Interactions);
        Assert.Equal(50, bounded.DurationMs);
        Assert.Equal(10000, bounded.DelayMs);
        Assert.Null(bounded.Direction);
        Assert.Null(bounded.Amount);
    }

    [Fact]
    public async Task Manage_ExposesOneServerOwnedMotionCatalog()
    {
        using var fixture = new Fixture(WebsiteEditorSiteKeys.Legend);
        var result = Assert.IsType<OkObjectResult>(await fixture.Controller.Manage(fixture.Ticket(DateTime.UtcNow.AddMinutes(10))));
        var json = JsonSerializer.Serialize(result.Value, JsonOptions);
        Assert.Contains("\"motionCatalog\"", json, StringComparison.Ordinal);
        Assert.Contains("\"enter-view\"", json, StringComparison.Ordinal);
        Assert.Contains("\"fade\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("motionCatalogV2", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MediaLibrary_ReturnsOnlyTheAuthorizedWebsiteOwnersAssetsAndNoStorageSecrets()
    {
        using var fixture = new Fixture(WebsiteEditorSiteKeys.Legend);
        var own = new WebsiteMediaAsset
        {
            Id = Guid.NewGuid(),
            OwnerKey = WebsiteEditorSiteKeys.GlobalOwnerKey,
            SourceUrl = "hero.png",
            Sha256 = new string('a', 64),
            StorageKey = "private/own-storage-key",
            ContentType = "image/png",
            SizeBytes = 2048,
            CreatedUtc = DateTime.UtcNow
        };
        var other = new WebsiteMediaAsset
        {
            Id = Guid.NewGuid(),
            OwnerKey = "another-website-owner",
            SourceUrl = "private.png",
            Sha256 = new string('b', 64),
            StorageKey = "private/other-storage-key",
            ContentType = "image/png",
            SizeBytes = 4096,
            CreatedUtc = DateTime.UtcNow
        };
        fixture.Db.AddRange(own, other);
        await fixture.Db.SaveChangesAsync();

        var result = Assert.IsType<OkObjectResult>(await fixture.Controller.MediaLibrary(
            fixture.Ticket(DateTime.UtcNow.AddMinutes(10))));
        var json = JsonSerializer.Serialize(result.Value, JsonOptions);

        Assert.Contains(own.Id.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hero.png", json, StringComparison.Ordinal);
        Assert.DoesNotContain(other.Id.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private.png", json, StringComparison.Ordinal);
        Assert.DoesNotContain("StorageKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sha256", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private/own-storage-key", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EditorLayerMetadata_RoundTripsWithoutASecondLayerStore()
    {
        using var fixture = new Fixture(WebsiteEditorSiteKeys.Legend);
        var document = new WebsiteContentDocument();
        document.Elements[ElementId] = new WebsiteElementOverride
        {
            Text = "Locked heading",
            EditorLocked = true,
            EditorLabel = "Hero headline",
            Style = new() { ZIndex = 9 }
        };

        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));
        var saved = ReadDocument(await fixture.Controller.Save(new(ticket, document, 0)));
        Assert.True(saved.Elements[ElementId].EditorLocked);
        Assert.Equal("Hero headline", saved.Elements[ElementId].EditorLabel);
        Assert.Equal(9, saved.Elements[ElementId].Style.ZIndex);

        fixture.Db.ChangeTracker.Clear();
        var reloaded = ReadDocument(await fixture.CreateController().Manage(ticket)).Elements[ElementId];
        Assert.True(reloaded.EditorLocked);
        Assert.Equal("Hero headline", reloaded.EditorLabel);
        Assert.Equal(9, reloaded.Style.ZIndex);
    }

    [Fact]
    public async Task InvalidNumericDomains_AreDiscardedWithoutInventingReplacementStyles()
    {
        using var fixture = new Fixture(WebsiteEditorSiteKeys.Legend);
        var document = new WebsiteContentDocument();
        document.Elements[ElementId] = new WebsiteElementOverride
        {
            Style = new WebsiteStyleOverride
            {
                FontScale = 0, WidthPercent = -1, PaddingTop = -2, PaddingBottom = -3
            }
        };
        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));
        ReadDocument(await fixture.Controller.Save(new(ticket, document, 0)));
        fixture.Db.ChangeTracker.Clear();
        AssertNoAdjustments(ReadDocument(await fixture.CreateController().Manage(ticket)).Elements[ElementId].Style);

        // Zero spacing remains legitimate; small positive scales/widths are
        // permitted instead of the former artificial minimums.
        document.Elements[ElementId].Style = new WebsiteStyleOverride
        {
            FontScale = 0.05m, WidthPercent = 0.25m, PaddingTop = 0, PaddingBottom = 0
        };
        var accepted = ReadDocument(await fixture.Controller.Save(new(ticket, document, 1))).Elements[ElementId].Style;
        Assert.Equal(0.05m, accepted.FontScale);
        Assert.Equal(0.25m, accepted.WidthPercent);
        Assert.Equal(0m, accepted.PaddingTop);
        Assert.Equal(0m, accepted.PaddingBottom);
    }

    [Fact]
    public async Task TextOnlyEdit_LeavesEveryStyleUnsetAfterPersistence()
    {
        using var fixture = new Fixture(WebsiteEditorSiteKeys.Legend);
        var document = JsonSerializer.Deserialize<WebsiteContentDocument>("""
            {"elements":{"home.h1.title":{"text":"New heading without resizing"}}}
            """, JsonOptions)!;
        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));
        ReadDocument(await fixture.Controller.Save(new(ticket, document, 0)));
        fixture.Db.ChangeTracker.Clear();
        var element = ReadDocument(await fixture.CreateController().Manage(ticket)).Elements[ElementId];
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
        var initial = new WebsiteContentDocument();
        initial.Elements[ElementId] = new WebsiteElementOverride { Text = "Preserved content" };
        var validTicket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));
        ReadDocument(await fixture.Controller.Save(new(validTicket, initial, 0)));
        var originalJson = (await fixture.Db.Set<WebsiteContentState>().SingleAsync()).DraftJson;
        initial.Elements[ElementId].Text = "Unauthorized replacement";
        var rejectedTicket = expired ? fixture.Ticket(DateTime.UtcNow.AddMinutes(-1)) : "not-a-protected-ticket";

        Assert.IsType<UnauthorizedResult>(await fixture.Controller.Manage(rejectedTicket));
        Assert.IsType<UnauthorizedResult>(await fixture.Controller.Save(new(rejectedTicket, initial, 1)));
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

    private static WebsiteContentDocument ReadDocument(IActionResult result)
    {
        var value = Assert.IsType<OkObjectResult>(result).Value;
        var envelope = JsonSerializer.SerializeToElement(value, JsonOptions);
        return envelope.GetProperty("document").Deserialize<WebsiteContentDocument>(JsonOptions)!;
    }

    private static void AssertLargeStyle(WebsiteStyleOverride style)
    {
        Assert.Equal(12.75m, style.FontScale);
        Assert.Equal(250.25m, style.WidthPercent);
        Assert.Equal(500.5m, style.PaddingTop);
        Assert.Equal(800.125m, style.PaddingBottom);
        Assert.Equal("start", style.TextAlign);
    }

    private static void AssertNoAdjustments(WebsiteStyleOverride style)
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
        private readonly string _siteKey;
        private readonly string _owner;
        private readonly WebsiteEditorTicketProtector _tickets = new(new EphemeralDataProtectionProvider());
        private readonly IConfiguration _configuration;
        private readonly string _actor = Guid.NewGuid().ToString();
        private Guid? _clientProfileId;
        private ServiceProvider? _services;
        public MasterAppDbContext Db { get; }
        public WebsiteContentController Controller { get; }

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
            _services = new ServiceCollection().AddSingleton(new WebsitePageCompiler(environment, _configuration)).BuildServiceProvider();
            Controller = CreateController();
        }

        public WebsiteContentController CreateController() => new(Db, _tickets, _configuration) { ControllerContext = new() { HttpContext = new DefaultHttpContext { RequestServices = _services! } } };
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
