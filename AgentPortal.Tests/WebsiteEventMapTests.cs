using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.WebsiteEditing;
using Microsoft.Extensions.Configuration;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class WebsiteEventMapTests
{
    [Theory]
    [InlineData("browser", "AnalyticsEvent", "not_observed")]
    [InlineData("server", "unrelated_source", "not_observed")]
    [InlineData("server", "AnalyticsEvent", "http_accepted")]
    public async Task AdversarialReceiptCannotClaimServerDeliveryFromWrongChannelOrSource(string channel, string canonicalSource, string expected)
    {
        using var db = ControllerTestHelpers.BuildDb();
        var profile = new AgentTrackingProfile { AgentUserId = "agent", AgentUpn = "agent@example.test", Slug = "agent" };
        var state = new WebsiteContentState { OwnerKey = profile.AgentUserId, SiteKey = WebsiteEditorSiteKeys.Protect };
        var version = new WebsiteContentVersion { StateId = state.Id, DocumentJson = "{}" };
        state.PublishedVersionId = version.Id;
        var source = new AnalyticsEvent { EventId = Guid.NewGuid(), EventType = "website_lead_submitted", EventUtc = DateTime.UtcNow,
            ReceivedUtc = DateTime.UtcNow, AgentTrackingProfileId = profile.Id, WebsiteContentVersionId = version.Id };
        db.AddRange(profile, state, version, source);
        await db.SaveChangesAsync();
        db.Add(new MarketingDestinationDelivery { OwnerKey = MarketingOwnerScope.Agent(profile.Id).Key, AgentTrackingProfileId = profile.Id,
            AnalyticsEventId = source.Id, CanonicalSource = canonicalSource, Channel = channel, Provider = "openai",
            Status = "sent", LastHttpStatusCode = 200, CanonicalEventId = source.EventId.ToString("N") });
        await db.SaveChangesAsync();
        var ticket = new WebsiteEditorTicket(state.SiteKey, profile.AgentUserId, profile.Slug, false, DateTime.UtcNow.AddMinutes(5));
        var map = await new WebsiteEventMapQuery(db, new ConfigurationBuilder().Build()).ReadTicketAsync(ticket);
        Assert.Equal(expected, Assert.Single(map.Where(row => row.Element == "automatic:lead_created")).OpenAiStatus);
    }

    [Fact]
    public async Task ReusableMapIncludesOnlyRenderedInstanceActionsWithoutDefinitionSignals()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var profile = new AgentTrackingProfile { AgentUserId = "agent", AgentUpn = "agent@example.test", Slug = "agent" };
        var state = new WebsiteContentState { OwnerKey = profile.AgentUserId, SiteKey = WebsiteEditorSiteKeys.Protect };
        var binding = new WebsiteSignalBinding { EventName = "cta_click", Trigger = "click", DeliveryMode = "analytics" };
        var document = WebsiteContentSanitizer.Sanitize(new WebsiteContentDocument
        {
            Pages = new() { ["/contact"] = new() { Composition = [new() { Id = "instance", Type = "reusable", SyncSourceId = "used" }] } },
            ReusableComponents = new()
            {
                ["used"] = new() { Id = "used", Name = "Used", Composition = [new() { Id = "button", Type = "cta", Tag = "a", Text = "Book", ActionKey = "protect_schedule", Signals = [binding] }] },
                ["unused"] = new() { Id = "unused", Name = "Unused", Composition = [new() { Id = "unused.button", Type = "cta", Tag = "a", Text = "Unused", ActionKey = "protect_schedule" }] }
            }
        });
        var version = new WebsiteContentVersion { StateId = state.Id, DocumentJson = JsonSerializer.Serialize(document, new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        state.PublishedVersionId = version.Id;
        db.AddRange(profile, state, version);
        await db.SaveChangesAsync();
        var ticket = new WebsiteEditorTicket(state.SiteKey, profile.AgentUserId, profile.Slug, false, DateTime.UtcNow.AddMinutes(5));
        var rows = await new WebsiteEventMapQuery(db, new ConfigurationBuilder().Build()).ReadTicketAsync(ticket);
        Assert.Single(rows.Where(r => r.Element == "instance.button" && r.ActionKey == "protect_schedule"));
        Assert.DoesNotContain(rows, r => r.Element == "unused.button" || r.Binding == binding.Id);
        Assert.Null(PublishedWebsiteBindingResolver.Resolve(version, state.SiteKey, "/contact", binding.Id));
    }

    [Fact]
    public async Task SharedBindingsResolveAndFieldReportingRequiresExactElementIdentity()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var profile = new AgentTrackingProfile { AgentUserId = "agent", AgentUpn = "agent@example.test", Slug = "agent" };
        var state = new WebsiteContentState { OwnerKey = profile.AgentUserId, SiteKey = WebsiteEditorSiteKeys.Protect };
        var binding = new WebsiteSignalBinding { EventName = "cta_click", Trigger = "click", DeliveryMode = "analytics" };
        var fieldBinding = new WebsiteSignalBinding { EventName = "form_start", Trigger = "form_started", DeliveryMode = "analytics" };
        var document = new WebsiteContentDocument
        {
            Shell = new() { Header = [new() { Id = "shared", Type = "cta", Tag = "a", Signals = [binding] }] },
            Pages = new() { ["/contact"] = new() { Composition = [new() {
                Id = "form", Type = "experience", Tag = "form",
                Experience = new() { Kind = "calculator", Controls = [
                    new() { Key = "email", Type = "text", Label = "Email" },
                    new() { Key = "phone", Type = "text", Label = "Phone" },
                    new() { Key = "schedule", Type = "button", Label = "Book", Action = new() { Type = "cta", ActionKey = "protect_schedule" } }
                ] },
                FieldSignals = new() { ["email"] = [fieldBinding], ["phone"] = [fieldBinding] }
            }] } }
        };
        document = WebsiteContentSanitizer.Sanitize(document);
        var version = new WebsiteContentVersion { StateId = state.Id, DocumentJson = JsonSerializer.Serialize(document, new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        state.PublishedVersionId = version.Id;
        db.AddRange(profile, state, version);
        db.Add(new AnalyticsEvent { EventId = Guid.NewGuid(), EventType = "form_start", EventUtc = DateTime.UtcNow, ReceivedUtc = DateTime.UtcNow,
            AgentTrackingProfileId = profile.Id, WebsiteContentVersionId = version.Id, WebsiteBindingId = fieldBinding.Id,
            Path = "/contact", ElementKey = "form:field:email", ElementId = "form:field:phone" });
        await db.SaveChangesAsync();
        Assert.Equal("shared", PublishedWebsiteBindingResolver.Resolve(version, state.SiteKey, "/contact", binding.Id)?.ElementId);
        Assert.Null(PublishedWebsiteBindingResolver.Resolve(version, state.SiteKey, "/missing", binding.Id));
        var ticket = new WebsiteEditorTicket(state.SiteKey, profile.AgentUserId, profile.Slug, false, DateTime.UtcNow.AddMinutes(5));
        var rows = await new WebsiteEventMapQuery(db, new ConfigurationBuilder().Build()).ReadTicketAsync(ticket);
        Assert.Equal("observed", Assert.Single(rows.Where(r => r.Element == "form:field:email")).AnalyticsStatus);
        Assert.Equal("not_observed", Assert.Single(rows.Where(r => r.Element == "form:field:phone")).AnalyticsStatus);
        Assert.Equal("protect_schedule", Assert.Single(rows.Where(r => r.Element == "form:field:schedule")).ActionKey);
    }

    [Fact]
    public async Task PublishedMapSeparatesLabelsFromIdentityAndDoesNotReadDraftOrOtherOwner()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var agent = new AgentTrackingProfile { Id = Guid.NewGuid(), AgentUserId = "agent-a", AgentUpn = "a@example.com", Slug = "a" };
        db.Add(agent);
        var state = new WebsiteContentState { OwnerKey = agent.AgentUserId, SiteKey = WebsiteEditorSiteKeys.Protect, DraftJson = "{\"pages\":{\"/draft-only\":{}}}" };
        var binding = new WebsiteSignalBinding { EventName = "LeadFormStart", Trigger = "click", DeliveryMode = "analytics" };
        var document = new WebsiteContentDocument
        {
            Pages = new()
            {
                ["/contact"] = new()
                {
                    Composition =
                    [
                        new WebsiteCompositionNode
                        {
                            Id = "button",
                            Type = "cta",
                            Tag = "a",
                            Text = "Purchase",
                            ActionKey = "custom-unresolved-action",
                            Signals = [binding]
                        }
                    ]
                }
            }
        };
        document = WebsiteContentSanitizer.Sanitize(document);
        WebsiteSystemTemplateAuthority.Apply(WebsiteEditorSiteKeys.Protect, document);
        var version = new WebsiteContentVersion { StateId = state.Id, DocumentJson = JsonSerializer.Serialize(document, new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        state.PublishedVersionId = version.Id;
        db.AddRange(state, version, new WebsiteContentState { OwnerKey = "other", SiteKey = WebsiteEditorSiteKeys.Protect });
        await db.SaveChangesAsync();
        var rows = await new WebsiteEventMapQuery(db, new ConfigurationBuilder().Build()).ReadAsync(ScopeContext.ForAgent(agent.Id));
        Assert.All(rows, row => Assert.Equal(MarketingOwnerScope.Agent(agent.Id).Key, row.Owner));
        var mapped = Assert.Single(rows.Where(row => row.Binding == binding.Id));
        Assert.Equal("Purchase", mapped.VisibleLabel);
        Assert.Equal("custom-unresolved-action", mapped.ActionKey);
        Assert.Equal("form_start", mapped.BehaviorKey);
        Assert.Equal("form_start", mapped.CanonicalEvent);
        Assert.Equal(version.Id, mapped.PublishedVersion);
        Assert.DoesNotContain(rows, row => row.Page == "/draft-only");
        var outcome = Assert.Single(rows.Where(row => row.Element == "automatic:appointment_booked"));
        Assert.True(outcome.Locked);
        Assert.Equal("verified_server", outcome.Authority);
        Assert.Equal("appointment_scheduled", outcome.OpenAiMapping);
        Assert.Equal("not_observed", outcome.AnalyticsStatus);
        Assert.Empty(await new WebsiteEventMapQuery(db, new ConfigurationBuilder().Build()).ReadAsync(ScopeContext.ForAgent(Guid.Empty)));
    }

    [Fact]
    public async Task TicketMapRequiresExactOwnerSiteAndBusinessAndNeverCreatesDraft()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var business = Guid.NewGuid();
        db.Add(new CommerceBusiness { Id = business, IsActive = true, Status = "Active" });
        db.Add(new WebsiteContentState { OwnerKey = WebsiteEditorSiteKeys.BusinessOwnerKey(business), SiteKey = WebsiteEditorSiteKeys.Business, CommerceBusinessId = business });
        await db.SaveChangesAsync();
        var query = new WebsiteEventMapQuery(db, new ConfigurationBuilder().Build());
        var ticket = new WebsiteEditorTicket(WebsiteEditorSiteKeys.Business, WebsiteEditorSiteKeys.BusinessOwnerKey(business), null, false, DateTime.UtcNow.AddMinutes(5), business);
        Assert.NotEmpty(await query.ReadTicketAsync(ticket));
        Assert.Empty(await query.ReadTicketAsync(ticket with { CommerceBusinessId = Guid.NewGuid() }));
        Assert.Empty(await query.ReadTicketAsync(ticket with { OwnerUserId = "missing" }));
        Assert.Single(db.Set<WebsiteContentState>());
    }
    [Fact]
    public async Task MalformedFounderAndMixedScopesCannotReadGlobalLegend()
    {
        using var db = ControllerTestHelpers.BuildDb();
        db.Add(new WebsiteContentState { OwnerKey = WebsiteEditorSiteKeys.GlobalOwnerKey, SiteKey = WebsiteEditorSiteKeys.Legend });
        var profile = new AgentTrackingProfile { AgentUserId = "ordinary", AgentUpn = "ordinary@example.test", Slug = "ordinary" };
        db.Add(profile);
        await db.SaveChangesAsync();
        var query = new WebsiteEventMapQuery(db, new ConfigurationBuilder().Build());
        foreach (var scope in new[] {
            new ScopeContext { ScopeType = ScopeType.Founder },
            new ScopeContext { ScopeType = ScopeType.Founder, AgentTrackingProfileId = Guid.Empty },
            ScopeContext.ForFounder(Guid.NewGuid()), ScopeContext.ForFounder(profile.Id),
            new ScopeContext { ScopeType = ScopeType.Founder, AgentTrackingProfileId = profile.Id, CommerceBusinessId = Guid.NewGuid() },
            new ScopeContext { ScopeType = ScopeType.Business, CommerceBusinessId = Guid.NewGuid(), AgentTrackingProfileId = profile.Id },
            new ScopeContext { ScopeType = ScopeType.Global, AgentTrackingProfileId = profile.Id } })
            Assert.Empty(await query.ReadAsync(scope));
    }

    [Fact]
    public async Task ManagedActionUsesRecordedIdentityAndTicketRejectsForeignOwnerWithCopiedVersion()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var profile = new AgentTrackingProfile { AgentUserId = "agent", AgentUpn = "agent@example.test", Slug = "agent" };
        var other = new AgentTrackingProfile { AgentUserId = "other", AgentUpn = "other@example.test", Slug = "other" };
        var state = new WebsiteContentState { OwnerKey = profile.AgentUserId, SiteKey = WebsiteEditorSiteKeys.Protect };
        var document = new WebsiteContentDocument
        {
            Pages = new()
            {
                ["/contact"] = new()
                {
                    Composition =
                    [
                        new WebsiteCompositionNode
                        {
                            Id = "schedule",
                            Type = "cta",
                            Tag = "a",
                            Text = "Book now",
                            ActionKey = "protect_schedule"
                        },
                        new WebsiteCompositionNode
                        {
                            Id = "unobserved",
                            Type = "cta",
                            Tag = "a",
                            Text = "Book now",
                            ActionKey = "protect_schedule"
                        }
                    ]
                }
            }
        };
        document = WebsiteContentSanitizer.Sanitize(document);
        WebsiteSystemTemplateAuthority.Apply(WebsiteEditorSiteKeys.Protect, document);
        var version = new WebsiteContentVersion { StateId = state.Id, DocumentJson = JsonSerializer.Serialize(document, new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        state.PublishedVersionId = version.Id;
        db.AddRange(profile, other, state, version);
        db.AddRange(new AnalyticsEvent { EventId = Guid.NewGuid(), EventType = "cta_click", EventUtc = DateTime.UtcNow, ReceivedUtc = DateTime.UtcNow,
            AgentTrackingProfileId = profile.Id, WebsiteContentVersionId = version.Id, Path = "/contact", ElementKey = "schedule", MetadataJson = "{\"actionKey\":\"protect_schedule\"}" },
            new AnalyticsEvent { EventId = Guid.NewGuid(), EventType = "cta_click", EventUtc = DateTime.UtcNow, ReceivedUtc = DateTime.UtcNow,
            AgentTrackingProfileId = other.Id, WebsiteContentVersionId = version.Id, Path = "/contact", ElementKey = "unobserved", MetadataJson = "{\"actionKey\":\"protect_schedule\"}" });
        await db.SaveChangesAsync();
        var ticket = new WebsiteEditorTicket(WebsiteEditorSiteKeys.Protect, profile.AgentUserId, profile.Slug, false, DateTime.UtcNow.AddMinutes(5));
        var rows = await new WebsiteEventMapQuery(db, new ConfigurationBuilder().Build()).ReadTicketAsync(ticket);
        var action = Assert.Single(rows.Where(r => r.Element == "schedule"));
        Assert.Equal("cta_click", action.BehaviorKey);
        Assert.Equal("observed", action.AnalyticsStatus);
        Assert.Equal("not_observed", Assert.Single(rows.Where(r => r.Element == "unobserved")).AnalyticsStatus);
    }

    [Fact]
    public async Task HistoricalReceiptIsVisibleWithoutLinkingOrChangingItsIdentity()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var profile = new AgentTrackingProfile { AgentUserId = "agent", AgentUpn = "agent@example.test", Slug = "agent" };
        var state = new WebsiteContentState { OwnerKey = profile.AgentUserId, SiteKey = WebsiteEditorSiteKeys.Protect };
        var version = new WebsiteContentVersion { StateId = state.Id, DocumentJson = "{}" };
        state.PublishedVersionId = version.Id;
        var source = new AnalyticsEvent { EventId = Guid.NewGuid(), EventType = "website_lead_submitted", EventUtc = DateTime.UtcNow, ReceivedUtc = DateTime.UtcNow,
            AgentTrackingProfileId = profile.Id, WebsiteContentVersionId = version.Id };
        db.AddRange(profile, state, version, source);
        await db.SaveChangesAsync();
        var meta = new MetaSignalEvent { EventId = "historical-lead-id", EventName = "Lead", AgentTrackingProfileId = profile.Id,
            WebsiteContentVersionId = version.Id, MetadataJson = JsonSerializer.Serialize(new { sourceAnalyticsEventId = source.Id }) };
        var receipt = new MarketingDestinationDelivery { OwnerKey = MarketingOwnerScope.Agent(profile.Id).Key, Provider = MarketingDestinationKeys.OpenAi,
            Channel = "server", CanonicalSource = nameof(MetaSignalEvent), CanonicalEventId = meta.EventId,
            ProviderEventName = OpenAiMeasurementEventNames.LeadCreated, Status = "sent" };
        // A foreign legacy receipt with a colliding provider event ID must
        // neither lend acceptance nor hide this owner's actual receipt.
        db.Add(new MarketingDestinationDelivery { OwnerKey = MarketingOwnerScope.Agent(Guid.NewGuid()).Key,
            Provider = MarketingDestinationKeys.OpenAi, Channel = "server", CanonicalSource = nameof(MetaSignalEvent),
            CanonicalEventId = meta.EventId, Status = "sent", LastHttpStatusCode = 200 });
        await db.SaveChangesAsync();
        db.AddRange(meta, receipt);
        await db.SaveChangesAsync();
        var ticket = new WebsiteEditorTicket(WebsiteEditorSiteKeys.Protect, profile.AgentUserId, profile.Slug, false, DateTime.UtcNow.AddMinutes(5));
        var rows = await new WebsiteEventMapQuery(db, new ConfigurationBuilder().Build()).ReadTicketAsync(ticket);
        Assert.Equal("receipt_unverified", Assert.Single(rows.Where(r => r.Element == "automatic:lead_created")).OpenAiStatus);
        Assert.Null(receipt.AnalyticsEventId);
        Assert.Equal("historical-lead-id", receipt.CanonicalEventId);
        Assert.Equal(Microsoft.EntityFrameworkCore.EntityState.Unchanged, db.Entry(receipt).State);
    }

}
