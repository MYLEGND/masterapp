using System.Text.Json;
using System.ComponentModel.DataAnnotations;
using Infrastructure.Leads;
using Domain.Entities;
using Infrastructure.Analytics;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;
using Shared.Crm;

namespace Infrastructure.Businesses;

public sealed partial class BusinessWorkspaceService(MasterAppDbContext db, IAnalyticsQueryService analytics, WebsiteIntakeRecipientResolver recipients)
{
    public async Task<List<BusinessWorkspaceNavigationItem>> NavigationForBusinessAsync(Guid? businessId, string actor, string? email, CancellationToken ct)
    {
        // Management permission is not navigation context. Never enumerate the
        // agent's client book to populate their personal application navigation.
        if (!businessId.HasValue || businessId == Guid.Empty || string.IsNullOrWhiteSpace(actor)) return new();
        var profileIds = await db.CommerceBusinessMembers.AsNoTracking()
            .Where(m => m.CommerceBusinessId == businessId && m.Status == "Active" && m.ClientProfileId.HasValue)
            .Select(m => m.ClientProfileId!.Value).Distinct().ToListAsync(ct);
        var items = new List<BusinessWorkspaceNavigationItem>();
        foreach (var id in profileIds) items.AddRange(await NavigationAsync(id, actor, email, ct, businessId));
        return items.GroupBy(x => x.BusinessId).Select(group =>
        {
            var first = group.First();
            return first with { CanCrm = group.Any(x => x.CanCrm), CanAnalytics = group.Any(x => x.CanAnalytics), CanCustomize = group.Any(x => x.CanCustomize), CanWebsite = group.Any(x => x.CanWebsite) };
        }).OrderBy(x => x.BusinessName).ToList();
    }

    public async Task<List<BusinessWorkspaceNavigationItem>> NavigationAsync(Guid profileId, string actor, string? email, CancellationToken ct, Guid? selectedBusinessId = null)
    {
        var ids = await db.CommerceBusinessMembers.AsNoTracking().Where(x => x.ClientProfileId == profileId && x.Status == "Active" &&
                (!selectedBusinessId.HasValue || x.CommerceBusinessId == selectedBusinessId))
            .Select(x => x.CommerceBusinessId).Distinct().ToListAsync(ct);
        var result = new List<BusinessWorkspaceNavigationItem>();
        foreach (var id in ids)
        {
            var crm = await BusinessWorkspaceAccess.ResolveAsync(db, id, profileId, actor, email, "crm", ct);
            var analyticsBusiness = await BusinessWorkspaceAccess.ResolveAsync(db, id, profileId, actor, email, "analytics", ct);
            var settings = await BusinessWorkspaceAccess.ResolveAsync(db, id, profileId, actor, email, "settings", ct);
            var website = await BusinessWorkspaceAccess.ResolveAsync(db, id, profileId, actor, email, "website", ct);
            var business = crm ?? analyticsBusiness ?? settings ?? website;
            if (business is null) continue;
            var preferences = (await SettingsAsync(id, ct)).Preferences;
            var cutoff = DateTime.UtcNow.AddHours(-24);
            var liveDomain = await db.Set<WebsiteDomainBinding>().AsNoTracking().Where(x => x.CommerceBusinessId == id &&
                x.Status == "active" && x.CertificateStatus == "active" && x.LastCheckedUtc >= cutoff)
                .OrderBy(x => x.CreatedUtc).Select(x => x.Hostname).FirstOrDefaultAsync(ct);
            result.Add(new(id, business.DisplayName, preferences.LeadLabel, preferences.ClientLabel,
                crm is not null, analyticsBusiness is not null, settings is not null, website is not null, liveDomain is null ? null : "https://" + liveDomain));
        }
        return result;
    }

    public async Task<object?> AnalyticsDataAsync(Guid businessId, string section, TimeRangeRequest range, TrafficType trafficType,
        string? metric = null, string? visitorId = null, string? sessionId = null, CancellationToken ct = default)
    {
        var scope = ScopeContext.ForBusiness(businessId);
        if (section is "kpi-detail" or "visitor-timeline")
        {
            var trust = new AgentPortal.Services.Analytics.VisitorTrustScoringService();
            var projection = new AnalyticsDetailProjection(analytics, new AgentPortal.Services.Analytics.KpiDetailBreakdownService());
            if (section == "kpi-detail")
            {
                if (string.IsNullOrWhiteSpace(metric)) throw new ArgumentException("Choose a metric.");
                var concentration = new AgentPortal.Services.Analytics.VisitorConcentrationService(db, trust, analytics);
                return await projection.KpiAsync(metric, range, scope, trafficType, concentration.GetVisitorConcentrationAsync, ct);
            }
            if (string.IsNullOrWhiteSpace(visitorId) && string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Choose a visitor or session.");
            return await projection.VisitorTimelineAsync(visitorId?.Trim(), sessionId?.Trim(), range, scope, trafficType, trust, ct);
        }
        return section switch
        {
            "meta-signal" => await new MetaSignalAnalyticsService(db, analytics).GetDashboardAsync(range, scope, trafficType),
            "meta-signal-health" => await new MetaSignalAnalyticsService(db, analytics).GetHealthDashboardAsync(range, scope),
            "summary" => await analytics.GetSummaryAsync(range, scope, trafficType),
            "traffic" => await analytics.GetTrafficAsync(range, scope, trafficType),
            "page-performance" => await analytics.GetPagePerformanceAsync(range, scope, trafficType),
            "cta-performance" => await analytics.GetCtaPerformanceAsync(range, scope, trafficType),
            "quote-funnel" => await analytics.GetQuoteFunnelAsync(range, scope, trafficType),
            "marketing-health" => await MarketingHealthProjection.LoadAsync(analytics, new MetaSignalAnalyticsService(db, analytics), range, scope, trafficType, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance),
            "conversions" => await analytics.GetConversionsAsync(range, scope, trafficType),
            "leads" => await analytics.GetLeadsAsync(range, scope, trafficType),
            "behavior/summary" => await analytics.GetEngagementSummaryAsync(range, scope, trafficType),
            "behavior/time-on-page" => await analytics.GetTimeOnPageAsync(range, scope, trafficType),
            "behavior/exit-analysis" => await analytics.GetExitAnalysisAsync(range, scope, trafficType),
            "behavior/journey" => await analytics.GetJourneyAnalysisAsync(range, scope, trafficType),
            "behavior/source-performance" => await analytics.GetSourcePerformanceAsync(range, scope, trafficType),
            "quote-funnel/abandonment" => await analytics.GetFormAbandonmentAsync(range, scope, trafficType),
            "DeviceIntelligence" => await analytics.GetDeviceIntelligenceAsync(range, scope, trafficType),
            _ => null
        };
    }

    private IQueryable<WorkstationLeadProfile> Contacts(Guid id) => id != Guid.Empty
        ? db.WorkstationLeadProfiles.Where(x => x.CommerceBusinessId == id && x.AgentUserId == "")
        : db.WorkstationLeadProfiles.Where(x => false);

    public async Task<BusinessWorkspaceModel> CrmAsync(CommerceBusiness business, string kind, string? search, int page, string? contactId, CancellationToken ct)
    {
        kind = kind == "Client" ? "Client" : "Lead";
        search = (search ?? "").Trim();
        if (search.Length > 120) search = search[..120];
        page = Math.Clamp(page, 1, 10000);
        var query = Contacts(business.Id).AsNoTracking().Where(x => x.CrmStatus == kind);
        if (search.Length > 0) query = query.Where(x => x.FirstName.Contains(search) || x.LastName.Contains(search) || x.Email.Contains(search) || x.Phone.Contains(search));
        var model = new BusinessWorkspaceModel { BusinessId = business.Id, BusinessName = business.DisplayName, Kind = kind, Search = search, Page = page, Total = await query.CountAsync(ct) };
        model.Preferences = (await SettingsAsync(business.Id, ct)).Preferences;
        var rows = await query.OrderBy(x => x.CrmOrder).ThenBy(x => x.LeadId).ToListAsync(ct);
        model.Contacts = rows.Select(x => Project(x, model.Preferences)).ToList();
        model.CanonicalContacts = rows.Select(x => ProjectCanonical(x, model.Preferences)).ToList();
        if (!string.IsNullOrEmpty(contactId))
        {
            var contact = await Contacts(business.Id).AsNoTracking().SingleOrDefaultAsync(x => x.LeadId == contactId && x.CrmStatus == kind, ct);
            if (contact is not null)
            {
                model.Selected = Project(contact, model.Preferences);
                var leadIds = db.WebsiteLeadIntakeLinks.Where(x => x.CommerceBusinessId == business.Id && x.WorkstationLeadId == contactId).Select(x => x.WebsiteLeadPublicId);
                model.Selected.Inquiries = await db.Set<CommerceWebsiteInquiry>().AsNoTracking()
                    .Where(x => x.CommerceBusinessId == business.Id && x.WebsiteLeadId.HasValue && leadIds.Contains(x.WebsiteLeadId.Value))
                    .OrderByDescending(x => x.CreatedUtc).Take(100)
                    .Select(x => new BusinessCrmInquiry(x.CreatedUtc, x.Message, x.SourcePath, x.NotificationStatus)).ToListAsync(ct);
                model.Selected.Sources = await db.WebsiteLeadIntakeLinks.AsNoTracking()
                    .Where(x => x.CommerceBusinessId == business.Id && x.WorkstationLeadId == contactId)
                    .OrderByDescending(x => x.SubmittedUtc).Take(100)
                    .Select(x => new BusinessCrmSource(x.SubmittedUtc, x.SourcePageKey, x.UtmCampaign)).ToListAsync(ct);
            }
        }
        return model;
    }

    public async Task<bool> UpdateAsync(Guid businessId, string contactId, BusinessCrmEdit input, string actor, CancellationToken ct)
    {
        var preferences = (await SettingsAsync(businessId, ct)).Preferences;
        if (input.Kind is not ("Lead" or "Client") || !preferences.Stages.Contains(input.Stage))
            throw new ArgumentException("Choose a supported relationship and stage.");
        var row = await Contacts(businessId).SingleOrDefaultAsync(x => x.LeadId == contactId, ct);
        if (row is null) return false;
        if (row.UpdatedUtc != input.ExpectedUpdatedUtc || Convert.ToBase64String(row.RowVersion) != input.Revision)
            throw new DbUpdateConcurrencyException();
        var meta = ClientCrmMetaSerializer.Deserialize(row.CrmNotes, preferences.Stages);
        var before = row.CrmStatus + "/" + meta.PipelineStage;
        row.CrmStatus = input.Kind;
        row.CrmStage = input.Stage;
        meta.RecordType = input.Kind;
        if (meta.PipelineStage != input.Stage) meta.StageEnteredUtc = DateTime.UtcNow;
        meta.PipelineStage = input.Stage;
        meta.AgentNotes = input.Notes?.Trim();
        meta.Activities.Add(new() { Type = "Update", IsSystem = true, CreatedBy = actor,
            Note = $"CRM updated: {before} → {input.Kind}/{input.Stage}", Date = DateTime.UtcNow.ToString("O") });
        row.CrmNotes = ClientCrmMetaSerializer.Serialize(meta, preferences.Stages);
        row.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<BusinessWorkspaceModel> AnalyticsAsync(CommerceBusiness business, int days, CancellationToken ct)
    {
        days = days is 7 or 30 or 90 ? days : 30;
        var range = new TimeRangeRequest { FromUtc = DateTime.UtcNow.AddDays(-days), ToUtc = DateTime.UtcNow,
            QualityMode = TrafficQualityMode.RealHumanTraffic, Label = $"Last {days} days", Preset = "custom" };
        var scope = ScopeContext.ForBusiness(business.Id);
        var settings = await SettingsAsync(business.Id, ct);
        var facts = await WebsiteBusinessFacts.LoadAsync(db, business.Id, ct);
        var bookingUrl = settings.Row.BookingEnabled
            ? settings.Row.BookingFallbackUrl ?? settings.Row.BookingEmbedUrl
            : null;
        var actionCatalog = WebsiteCallToActionCatalog.Build(
            WebsiteEditorSiteKeys.Business,
            facts.Phone,
            facts.ContactEmail,
            bookingUrl);
        var model = new BusinessWorkspaceModel { BusinessId = business.Id, BusinessName = business.DisplayName, Tab = "analytics", Days = days,
            Summary = await analytics.GetSummaryAsync(range, scope), Health = await analytics.GetMarketingHealthAsync(range, scope) };
        model.Preferences = settings.Preferences;
        var owner = WebsiteEditorSiteKeys.BusinessOwnerKey(business.Id);
        var state = await db.Set<WebsiteContentState>().AsNoTracking().SingleOrDefaultAsync(x => x.OwnerKey == owner && x.SiteKey == WebsiteEditorSiteKeys.Business, ct);
        if (state is not null)
        {
            AddMap(model, state.DraftJson, false, state.Revision, actionCatalog);
            var version = await db.Set<WebsiteContentVersion>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == state.PublishedVersionId && x.StateId == state.Id, ct);
            if (version is not null) AddMap(model, version.DocumentJson, true, version.Revision, actionCatalog);
        }
        model.RecentEvents = await db.MetaSignalEvents.AsNoTracking().Where(x => x.CommerceBusinessId == business.Id && x.AgentTrackingProfileId == null && x.CreatedUtc >= range.FromUtc)
            .OrderByDescending(x => x.CreatedUtc).Take(50).Select(x => new BusinessWebsiteEventRow(x.CreatedUtc, x.EventName, x.PageKey, x.MetaServerSent)).ToListAsync(ct);
        return model;
    }

    private async Task<(CommerceBusinessStorefrontSettings Row, BusinessWorkspacePreferences Preferences)> SettingsAsync(Guid id, CancellationToken ct)
    {
        var row = await db.CommerceBusinessStorefrontSettings.SingleAsync(x => x.CommerceBusinessId == id, ct);
        return (row, BusinessWorkspacePreferences.Read(row.WorkspacePreferencesJson));
    }

    public async Task<BusinessWorkspaceModel> CustomizeAsync(CommerceBusiness business, CancellationToken ct)
    {
        var settings = await SettingsAsync(business.Id, ct);
        return new() { BusinessId = business.Id, BusinessName = business.DisplayName, Tab = "settings", CanCustomize = true,
            Preferences = settings.Preferences, SettingsRevision = settings.Row.Revision,
            Recipients = await recipients.BusinessOptionsAsync(business.Id, ct) };
    }

    public async Task CustomizeAsync(Guid businessId, BusinessWorkspaceSettingsInput input, CancellationToken ct)
    {
        var settings = await SettingsAsync(businessId, ct);
        if (settings.Row.Revision != input.Revision) throw new DbUpdateConcurrencyException();
        var preferences = new BusinessWorkspacePreferences { LeadLabel = input.LeadLabel, ClientLabel = input.ClientLabel,
            Stages = input.Stages.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(), Metrics = input.Metrics };
        if (!string.IsNullOrEmpty(input.Recipient))
        {
            var options = await recipients.BusinessOptionsAsync(businessId, ct);
            if (!options.Any(x => x.Key == input.Recipient)) throw new ValidationException("Choose an active team member or assigned agent.");
            var id = Guid.Parse(input.Recipient[(input.Recipient.IndexOf(':') + 1)..]);
            if (input.Recipient.StartsWith("member:")) preferences.NotificationMemberId = id;
            else preferences.NotificationAgentTrackingProfileId = id;
        }
        var json = preferences.Write();
        var removed = settings.Preferences.Stages.Except(preferences.Stages).ToArray();
        if (removed.Length > 0 && await Contacts(businessId).AnyAsync(x => removed.Contains(x.CrmStage), ct))
            throw new ValidationException("Move contacts out of a stage before removing or renaming it.");
        settings.Row.WorkspacePreferencesJson = json;
        settings.Row.Revision = Guid.NewGuid();
        settings.Row.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private static AgentPortal.Models.ClientListItemViewModel ProjectCanonical(WorkstationLeadProfile row, BusinessWorkspacePreferences preferences)
    {
        var meta = ClientCrmMetaSerializer.Deserialize(row.CrmNotes, preferences.Stages);
        return new()
        {
            ClientUserId = row.LeadId, SourceWorkstationLeadId = row.LeadId, BusinessRevision = ContactRevision(row),
            FirstName = row.FirstName, LastName = row.LastName, Email = row.Email, Phone = row.Phone,
            RecordType = row.CrmStatus, AccountManagementMode = "BusinessContact", CrmStatus = row.CrmStatus,
            PipelineStage = row.CrmStage, PipelineOrder = row.CrmOrder, CrmPriority = meta.CrmPriority ?? "Normal",
            CrmNextDate = meta.CrmNextDate, CrmNextText = meta.CrmNextText, CrmTags = meta.CrmTags,
            AgentNotes = meta.AgentNotes, StageEnteredUtc = meta.StageEnteredUtc,
            StageAgeDays = Math.Max(0, (DateTime.UtcNow - meta.StageEnteredUtc).Days),
            AddressLine = row.AddressLine, City = row.City, State = row.State, County = row.County, ZipCode = row.ZipCode,
            Phone2 = row.Phone2, WaitingOn = meta.WaitingOn, PinnedBrief = meta.PinnedBrief,
            AttemptsToday = row.CallsToday, AttemptsThisWeek = row.CallsWeek, AttemptsThisMonth = row.CallsMonth,
            AttemptsYear = row.CallsYear, AttemptsLifetime = row.CallCount, LastContactChannel = meta.LastContactChannel,
            MeetingLocation = meta.MeetingLocation, MeetingTime = meta.MeetingTime, MeetingDurationMinutes = meta.MeetingDurationMinutes,
            LeadOriginLabel = "Website inquiry", LeadOriginTone = "info"
        };
    }

    private static BusinessCrmContact Project(WorkstationLeadProfile row, BusinessWorkspacePreferences preferences)
    {
        var meta = ClientCrmMetaSerializer.Deserialize(row.CrmNotes, preferences.Stages);
        return new() { Id = row.LeadId, Name = (row.FirstName + " " + row.LastName).Trim(), Email = row.Email, Phone = row.Phone,
            Kind = row.CrmStatus, Stage = preferences.Stages.Contains(row.CrmStage) ? row.CrmStage : meta.PipelineStage, Notes = meta.AgentNotes ?? "", Revision = Convert.ToBase64String(row.RowVersion),
            UpdatedUtc = row.UpdatedUtc, History = meta.Activities.OrderByDescending(x => x.CreatedUtc).Take(100).ToList() };
    }

    private static void AddMap(
        BusinessWorkspaceModel model,
        string json,
        bool published,
        long revision,
        IReadOnlyList<WebsiteCallToActionOption> actionCatalog)
    {
        var document = JsonSerializer.Deserialize<WebsiteContentDocument>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (document is null) return;
        var actions = actionCatalog.ToDictionary(action => action.Key, StringComparer.Ordinal);

        void AddAutomaticAction(string path, string element, string? actionKey)
        {
            if (string.IsNullOrWhiteSpace(actionKey) || !actions.TryGetValue(actionKey, out var action)) return;
            model.EventMap.Add(new(path, element, "click", action.AnalyticsEventName, "automatic", published, revision));
            if (!string.IsNullOrWhiteSpace(action.MetaIntentEventName))
                model.EventMap.Add(new(path, element, "click", action.MetaIntentEventName!, "automatic_meta", published, revision));
        }

        void Add(string path, Dictionary<string, WebsiteElementOverride> elements, List<WebsiteExtraComponent> extras)
        {
            foreach (var pair in elements)
            {
                AddAutomaticAction(path, pair.Key, pair.Value.ActionKey);
                foreach (var binding in pair.Value.Signals)
                    model.EventMap.Add(new(path, pair.Key, binding.Trigger, binding.EventName, binding.DeliveryMode, published, revision));
            }
            foreach (var extra in extras)
            {
                AddAutomaticAction(path, "extra:" + extra.Id, extra.ActionKey);
                foreach (var binding in extra.Signals)
                    model.EventMap.Add(new(path, "extra:" + extra.Id, binding.Trigger, binding.EventName, binding.DeliveryMode, published, revision));
            }
        }

        // These are intrinsic to the Protect runtime used by every published business website.
        model.EventMap.Add(new("*", "page", "viewed", "ViewContent", "automatic", published, revision));
        model.EventMap.Add(new("*", "page", "scroll_threshold", "MeaningfulScroll", "automatic", published, revision));
        Add("*", document.Elements, document.Extras);
        foreach (var page in document.Pages) Add(page.Key, page.Value.Elements, page.Value.Extras);
    }
}
