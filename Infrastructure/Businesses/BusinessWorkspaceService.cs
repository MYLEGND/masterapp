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

public sealed class BusinessWorkspaceService(MasterAppDbContext db, IAnalyticsQueryService analytics, WebsiteIntakeRecipientResolver recipients)
{
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
        model.Contacts = (await query.OrderByDescending(x => x.UpdatedUtc).ThenBy(x => x.LeadId).Skip((page - 1) * 30).Take(30).ToListAsync(ct)).Select(x => Project(x, model.Preferences)).ToList();
        if (!string.IsNullOrEmpty(contactId))
        {
            var contact = await Contacts(business.Id).AsNoTracking().SingleOrDefaultAsync(x => x.LeadId == contactId, ct);
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
        var model = new BusinessWorkspaceModel { BusinessId = business.Id, BusinessName = business.DisplayName, Tab = "analytics", Days = days,
            Summary = await analytics.GetSummaryAsync(range, scope), Health = await analytics.GetMarketingHealthAsync(range, scope) };
        model.Preferences = (await SettingsAsync(business.Id, ct)).Preferences;
        var owner = WebsiteEditorSiteKeys.BusinessOwnerKey(business.Id);
        var state = await db.Set<WebsiteContentState>().AsNoTracking().SingleOrDefaultAsync(x => x.OwnerKey == owner && x.SiteKey == WebsiteEditorSiteKeys.Business, ct);
        if (state is not null)
        {
            AddMap(model, state.DraftJson, false, state.Revision);
            var version = await db.Set<WebsiteContentVersion>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == state.PublishedVersionId && x.StateId == state.Id, ct);
            if (version is not null) AddMap(model, version.DocumentJson, true, version.Revision);
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

    private static BusinessCrmContact Project(WorkstationLeadProfile row, BusinessWorkspacePreferences preferences)
    {
        var meta = ClientCrmMetaSerializer.Deserialize(row.CrmNotes, preferences.Stages);
        return new() { Id = row.LeadId, Name = (row.FirstName + " " + row.LastName).Trim(), Email = row.Email, Phone = row.Phone,
            Kind = row.CrmStatus, Stage = preferences.Stages.Contains(row.CrmStage) ? row.CrmStage : meta.PipelineStage, Notes = meta.AgentNotes ?? "", Revision = Convert.ToBase64String(row.RowVersion),
            UpdatedUtc = row.UpdatedUtc, History = meta.Activities.OrderByDescending(x => x.CreatedUtc).Take(100).ToList() };
    }

    private static void AddMap(BusinessWorkspaceModel model, string json, bool published, long revision)
    {
        var document = JsonSerializer.Deserialize<WebsiteContentDocument>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (document is null) return;
        void Add(string path, Dictionary<string, WebsiteElementOverride> elements, List<WebsiteExtraComponent> extras)
        {
            foreach (var pair in elements)
                foreach (var binding in pair.Value.Signals)
                    model.EventMap.Add(new(path, pair.Key, binding.Trigger, binding.EventName, binding.DeliveryMode, published, revision));
            foreach (var extra in extras)
                foreach (var binding in extra.Signals)
                    model.EventMap.Add(new(path, "extra:" + extra.Id, binding.Trigger, binding.EventName, binding.DeliveryMode, published, revision));
        }
        Add("*", document.Elements, document.Extras);
        foreach (var page in document.Pages) Add(page.Key, page.Value.Elements, page.Value.Extras);
    }
}
