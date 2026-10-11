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
        var receipts = await db.Set<MarketingDestinationDelivery>().AsNoTracking().Where(r => r.Channel == "server" && r.CanonicalSource == nameof(AnalyticsEvent) &&
                r.AnalyticsEventId.HasValue && sourceIds.Contains(r.AnalyticsEventId.Value)).ToListAsync(ct);
        var meta = await db.MetaSignalEvents.AsNoTracking().Where(m => m.WebsiteContentVersionId.HasValue && versionIds.Contains(m.WebsiteContentVersionId.Value)).ToListAsync(ct);
        var historicalReceipts = new Dictionary<long, MarketingDestinationDelivery>();
        var legacyEventIds = meta.Select(m => m.EventId).ToArray();
        var legacyReceipts = await db.MarketingDestinationDeliveries.AsNoTracking()
            .Where(r => r.Channel == "server" && r.CanonicalSource == nameof(MetaSignalEvent) && r.Provider == MarketingDestinationKeys.OpenAi && legacyEventIds.Contains(r.CanonicalEventId))
            .ToListAsync(ct);
        foreach (var m in meta)
            if (CanonicalAdvertisingEventProjection.ReadInt64(m.MetadataJson, "sourceAnalyticsEventId") is { } id &&
                events.Any(e => e.Id == id && e.AgentTrackingProfileId == m.AgentTrackingProfileId && e.CommerceBusinessId == m.CommerceBusinessId))
            {
                var source = events.Single(e => e.Id == id);
                var sourceOwner = await CanonicalAdvertisingEventProjection.ResolveOwnerAsync(db, configuration, source, ct);
                if (sourceOwner is null) continue;
                var receipt = legacyReceipts.FirstOrDefault(r => r.CanonicalEventId == m.EventId && r.OwnerKey == sourceOwner.Key);
                if (receipt is not null) historicalReceipts[id] = receipt;
            }
        var result = new List<Entry>();
        foreach (var state in states)
        {
            var version = versions.SingleOrDefault(v => v.Id == state.PublishedVersionId && v.StateId == state.Id);
            WebsiteContentDocument document;
            try { document = version is null ? new() : WebsiteContentSanitizer.ReadPersisted(version.DocumentJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException) { continue; }
            var stateOwner = await CanonicalAdvertisingEventProjection.ResolveOwnerAsync(db, configuration, state, ct);
            var stateEvents = new List<AnalyticsEvent>();
            if (stateOwner is not null)
                foreach (var group in events.Where(e => e.WebsiteContentVersionId == version?.Id)
                    .GroupBy(e => new { e.CommerceBusinessId, e.AgentTrackingProfileId, e.AgentSlug, e.Host }))
                    if (await CanonicalAdvertisingEventProjection.ResolveOwnerAsync(db, configuration, group.First(), ct) == stateOwner)
                        stateEvents.AddRange(group);
            void Add(string page, string element, string? label, string? action, WebsiteSignalBinding? binding, AnalyticsBehaviorContract? behavior, bool automatic)
            {
                var eventName = behavior?.EventName ?? binding?.EventName ?? "unresolved";
                var sources = stateEvents.Where(e => e.WebsiteContentVersionId == version?.Id &&
                    (binding is not null ? e.WebsiteBindingId == binding.Id &&
                        ((string.IsNullOrWhiteSpace(e.ElementKey) ? e.ElementId : e.ElementKey) == element) &&
                        (page == "*" || page.StartsWith("@shell/", StringComparison.Ordinal) || e.Path == page || e.PageKey == page) : automatic
                        ? AnalyticsEventCatalog.TryGetBehavior(e.EventType, out var b) && b.Key == behavior?.Key
                        : action is not null && CanonicalAdvertisingEventProjection.ReadString(e.MetadataJson, "actionKey") == action &&
                          ((string.IsNullOrWhiteSpace(e.ElementKey) ? e.ElementId : e.ElementKey) == element) &&
                          (page == "*" || page.StartsWith("@shell/", StringComparison.Ordinal) || e.Path == page || e.PageKey == page))).ToArray();
                var selectedIds = sources.Select(e => e.Id).ToHashSet();
                var deliveries = receipts.Where(r => r.OwnerKey == stateOwner?.Key && r.AgentTrackingProfileId == stateOwner?.AgentTrackingProfileId &&
                    r.CommerceBusinessId == stateOwner?.CommerceBusinessId && r.AnalyticsEventId.HasValue && selectedIds.Contains(r.AnalyticsEventId.Value))
                    .Concat(historicalReceipts.Where(pair => selectedIds.Contains(pair.Key)).Select(pair => pair.Value).Where(r => r.OwnerKey == stateOwner?.Key)).ToArray();
                var requiresReconciliation = sources.Any(e => CanonicalAdvertisingEventProjection.ReadString(e.MetadataJson, "providerCorrectionStatus") != null);
                string Status(string provider)
                {
                    if (requiresReconciliation) return "requires_reconciliation";
                    var rows = deliveries.Where(r => r.Provider == provider).ToArray();
                    if (rows.Length == 0) return sources.Any(e => CanonicalAdvertisingEventProjection.ReadString(e.MetadataJson, provider + "ProjectionBlock") != null) ? "blocked" : "not_observed";
                    if (rows.All(Infrastructure.Analytics.MarketingDeliveryEvidencePolicy.HttpTransportAccepted)) return "http_accepted";
                    if (rows.All(r => r.Status == "sent")) return "receipt_unverified";
                    if (rows.Any(r => r.Status == "sent")) return "partial_delivery";
                    return rows.OrderByDescending(r => r.UpdatedUtc).First().Status;
                }
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
                    requiresReconciliation ? "requires_reconciliation" : metaRows.Length > 0 && metaRows.All(Infrastructure.Analytics.MarketingDeliveryEvidencePolicy.MetaProviderAccepted) ? "provider_accepted" : metaRows.Any(m => m.MetaServerSent) ? "partial_delivery" : metaRows.Any(m => CanonicalAdvertisingEventProjection.ReadString(m.MetadataJson, "metaServerStatus")?.StartsWith("blocked_") == true) ? "blocked" : metaRows.Length > 0 ? "projected" : Status("meta"), Status("openai"),
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
            void Composition(string page, IEnumerable<WebsiteCompositionNode> nodes,
                string? instancePrefix = null, HashSet<string>? componentPath = null)
            {
                foreach (var node in nodes ?? [])
                {
                    var key = instancePrefix is null ? node.Id : instancePrefix + "." + node.Id;
                    // The renderer prefixes definition children and strips their
                    // custom bindings; their managed CTA actions still render.
                    Element(page, key, node.Text ?? node.Title, node.ActionKey,
                        instancePrefix is null ? node.Signals : []);
                    var fields = (instancePrefix is null ? node.FieldSignals.Keys.AsEnumerable() : [])
                        .Concat(node.Experience?.Controls.Where(control => !string.IsNullOrWhiteSpace(control.Action?.ActionKey))
                            .Select(control => control.Key) ?? []).Distinct(StringComparer.OrdinalIgnoreCase);
                    foreach (var field in fields)
                    {
                        var action = node.Experience?.Controls.SingleOrDefault(control =>
                            string.Equals(control.Key, field, StringComparison.OrdinalIgnoreCase))?.Action?.ActionKey;
                        var bindings = instancePrefix is null && node.FieldSignals.TryGetValue(field, out var configured) ? configured : [];
                        Element(page, key + ":field:" + field, field, action, bindings);
                    }
                    if (node.Type != "reusable") Composition(page, node.Children, instancePrefix, componentPath);
                    if (node.Type == "reusable" && node.SyncSourceId is { } componentId &&
                        document.ReusableComponents.TryGetValue(componentId, out var component) &&
                        !(componentPath?.Contains(componentId) ?? false))
                    {
                        var path = new HashSet<string>(componentPath ?? [], StringComparer.Ordinal) { componentId };
                        Composition(page, component.Composition, key, path);
                    }
                }
            }

            if (document.LegacyMigration is { } legacy)
            {
                foreach (var page in legacy.Pages)
                {
                    foreach (var element in page.Value.Elements)
                        Element(page.Key, element.Key, element.Value.Text, element.Value.ActionKey, element.Value.Signals);
                    foreach (var extra in page.Value.Extras)
                        Element(page.Key, "extra:" + extra.Id, extra.Text ?? extra.Title, extra.ActionKey, extra.Signals);
                }
                foreach (var element in legacy.Elements)
                    Element("*", element.Key, element.Value.Text, element.Value.ActionKey, element.Value.Signals);
                foreach (var extra in legacy.Extras)
                    Element("*", "extra:" + extra.Id, extra.Text ?? extra.Title, extra.ActionKey, extra.Signals);
            }
            else
            {
                Composition("@shell/header", document.Shell.Header);
                Composition("@shell/footer", document.Shell.Footer);
                foreach (var page in document.Pages)
                    Composition(page.Key, page.Value.Composition);
                // Only components reachable from the rendered page/shell participate.
            }
            foreach (var behavior in AnalyticsEventCatalog.Behaviors.Where(b => !string.IsNullOrWhiteSpace(b.AutomaticTrigger)))
                Add("*", "automatic:" + behavior.Key, behavior.DisplayLabel, behavior.Key, null, behavior, true);
        }
        return result;
    }

}
