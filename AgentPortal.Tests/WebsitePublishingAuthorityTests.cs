using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Infrastructure.Data;
using Domain.Entities;
using Shared.Analytics;
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
    [Theory]
    [InlineData("/quote/life", true)]
    [InlineData("/QUOTE/LIFE/", true)]
    [InlineData("/a/projection/Quote/Life/", true)]
    [InlineData("/unknown", false)]
    [InlineData(null, false)]
    public async Task ProtectPublicPageProjectionPreservesIdentityMetadataAndStoredContent(string? path, bool projected)
    {
        using var f = new Fixture();
        var profile = new AgentTrackingProfile { AgentUserId = "projection-agent", AgentUpn = "projection@example.test", Slug = "projection" };
        var document = Document("Other page body");
        document.Pages["/quote/life"] = new WebsitePageDocument
        {
            Title = "Life", Navigation = new WebsitePageNavigation { Label = "Life", Order = 2 },
            Composition = [new WebsiteCompositionNode { Id = "life.heading", Type = "heading", Tag = "h1", Text = "Current page body" }]
        };
        var stored = JsonSerializer.Serialize(document, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var state = new WebsiteContentState { OwnerKey = profile.AgentUserId, SiteKey = WebsiteEditorSiteKeys.Protect, DraftJson = "private draft" };
        var version = new WebsiteContentVersion { StateId = state.Id, Revision = 17, DocumentJson = stored };
        state.PublishedVersionId = version.Id;
        f.Db.AddRange(profile, state, version);
        await f.Db.SaveChangesAsync();

        var full = Body(await f.Controller.Public("protect", agentSlug: "projection"));
        var result = Body(await f.Controller.Public("protect", agentSlug: "projection", pagePath: path));
        Assert.Equal(full.GetProperty("publication").ToString(), result.GetProperty("publication").ToString());
        Assert.Equal(version.Id, result.GetProperty("publication").GetProperty("versionId").GetGuid());
        Assert.Equal(Page(full, "/quote/life").ToString(), Page(result, "/quote/life").ToString());
        Assert.Equal(Page(full).GetProperty("navigation").ToString(), Page(result).GetProperty("navigation").ToString());
        Assert.Equal(Page(full).GetProperty("title").ToString(), Page(result).GetProperty("title").ToString());
        Assert.Equal(projected ? 0 : Page(full).GetProperty("composition").GetArrayLength(), Page(result).GetProperty("composition").GetArrayLength());
        foreach (var field in new[] { "shell", "theme", "reusableComponents", "collections", "breakpoints" })
            Assert.Equal(full.GetProperty("document").GetProperty(field).ToString(), result.GetProperty("document").GetProperty(field).ToString());
        foreach (var field in new[] { "ctaCatalog", "store" })
            Assert.Equal(full.GetProperty(field).ToString(), result.GetProperty(field).ToString());
        Assert.Equal(stored, (await f.Db.Set<WebsiteContentVersion>().SingleAsync(v => v.Id == version.Id)).DocumentJson);
        Assert.Equal(version.Id, state.PublishedVersionId);
        Assert.Equal("private draft", state.DraftJson);
        Assert.Equal(full.ToString(), Body(await f.Controller.Public("protect", agentSlug: "projection")).ToString());
    }

    private sealed class MaterializationAudit : Microsoft.EntityFrameworkCore.Diagnostics.IMaterializationInterceptor
    {
        public System.Collections.Generic.List<Type> EntityTypes { get; } = new();
        public object InitializedInstance(
            Microsoft.EntityFrameworkCore.Diagnostics.MaterializationInterceptionData data, object entity)
        {
            EntityTypes.Add(entity.GetType());
            return entity;
        }
    }

    [Fact]
    public async Task ReportingEntryPointsProjectPublishedInputsWithoutMaterializingPrivatePayloadEntities()
    {
        var audit = new MaterializationAudit();
        using var db = ControllerTestHelpers.BuildDb(audit);
        var profile = new AgentTrackingProfile { AgentUserId = "projection-agent", AgentUpn = "projection@example.test", Slug = "projection" };
        var state = new WebsiteContentState
        {
            OwnerKey = profile.AgentUserId, SiteKey = WebsiteEditorSiteKeys.Protect,
            DraftJson = "private draft must not be read", NamedDraftsJson = "private named drafts must not be read"
        };
        var version = new WebsiteContentVersion
        {
            StateId = state.Id, Revision = 17, DocumentJson = "{}", CompiledPagesJson = "compiled payload must not be read"
        };
        state.PublishedVersionId = version.Id;
        db.AddRange(profile, state, version);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var query = new WebsiteEventMapQuery(db, new ConfigurationBuilder().Build());
        var ticket = new WebsiteEditorTicket(state.SiteKey, profile.AgentUserId, profile.Slug, false, DateTime.UtcNow.AddMinutes(5));
        foreach (var viaTicket in new[] { false, true })
        {
            audit.EntityTypes.Clear();
            var rows = viaTicket ? await query.ReadTicketAsync(ticket) : await query.ReadAsync(ScopeContext.ForAgent(profile.Id));
            Assert.NotEmpty(rows);
            Assert.All(rows, row =>
            {
                Assert.Equal(MarketingOwnerScope.Agent(profile.Id).Key, row.Owner);
                Assert.Equal(version.Id, row.PublishedVersion);
                Assert.Equal(17, row.PublishedRevision);
            });
            Assert.DoesNotContain(typeof(WebsiteContentState), audit.EntityTypes);
            Assert.DoesNotContain(typeof(WebsiteContentVersion), audit.EntityTypes);
            Assert.Empty(db.ChangeTracker.Entries<WebsiteContentState>());
            Assert.Empty(db.ChangeTracker.Entries<WebsiteContentVersion>());
        }
    }

    [Fact]
    public async Task HistoricalVersionOwnerResolutionProjectsIdentityWithoutPrivatePayloads()
    {
        var audit = new MaterializationAudit();
        using var db = ControllerTestHelpers.BuildDb(audit);
        var config = new ConfigurationBuilder().Build();
        var cases = new[]
        {
            (Site: WebsiteEditorSiteKeys.Legend, Owner: WebsiteEditorSiteKeys.GlobalOwnerKey, IsFounder: true),
            (Site: WebsiteEditorSiteKeys.Legend, Owner: "foreign-owner", IsFounder: false),
            (Site: WebsiteEditorSiteKeys.Protect, Owner: WebsiteEditorSiteKeys.GlobalOwnerKey, IsFounder: false),
            (Site: WebsiteEditorSiteKeys.Business, Owner: WebsiteEditorSiteKeys.BusinessOwnerKey(Guid.NewGuid()), IsFounder: false)
        };
        foreach (var item in cases)
        {
            var state = new WebsiteContentState
            {
                SiteKey = item.Site, OwnerKey = item.Owner,
                DraftJson = "private draft must not be materialized",
                NamedDraftsJson = "private named drafts must not be materialized"
            };
            var version = new WebsiteContentVersion
            {
                StateId = state.Id, Revision = 1,
                DocumentJson = "published content is not an identity input",
                CompiledPagesJson = "compiled content is not an identity input"
            };
            // Historical version ownership remains valid without a current publication.
            Assert.Null(state.PublishedVersionId);
            db.AddRange(state, version);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            audit.EntityTypes.Clear();
            var owner = await Infrastructure.Analytics.CanonicalAdvertisingEventProjection.ResolveOwnerAsync(
                db, config, new AnalyticsEvent { Id = 42, WebsiteContentVersionId = version.Id, MetadataJson = "{}" });
            if (item.IsFounder) Assert.Equal(MarketingOwnerScope.Founder, owner);
            else Assert.Null(owner);
            Assert.DoesNotContain(typeof(WebsiteContentState), audit.EntityTypes);
            Assert.DoesNotContain(typeof(WebsiteContentVersion), audit.EntityTypes);
            Assert.Empty(db.ChangeTracker.Entries<WebsiteContentState>());
            Assert.Empty(db.ChangeTracker.Entries<WebsiteContentVersion>());
        }
        Assert.Null(await Infrastructure.Analytics.CanonicalAdvertisingEventProjection.ResolveOwnerAsync(
            db, config, new AnalyticsEvent { Id = 42, WebsiteContentVersionId = Guid.NewGuid(), MetadataJson = "{}" }));
    }

    private sealed class Fixture : IDisposable
    {
        public MaterializationAudit Audit { get; } = new();
        public MasterAppDbContext Db { get; }

        public Fixture(bool auditMaterialization = false)
        {
            var options = new DbContextOptionsBuilder<MasterAppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString());
            if (auditMaterialization) options.AddInterceptors(Audit);
            Db = new(options.Options);
        }
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

    private static async Task SeedCanonicalDraftAsync(
        Fixture fixture,
        string token,
        string siteKey,
        WebsiteContentDocument document,
        long revision = 1)
    {
        _ = Body(await fixture.Controller.Manage(token));
        var state = Assert.Single(await fixture.Db.Set<Domain.Entities.WebsiteContentState>().ToListAsync());
        var canonical = WebsiteContentSanitizer.Sanitize(document);
        WebsiteSystemTemplateAuthority.Apply(siteKey, canonical);
        canonical.UpdatedUtc = DateTime.UtcNow;
        state.DraftJson = JsonSerializer.Serialize(canonical, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        state.Revision = revision;
        state.PublishedVersionId = null;
        state.ScheduledPublishUtc = null;
        state.ScheduledActorJson = null;
        state.ScheduledRevision = null;
        state.ScheduleError = null;
        state.UpdatedUtc = DateTime.UtcNow;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task PublicReadsAndPublicationReconciliationDoNotMaterializeSnapshotEntities()
    {
        using var fixture = new Fixture(auditMaterialization: true);
        var state = new Domain.Entities.WebsiteContentState
        {
            OwnerKey = WebsiteEditorSiteKeys.GlobalOwnerKey,
            SiteKey = WebsiteEditorSiteKeys.Legend,
            Revision = 8,
            DraftJson = JsonSerializer.Serialize(Document("Private draft"), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            NamedDraftsJson = "[]"
        };
        var published = Document("Published");
        published.Store.Enabled = true;
        var version = new Domain.Entities.WebsiteContentVersion
        {
            StateId = state.Id, Revision = 7,
            DocumentJson = JsonSerializer.Serialize(published, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CompiledPagesJson = "{}"
        };
        state.PublishedVersionId = version.Id;
        fixture.Db.AddRange(state, version);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        fixture.Audit.EntityTypes.Clear();

        Assert.Equal("Published", TitleText(Body(await fixture.Controller.Public(WebsiteEditorSiteKeys.Legend))));
        var status = Body(await fixture.Controller.PublicationStatus(fixture.Token, version.Id));
        Assert.True(status.GetProperty("found").GetBoolean());
        Assert.True(status.GetProperty("isCurrent").GetBoolean());
        Assert.Equal(8, status.GetProperty("revision").GetInt64());
        Assert.DoesNotContain(typeof(Domain.Entities.WebsiteContentState), fixture.Audit.EntityTypes);
        Assert.DoesNotContain(typeof(Domain.Entities.WebsiteContentVersion), fixture.Audit.EntityTypes);
    }

    [Theory]
    [InlineData("/legend-public-openai-measurement.js")]
    [InlineData("/legend-public-meta-signal-intelligence.js")]
    [InlineData("/team//history")]
    [InlineData("/TEAM/history")]
    public async Task PublicLegendHtmlNeverServesInvalidPathsEvenIfPresentInPublication(string path)
    {
        using var fixture = new Fixture();
        var state = new Domain.Entities.WebsiteContentState
        {
            OwnerKey = WebsiteEditorSiteKeys.GlobalOwnerKey,
            SiteKey = WebsiteEditorSiteKeys.Legend
        };
        var version = new Domain.Entities.WebsiteContentVersion
        {
            StateId = state.Id, Revision = 1, DocumentJson = "{}",
            CompiledPagesJson = JsonSerializer.Serialize(new
            {
                pages = new System.Collections.Generic.Dictionary<string, object>
                {
                    [path] = new { html = "<!doctype html><main>Invalid route must remain private</main>" }
                }
            })
        };
        state.PublishedVersionId = version.Id;
        fixture.Db.AddRange(state, version);
        await fixture.Db.SaveChangesAsync();
        var controller = fixture.Controller;
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext()
        };
        Assert.IsType<NotFoundResult>(await controller.LegendPage(path));
    }

    [Fact]
    public async Task PublicLegendHtmlReadsOnlyActivePrecompiledPublication()
    {
        using var fixture = new Fixture();
        var state = new Domain.Entities.WebsiteContentState
        {
            OwnerKey = WebsiteEditorSiteKeys.GlobalOwnerKey,
            SiteKey = WebsiteEditorSiteKeys.Legend
        };
        static string Page(string text) => JsonSerializer.Serialize(new
        {
            pages = new System.Collections.Generic.Dictionary<string, object>
            {
                ["/"] = new { html = "<!doctype html><main><h1>" + text + "</h1></main><a href='__LEGEND_CANONICAL_URL__'>Home</a>" },
                ["/team/history"] = new { html = "<!doctype html><main><h1>Nested " + text + "</h1></main>" }
            }
        });
        var first = new Domain.Entities.WebsiteContentVersion
        {
            StateId = state.Id, Revision = 1, DocumentJson = "{}", CompiledPagesJson = Page("First")
        };
        var second = new Domain.Entities.WebsiteContentVersion
        {
            StateId = state.Id, Revision = 2, DocumentJson = "{}", CompiledPagesJson = Page("Second")
        };
        state.PublishedVersionId = first.Id;
        fixture.Db.AddRange(state, first, second);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var controller = fixture.Controller;
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext()
        };
        var firstPage = Assert.IsType<ContentResult>(await controller.LegendPage("/"));
        Assert.Contains("<h1>First</h1>", firstPage.Content, StringComparison.Ordinal);
        Assert.Contains("https://www.mylegnd.com/", firstPage.Content, StringComparison.Ordinal);
        Assert.Equal(first.Id.ToString("D"),
            controller.Response.Headers["X-Legend-Publication-Id"].ToString());

        var active = await fixture.Db.Set<Domain.Entities.WebsiteContentState>().SingleAsync();
        active.PublishedVersionId = second.Id;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var currentPage = Assert.IsType<ContentResult>(await controller.LegendPage("/"));
        Assert.Contains("<h1>Second</h1>", currentPage.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("<h1>First</h1>", currentPage.Content, StringComparison.Ordinal);
        Assert.Equal(second.Id.ToString("D"),
            controller.Response.Headers["X-Legend-Publication-Id"].ToString());
        var nestedPage = Assert.IsType<ContentResult>(await controller.LegendPage("/team/history/"));
        Assert.Contains("<h1>Nested Second</h1>", nestedPage.Content, StringComparison.Ordinal);
        Assert.IsType<NotFoundResult>(await controller.LegendPage("/absent"));
    }

    private static WebsiteMutationOperation ReplaceTitle(
        WebsiteContentDocument document,
        string text)
    {
        var node = document.Pages["/"].Composition.Single(value => value.Id == "home.title");
        var replacement = JsonSerializer.Deserialize<WebsiteCompositionNode>(
            JsonSerializer.Serialize(node, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        replacement.Text = text;
        return new WebsiteMutationOperation
        {
            Type = "replaceNode",
            NodeId = node.Id,
            ExpectedFingerprint = WebsiteCreativeFingerprint.Node(node),
            Node = replacement
        };
    }


    [Fact]
    public async Task AdversarialConversionTraceRejectsMislinkedAndBrowserReceipts()
    {
        using var f = new Fixture(); var token = f.Token;
        await SeedCanonicalDraftAsync(f, token, WebsiteEditorSiteKeys.Legend, Document("Published"));
        var publication = Body(await f.Controller.Publish(new(token,1)));
        var source = Infrastructure.Analytics.UnifiedEventMapper.ToAnalytics(new Infrastructure.Analytics.UnifiedEventContext {
            SiteKey = "legend", Host = "www.mylegnd.com", EventName = "Lead", IsServerAuthority = true,
            EventUtc = DateTime.UtcNow, WebsiteContentVersionId = publication.GetProperty("versionId").GetGuid()
        });
        Infrastructure.Analytics.UnifiedAnalyticsWriter.Write(f.Db, source); await f.Db.SaveChangesAsync();
        foreach (var (channel, canonicalSource) in new[] {("server","AnalyticsEvent"),("browser","AnalyticsEvent"),("server","unrelated_source")})
            f.Db.Add(new Domain.Entities.MarketingDestinationDelivery {
                OwnerKey = Shared.Analytics.MarketingOwnerScope.Founder.Key, AnalyticsEventId = source.Id,
                Channel=channel, CanonicalSource=canonicalSource, Provider="openai", Status="sent", LastHttpStatusCode=200,
                CanonicalEventId=source.EventId.ToString("N")
            });
        await f.Db.SaveChangesAsync();
        var trace = Body(await f.Controller.CreativeConversionTrace(token));
        var entry = Assert.Single(trace.GetProperty("events").EnumerateArray());
        var receipt = Assert.Single(entry.GetProperty("openai").GetProperty("receipts").EnumerateArray());
        Assert.True(receipt.GetProperty("httpAccepted").GetBoolean());
        Assert.Equal("AnalyticsEvent", receipt.GetProperty("canonicalSource").GetString());
    }

    [Fact]
    public async Task PublicationRequestReconcilesWithoutCreatingAnotherVersion()
    {
        using var f = new Fixture();
        var token = f.Token;
        await SeedCanonicalDraftAsync(f, token, WebsiteEditorSiteKeys.Legend, Document("Publication"));
        var requestId = Guid.NewGuid();
        var first = Body(await f.Controller.Publish(new(token, 1, requestId)));
        Assert.Equal(requestId, first.GetProperty("versionId").GetGuid());
        var count = await f.Db.Set<Domain.Entities.WebsiteContentVersion>().CountAsync();
        var retry = Body(await f.Controller.Publish(new(token, 1, requestId)));
        Assert.True(retry.GetProperty("alreadyPublished").GetBoolean());
        Assert.False(retry.TryGetProperty("publicationRejection", out _));
        Assert.Equal(count, await f.Db.Set<Domain.Entities.WebsiteContentVersion>().CountAsync());
        var status = Body(await f.Controller.PublicationStatus(token, requestId));
        Assert.True(status.GetProperty("found").GetBoolean());
        Assert.True(status.GetProperty("isCurrent").GetBoolean());
        Assert.False(Body(await f.Controller.PublicationStatus(token, Guid.NewGuid())).GetProperty("found").GetBoolean());
        Assert.IsType<UnauthorizedResult>(await f.Controller.PublicationStatus("invalid", requestId));
        var published = Body(await f.Controller.Public("legend"));
        Assert.Equal(requestId, published.GetProperty("publication").GetProperty("versionId").GetGuid());
    }

    [Theory]
    [InlineData(3L, true)]
    [InlineData(4L, false)]
    [InlineData(5L, false)]
    [InlineData(null, false)]
    public async Task PublicationStatus_ProvesOnlyOlderAbsentRequestsTerminal(long? expectedRevision, bool terminal)
    {
        using var f = new Fixture(auditMaterialization: true);
        await SeedCanonicalDraftAsync(f, f.Token, WebsiteEditorSiteKeys.Legend, Document("Saved draft"), revision: 4);
        f.Db.ChangeTracker.Clear();
        f.Audit.EntityTypes.Clear();
        var requestId = Guid.NewGuid();
        var status = Body(await f.Controller.PublicationStatus(f.Token, requestId, expectedRevision: expectedRevision));
        Assert.False(status.GetProperty("found").GetBoolean());
        Assert.False(status.GetProperty("isCurrent").GetBoolean());
        Assert.Equal(4, status.GetProperty("revision").GetInt64());
        Assert.Equal(requestId, status.GetProperty("requestId").GetGuid());
        var receipt = status.GetProperty("publicationRejection");
        if (terminal)
        {
            Assert.Equal("rejected_before_commit", receipt.GetProperty("disposition").GetString());
            Assert.Equal(requestId, receipt.GetProperty("requestId").GetGuid());
            Assert.Equal(expectedRevision, receipt.GetProperty("expectedRevision").GetInt64());
            Assert.Equal(4, receipt.GetProperty("currentRevision").GetInt64());
        }
        else Assert.Equal(JsonValueKind.Null, receipt.ValueKind);
        Assert.DoesNotContain(typeof(WebsiteContentState), f.Audit.EntityTypes);
        Assert.DoesNotContain(typeof(WebsiteContentVersion), f.Audit.EntityTypes);
        Assert.Empty(f.Db.ChangeTracker.Entries<WebsiteContentState>());
        Assert.Empty(f.Db.ChangeTracker.Entries<WebsiteContentVersion>());
        Assert.Empty(await f.Db.Set<WebsiteContentVersion>().AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task PublicationStatus_MissingStateNeverInitializesOrRejectsAnOriginalRequest()
    {
        using var f = new Fixture();
        var status = Body(await f.Controller.PublicationStatus(f.Token, Guid.NewGuid(), expectedRevision: 0));
        Assert.False(status.GetProperty("found").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("revision").ValueKind);
        Assert.Equal(JsonValueKind.Null, status.GetProperty("publicationScopeId").ValueKind);
        Assert.Equal(JsonValueKind.Null, status.GetProperty("publicationRejection").ValueKind);
        Assert.Empty(await f.Db.Set<WebsiteContentState>().ToListAsync());
        Assert.Empty(await f.Db.Set<WebsiteContentVersion>().ToListAsync());
        Assert.IsType<BadRequestObjectResult>(
            await f.Controller.PublicationStatus(f.Token, Guid.Empty, expectedRevision: 0));
    }

    [Fact]
    public async Task PublicationRevisionPrecheckReturnsRequestBoundRejectionWithoutCommitting()
    {
        using var f = new Fixture();
        var token = f.Token;
        await SeedCanonicalDraftAsync(f, token, WebsiteEditorSiteKeys.Legend, Document("Saved draft"), revision: 4);
        var requestId = Guid.NewGuid();
        var rejected = Assert.IsType<ConflictObjectResult>(await f.Controller.Publish(new(token, 3, requestId)));
        var payload = JsonSerializer.SerializeToElement(rejected.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("revision_conflict", payload.GetProperty("error").GetString());
        var receipt = payload.GetProperty("publicationRejection");
        Assert.Equal("rejected_before_commit", receipt.GetProperty("disposition").GetString());
        Assert.Equal(requestId, receipt.GetProperty("requestId").GetGuid());
        Assert.Equal(3, receipt.GetProperty("expectedRevision").GetInt64());
        Assert.Empty(await f.Db.Set<Domain.Entities.WebsiteContentVersion>().ToListAsync());
        var state = Assert.Single(await f.Db.Set<Domain.Entities.WebsiteContentState>().AsNoTracking().ToListAsync());
        Assert.Equal(4, state.Revision);
        Assert.Null(state.PublishedVersionId);
        Assert.False(Body(await f.Controller.PublicationStatus(token, requestId)).GetProperty("found").GetBoolean());
    }

    [Fact]
    public async Task AgentContractBootstrapsCompact_AndLoadsCompleteScopedCatalogsOnDemand()
    {
        using var f = new Fixture();
        var payload = Body(await f.Controller.Manage(f.Token));
        var compact = payload.GetProperty("agentContract");
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("signalCatalog").ValueKind);
        Assert.True(compact.GetProperty("browserCommands").GetArrayLength() > 0);
        Assert.Contains("whole_site", compact.GetRawText(), StringComparison.Ordinal);

        var full = Body(await f.Controller.CreativeAgentContract(f.Token));
        var actions = full.GetProperty("availableActions");
        var signals = full.GetProperty("signalCatalog");
        Assert.True(actions.GetArrayLength() > 0);
        Assert.True(signals.GetProperty("events").GetArrayLength() > 0);

        var prompt = full.GetProperty("promptTemplate").GetString()!;
        Assert.DoesNotContain("SCOPED PRESET INVENTORY", prompt, StringComparison.Ordinal);
        Assert.Contains("Selected Source never writes Signals/FieldSignals", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProtectRuntimeIdentitySurvivesCreativeMutation_AndRejectsAnotherRoutesRuntime()
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
            SystemTemplateKey = "protect_template:life_wizard",
            Composition = [new WebsiteCompositionNode
            {
                Id = "life.runtime",
                Type = "container",
                Tag = "div",
                SystemKey = "protect_runtime_form:quote_life",
                FieldLabels = new() { ["FirstName"] = "Your first name" }
            }]
        };
        await SeedCanonicalDraftAsync(f, token, WebsiteEditorSiteKeys.Protect, draft, revision: 1);

        var saved = Body(await f.Controller.Manage(token));
        Assert.Equal("protect_runtime_form:quote_life",
            Page(saved, "/Quote/Life").GetProperty("composition")[0].GetProperty("systemKey").GetString());
        Body(await f.Controller.Publish(new(token, 1)));

        var rejected = Assert.IsType<BadRequestObjectResult>(await f.Controller.ApplyMutations(
            new WebsitePlatformController.WebsiteMutationRequest(token, 2, [
                new WebsiteMutationOperation
                {
                    Type = "replaceNode",
                    NodeId = "life.runtime",
                    Node = new WebsiteCompositionNode
                    {
                        Id = "life.runtime",
                        Type = "container",
                        Tag = "div",
                        SystemKey = "protect_runtime_form:quote_home_form",
                        FieldLabels = new() { ["FirstName"] = "Still presentation" }
                    }
                }
            ])));
        var error = JsonSerializer.SerializeToElement(rejected.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("website_mutation_protected", error.GetProperty("error").GetString());
        Assert.Equal(2, Body(await f.Controller.Manage(token)).GetProperty("revision").GetInt64());
    }

    [Fact]
    public async Task RollbackRunsCurrentPreflight_AndCannotRepublishBrokenHistoricalContent()
    {
        using var f = new Fixture();
        var token = f.Token;
        await SeedCanonicalDraftAsync(f, token, WebsiteEditorSiteKeys.Legend, Document("good"), revision: 1);
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
    public async Task DraftRemainsPrivateUntilPublish_AndStaleMutationCannotOverwrite()
    {
        using var f = new Fixture();
        var token = f.Token;

        Assert.Equal(0, Body(await f.Controller.Manage(token)).GetProperty("revision").GetInt64());
        await SeedCanonicalDraftAsync(f, token, WebsiteEditorSiteKeys.Legend, Document("draft"), revision: 1);

        var beforePublish = Body(await f.Controller.Public("legend"))
            .GetProperty("document")
            .GetProperty("pages");
        Assert.False(beforePublish.TryGetProperty("/", out _));

        Assert.IsType<ConflictObjectResult>(await f.Controller.ApplyMutations(
            new WebsitePlatformController.WebsiteMutationRequest(token, 0, [
                new WebsiteMutationOperation
                {
                    Type = "setTheme",
                    Theme = new WebsiteDesignTheme { Navy = "#000000" }
                }
            ])));

        Body(await f.Controller.Publish(new(token, 1)));
        Assert.Equal("draft", TitleText(Body(await f.Controller.Public("legend"))));
    }

    [Fact]
    public async Task RollbackRestoresPublishedSnapshot_AndMakesItCurrentDraft()
    {
        using var f = new Fixture();
        var token = f.Token;

        await SeedCanonicalDraftAsync(f, token, WebsiteEditorSiteKeys.Legend, Document("first"), revision: 1);
        var first = Body(await f.Controller.Publish(new(token, 1))).GetProperty("versionId").GetGuid();

        var current = Body(await f.Controller.Manage(token))
            .GetProperty("document")
            .Deserialize<WebsiteContentDocument>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.IsType<OkObjectResult>(await f.Controller.ApplyMutations(
            new WebsitePlatformController.WebsiteMutationRequest(token, 2, [ReplaceTitle(current, "second")])));
        Body(await f.Controller.Publish(new(token, 3)));

        current = Body(await f.Controller.Manage(token))
            .GetProperty("document")
            .Deserialize<WebsiteContentDocument>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.IsType<OkObjectResult>(await f.Controller.ApplyMutations(
            new WebsitePlatformController.WebsiteMutationRequest(token, 4, [ReplaceTitle(current, "unfinished")])));

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
    public async Task CanonicalMutationRemovalIsExact_AndNeverResurrectsOmittedNodes()
    {
        using var f = new Fixture();
        var token = f.Token;

        await SeedCanonicalDraftAsync(f, token, WebsiteEditorSiteKeys.Legend, Document("persisted"), revision: 1);
        Assert.IsType<OkObjectResult>(await f.Controller.ApplyMutations(
            new WebsitePlatformController.WebsiteMutationRequest(token, 1, [
                new WebsiteMutationOperation
                {
                    Type = "removeNode",
                    NodeId = "home.title"
                }
            ])));

        var saved = Body(await f.Controller.Manage(token));
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
    public void PersistedCanonicalV3_FailsClosedInsteadOfSelfHealing()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var passiveLink = new WebsiteContentDocument
        {
            Pages = new(StringComparer.Ordinal)
            {
                ["/"] = new WebsitePageDocument
                {
                    Composition =
                    [
                        new WebsiteCompositionNode
                        {
                            Id = "dead-link",
                            Type = "link",
                            Tag = "a",
                            Text = "Dead",
                            Href = "#"
                        }
                    ]
                }
            }
        };

        var nonCanonical = JsonSerializer.Serialize(passiveLink, options);
        var repairError = Assert.Throws<InvalidOperationException>(() =>
            WebsiteContentSanitizer.ReadPersisted(nonCanonical, options));
        Assert.Equal("website_v3_noncanonical_persisted_document", repairError.Message);

        var duplicate = new WebsiteContentDocument
        {
            Pages = new(StringComparer.Ordinal)
            {
                ["/"] = new WebsitePageDocument
                {
                    Composition =
                    [
                        new WebsiteCompositionNode { Id = "same", Type = "text", Tag = "p", Text = "One" },
                        new WebsiteCompositionNode { Id = "same", Type = "text", Tag = "p", Text = "Two" }
                    ]
                }
            }
        };

        var duplicateError = Assert.Throws<InvalidOperationException>(() =>
            WebsiteContentSanitizer.ReadPersisted(JsonSerializer.Serialize(duplicate, options), options));
        Assert.Equal("website_duplicate_node_identity:same", duplicateError.Message);
    }

    [Fact]
    public async Task FullDocumentWrite_IsAllowedOnlyForOneWayLegacyMaterialization()
    {
        using var f = new Fixture();
        var token = f.Token;

        _ = Body(await f.Controller.Manage(token));
        var state = Assert.Single(await f.Db.Set<Domain.Entities.WebsiteContentState>().ToListAsync());
        state.DraftJson =
            """
            {
              "version": 2,
              "pages": {
                "/": {
                  "title": "Legacy home",
                  "navigation": { "label": "Home", "showInNavigation": true },
                  "elements": {},
                  "sectionOrder": {},
                  "extras": []
                }
              }
            }
            """;
        state.Revision = 0;
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();

        var materialized = Body(await f.Controller.Save(new(token, Document("Materialized"), 0)));
        Assert.Equal("canonical_v3_materialization", materialized.GetProperty("source").GetString());
        Assert.Equal(1, materialized.GetProperty("revision").GetInt64());

        var retired = Assert.IsType<BadRequestObjectResult>(
            await f.Controller.Save(new(token, Document("Second full write"), 1)));
        var error = JsonSerializer.SerializeToElement(
            retired.Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("website_full_document_write_retired", error.GetProperty("error").GetString());

        Assert.Equal("Materialized", TitleText(Body(await f.Controller.Manage(token))));
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
        await SeedCanonicalDraftAsync(f, token, WebsiteEditorSiteKeys.Legend, dead, revision: 1);
        var rejectedRequestId = Guid.NewGuid();
        var rejected = Assert.IsType<BadRequestObjectResult>(await f.Controller.Publish(new(token, 1, rejectedRequestId)));
        var error = JsonSerializer.SerializeToElement(rejected.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var rejection = error.GetProperty("publicationRejection");
        Assert.Equal("rejected_before_commit", rejection.GetProperty("disposition").GetString());
        Assert.Equal(rejectedRequestId, rejection.GetProperty("requestId").GetGuid());
        Assert.Equal(1, rejection.GetProperty("expectedRevision").GetInt64());
        Assert.Empty(await f.Db.Set<Domain.Entities.WebsiteContentVersion>().ToListAsync());
        Assert.False(Body(await f.Controller.PublicationStatus(token, rejectedRequestId)).GetProperty("found").GetBoolean());

        Assert.IsType<OkObjectResult>(await f.Controller.ApplyMutations(
            new WebsitePlatformController.WebsiteMutationRequest(token, 1, [
                new WebsiteMutationOperation { Type = "removeNode", NodeId = "dead" },
                new WebsiteMutationOperation
                {
                    Type = "insertNode",
                    Scope = "page",
                    PagePath = "/",
                    Node = new WebsiteCompositionNode
                    {
                        Id = "managed",
                        Type = "cta",
                        Tag = "a",
                        Text = "Talk",
                        ActionKey = "legend_contact",
                        Href = "#"
                    }
                }
            ])));
        var correctedRequestId = Guid.NewGuid();
        var corrected = Body(await f.Controller.Publish(new(token, 2, correctedRequestId)));
        Assert.Equal(correctedRequestId, corrected.GetProperty("versionId").GetGuid());
        Assert.False(Body(await f.Controller.PublicationStatus(token, rejectedRequestId)).GetProperty("found").GetBoolean());
        Assert.Single(await f.Db.Set<Domain.Entities.WebsiteContentVersion>().ToListAsync());

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
