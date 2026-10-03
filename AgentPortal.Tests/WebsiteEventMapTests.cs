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
        db.AddRange(meta, receipt);
        await db.SaveChangesAsync();
        var ticket = new WebsiteEditorTicket(WebsiteEditorSiteKeys.Protect, profile.AgentUserId, profile.Slug, false, DateTime.UtcNow.AddMinutes(5));
        var rows = await new WebsiteEventMapQuery(db, new ConfigurationBuilder().Build()).ReadTicketAsync(ticket);
        Assert.Equal("sent", Assert.Single(rows.Where(r => r.Element == "automatic:lead_created")).OpenAiStatus);
        Assert.Null(receipt.AnalyticsEventId);
        Assert.Equal("historical-lead-id", receipt.CanonicalEventId);
        Assert.Equal(Microsoft.EntityFrameworkCore.EntityState.Unchanged, db.Entry(receipt).State);
    }

}
