using System.Text.Json;
using Domain.Entities;
using Infrastructure.Analytics;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shared.Analytics;

namespace Infrastructure.WebsiteEditing;

/// <summary>Read-only projection of published configuration and scoped transport receipts.</summary>
public sealed class WebsiteEventMapQuery(MasterAppDbContext db, IConfiguration configuration)
{
    public sealed record Entry(string Owner, string Site, string Page, string Element, string? Binding,
        string? VisibleLabel, string? ActionKey, string? BehaviorKey, string CanonicalEvent,
        string Authority, string Mode, bool Locked, string? MetaMapping, string? OpenAiMapping,
        Guid? PublishedVersion, string AnalyticsStatus, string MetaStatus, string OpenAiStatus, string Trigger = "", long PublishedRevision = 0);

    public async Task<IReadOnlyList<Entry>> ReadAsync(ScopeContext scope, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(scope.ScopeType) ||
            (scope.ScopeType == ScopeType.Global && (scope.AgentTrackingProfileId.HasValue || scope.CommerceBusinessId.HasValue)) ||
            (scope.ScopeType == ScopeType.Business && (scope.AgentTrackingProfileId.HasValue || scope.CommerceBusinessId is null || scope.CommerceBusinessId == Guid.Empty))) return [];
        if (scope.ScopeType is ScopeType.Agent or ScopeType.Founder)
        {
            if (scope.AgentTrackingProfileId is null || scope.AgentTrackingProfileId == Guid.Empty || scope.CommerceBusinessId.HasValue) return [];
            var profile = await db.AgentTrackingProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.Id == scope.AgentTrackingProfileId, ct);
            if (profile is null) return [];
            var owner = await CanonicalAdvertisingEventProjection.ResolveOwnerAsync(db, configuration, profile, ct);
            if (owner is null || (scope.ScopeType == ScopeType.Founder && owner != MarketingOwnerScope.Founder)) return [];
        }
        var ids = await AnalyticsTrackingProfileScope.ResolveAsync(db, scope, ct);
        var states = db.Set<WebsiteContentState>().AsNoTracking();
        if (scope.ScopeType is ScopeType.Agent or ScopeType.Founder)
        {
            var ownerKeys = await db.AgentTrackingProfiles.AsNoTracking()
                .Where(p => ids != null && ids.Contains(p.Id)).Select(p => p.AgentUserId).ToListAsync(ct);
            var founder = scope.ScopeType == ScopeType.Founder;
            states = states.Where(s => (s.SiteKey == WebsiteEditorSiteKeys.Protect && ownerKeys.Contains(s.OwnerKey)) ||
                (founder && s.SiteKey == WebsiteEditorSiteKeys.Legend && s.OwnerKey == WebsiteEditorSiteKeys.GlobalOwnerKey));
        }
        else if (scope.ScopeType == ScopeType.Business)
            {
            var ownerKey = WebsiteEditorSiteKeys.BusinessOwnerKey(scope.CommerceBusinessId!.Value);
            states = states.Where(s => s.SiteKey == WebsiteEditorSiteKeys.Business && s.CommerceBusinessId == scope.CommerceBusinessId && s.OwnerKey == ownerKey);
        }
        if (!string.IsNullOrWhiteSpace(scope.SiteKey)) states = states.Where(s => s.SiteKey == scope.SiteKey);
        var range = new TimeRangeRequest { FromUtc = DateTime.UtcNow.AddDays(-30), ToUtc = DateTime.UtcNow, QualityMode = TrafficQualityMode.AllTraffic };
        var events = await new AnalyticsQueryService(db, configuration).LoadAttributedEventsAsync(range, scope, ct: ct);
        return await ReadStatesAsync(await states.ToListAsync(ct), events, ct);
    }

    public async Task<IReadOnlyList<Entry>> ReadTicketAsync(WebsiteEditorTicket actor, CancellationToken ct = default)
    {
        // AuthorizeAsync has validated this ticket; exact state ownership is the publication boundary.
        var owner = await CanonicalAdvertisingEventProjection.ResolveOwnerAsync(db, configuration, actor, ct);
        if (owner is null) return [];
        var states = await db.Set<WebsiteContentState>().AsNoTracking().Where(s =>
            s.OwnerKey == actor.OwnerUserId && s.SiteKey == actor.SiteKey &&
            (actor.SiteKey != WebsiteEditorSiteKeys.Business || s.CommerceBusinessId == actor.CommerceBusinessId)).ToListAsync(ct);
        var versions = states.Where(s => s.PublishedVersionId.HasValue).Select(s => s.PublishedVersionId!.Value).ToArray();
        var events = await new AnalyticsQueryService(db, configuration).LoadOwnerEventsAsync(DateTime.UtcNow.AddDays(-30), owner, ct);
        var ownedEvents = events.Where(e => e.WebsiteContentVersionId.HasValue && versions.Contains(e.WebsiteContentVersionId.Value)).ToList();
        return await ReadStatesAsync(states, ownedEvents, ct);
    }

    private async Task<IReadOnlyList<Entry>> ReadStatesAsync(List<WebsiteContentState> states, List<AnalyticsEvent> events, CancellationToken ct)
    {
        var versionIds = states.Where(s => s.PublishedVersionId.HasValue).Select(s => s.PublishedVersionId!.Value).ToArray();
        var versions = await db.Set<WebsiteContentVersion>().AsNoTracking().Where(v => versionIds.Contains(v.Id)).ToListAsync(ct);
        var sourceIds = events.Select(e => e.Id).ToArray();
        var receipts = await db.Set<MarketingDestinationDelivery>().AsNoTracking().Where(r => r.AnalyticsEventId.HasValue && sourceIds.Contains(r.AnalyticsEventId.Value)).ToListAsync(ct);
        var meta = await db.MetaSignalEvents.AsNoTracking().Where(m => m.WebsiteContentVersionId.HasValue && versionIds.Contains(m.WebsiteContentVersionId.Value)).ToListAsync(ct);
        var historicalReceipts = new Dictionary<long, MarketingDestinationDelivery>();
        foreach (var source in events)
        {
            var mapping = MarketingConversionDestinationCatalog.ResolveOpenAi(CanonicalAdvertisingEventProjection.ResolveEventName(source));
            if (mapping is null || receipts.Any(r => r.Provider == MarketingDestinationKeys.OpenAi && r.AnalyticsEventId == source.Id)) continue;
            var owner = await CanonicalAdvertisingEventProjection.ResolveOwnerAsync(db, configuration, source, ct);
            if (owner is not null && await OpenAiConversionDispatcherHostedService.FindHistoricalReceiptAsync(db, owner, source, mapping.EventName, ct) is { } receipt)
                historicalReceipts[source.Id] = receipt;
        }
        var result = new List<Entry>();
        foreach (var state in states)
        {
            var version = versions.SingleOrDefault(v => v.Id == state.PublishedVersionId && v.StateId == state.Id);
            WebsiteContentDocument document;
            try { document = version is null ? new() : JsonSerializer.Deserialize<WebsiteContentDocument>(version.DocumentJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new(); }
            catch (JsonException) { continue; }
            var stateOwner = await CanonicalAdvertisingEventProjection.ResolveOwnerAsync(db, configuration, state, ct);
            var stateEvents = new List<AnalyticsEvent>();
            if (stateOwner is not null)
                foreach (var source in events.Where(e => e.WebsiteContentVersionId == version?.Id))
                    if (await CanonicalAdvertisingEventProjection.ResolveOwnerAsync(db, configuration, source, ct) == stateOwner)
                        stateEvents.Add(source);
            void Add(string page, string element, string? label, string? action, WebsiteSignalBinding? binding, AnalyticsBehaviorContract? behavior, bool automatic)
            {
                var eventName = behavior?.EventName ?? binding?.EventName ?? "unresolved";
                var sources = stateEvents.Where(e => e.WebsiteContentVersionId == version?.Id &&
                    (binding is not null ? e.WebsiteBindingId == binding.Id : automatic
                        ? AnalyticsEventCatalog.TryGetBehavior(e.EventType, out var b) && b.Key == behavior?.Key
                        : action is not null && CanonicalAdvertisingEventProjection.ReadString(e.MetadataJson, "actionKey") == action &&
                          (e.ElementKey == element || e.ElementId == element) &&
                          (page == "*" || e.Path == page || e.PageKey == page))).ToArray();
                var selectedIds = sources.Select(e => e.Id).ToHashSet();
                var deliveries = receipts.Where(r => r.OwnerKey == stateOwner?.Key && r.AnalyticsEventId.HasValue && selectedIds.Contains(r.AnalyticsEventId.Value))
                    .Concat(historicalReceipts.Where(pair => selectedIds.Contains(pair.Key)).Select(pair => pair.Value)).ToArray();
                string Status(string provider) => deliveries.Where(r => r.Provider == provider).OrderByDescending(r => r.UpdatedUtc).FirstOrDefault()?.Status ?? "not_observed";
                var metaRows = meta.Where(m => m.WebsiteContentVersionId == version?.Id &&
                    (CanonicalAdvertisingEventProjection.ReadInt64(m.MetadataJson, "sourceAnalyticsEventId") is { } id &&
                     sources.Any(source => source.Id == id && source.AgentTrackingProfileId == m.AgentTrackingProfileId && source.CommerceBusinessId == m.CommerceBusinessId))).ToArray();
                var conversion = behavior?.ConversionEventName;
                var signal = MetaSignalAnalyticsAliasCatalog.ResolveSignalName(eventName);
                result.Add(new(stateOwner?.Key ?? "unresolved", state.SiteKey, page, element, binding?.Id, label, action,
                    behavior?.Key, eventName, behavior?.RequiresServerAuthority == true ? "verified_server" : "browser",
                    automatic ? "automatic" : "manual", automatic || behavior?.RequiresServerAuthority == true,
                    MarketingConversionDestinationCatalog.ResolveMeta(conversion)?.EventName ?? signal,
                    MarketingConversionDestinationCatalog.ResolveOpenAi(conversion)?.EventName ?? (behavior?.Key == "page_view" ? OpenAiMeasurementEventNames.PageViewed : null),
                    version?.Id, version is null ? "not_published" : sources.Length > 0 ? "observed" : "not_observed",
                    metaRows.Any(m => m.MetaServerSent || m.MetaBrowserSent) ? "sent" : metaRows.Length > 0 ? "projected" : Status("meta"), Status("openai"),
                    binding?.Trigger ?? (automatic ? behavior?.AutomaticTrigger : "click") ?? "", version?.Revision ?? 0));
            }
            void Element(string page, string key, string? label, string? action, List<WebsiteSignalBinding> bindings)
            {
                foreach (var binding in bindings)
                {
                    AnalyticsEventCatalog.TryGetBehavior(binding.EventName, out var behavior);
                    Add(page, key, label, action ?? binding.ActionKey, binding, behavior, false);
                }
                if (!string.IsNullOrWhiteSpace(action))
                {
                    WebsiteCallToActionCatalog.TryResolveBehavior(state.SiteKey, action, out var behavior);
                    Add(page, key, label, action, null, behavior, false);
                }
            }
            foreach (var page in document.Pages)
            {
                foreach (var element in page.Value.Elements) Element(page.Key, element.Key, element.Value.Text, element.Value.ActionKey, element.Value.Signals);
                foreach (var extra in page.Value.Extras) Element(page.Key, "extra:" + extra.Id, extra.Text ?? extra.Title, extra.ActionKey, extra.Signals);
            }
            foreach (var element in document.Elements) Element("*", element.Key, element.Value.Text, element.Value.ActionKey, element.Value.Signals);
            foreach (var extra in document.Extras) Element("*", "extra:" + extra.Id, extra.Text ?? extra.Title, extra.ActionKey, extra.Signals);
            foreach (var behavior in AnalyticsEventCatalog.Behaviors.Where(b => !string.IsNullOrWhiteSpace(b.AutomaticTrigger)))
                Add("*", "automatic:" + behavior.Key, behavior.DisplayLabel, behavior.Key, null, behavior, true);
        }
        return result;
    }

}
