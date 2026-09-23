using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Shared.Crm;

namespace Infrastructure.Businesses;

public sealed partial class BusinessWorkspaceService
{
    // Call only after the controller resolves the actor’s business CRM capability.
    internal AgentPortal.Services.ExecutionEngine BusinessActions(Guid businessId) => new(db, businessId);

    // Both values participate: SQL rowversion protects concurrent writes, while
    // UpdatedUtc also makes stale clients detectable on providers without rowversion.
    private static string ContactRevision(WorkstationLeadProfile row) =>
        $"{row.UpdatedUtc.Ticks}:{Convert.ToBase64String(row.RowVersion)}";

    private static void RequireRevision(WorkstationLeadProfile row, string? revision)
    {
        if (string.IsNullOrEmpty(revision) || revision != ContactRevision(row))
            throw new DbUpdateConcurrencyException("This contact changed. Reload before saving.");
    }

    public async Task<object?> QuickViewAsync(Guid businessId, string contactId, string? kind, CancellationToken ct)
    {
        var row = await Contacts(businessId).AsNoTracking().SingleOrDefaultAsync(x => x.LeadId == contactId &&
            (kind == null || x.CrmStatus == kind), ct);
        if (row is null) return null;
        return ContactPayload(row, (await SettingsAsync(businessId, ct)).Preferences);
    }

    public async Task<object?> SaveQuickViewAsync(Guid businessId, string kind, BusinessCrmQuickViewRequest input,
        string actor, CancellationToken ct)
    {
        var row = await Contacts(businessId).SingleOrDefaultAsync(x => x.LeadId == input.ClientUserId && x.CrmStatus == kind, ct);
        if (row is null) return null;
        RequireRevision(row, input.Revision);
        var preferences = (await SettingsAsync(businessId, ct)).Preferences;
        var stage = input.PipelineStage ?? row.CrmStage;
        if (!preferences.Stages.Contains(stage)) throw new ArgumentException("Choose a configured business stage.");
        if (input.CrmStatus is not (null or "Lead" or "Client"))
            throw new ArgumentException("Choose Lead or Client as the relationship.");
        if (input.CrmPriority is not (null or "Low" or "Normal" or "High" or "Urgent"))
            throw new ArgumentException("Choose a supported priority.");
        if (input.CrmNextDate.HasValue && string.IsNullOrWhiteSpace(input.CrmNextText))
            throw new ArgumentException("Describe the next action when setting a follow-up date.");
        ValidateMeetingLink(input.ZoomJoinUrl);
        var meta = ClientCrmMetaSerializer.Deserialize(row.CrmNotes, preferences.Stages);
        var now = DateTime.UtcNow;
        var before = row.CrmStatus + "/" + row.CrmStage;
        row.FirstName = input.FirstName?.Trim() ?? row.FirstName;
        row.LastName = input.LastName?.Trim() ?? row.LastName;
        row.Email = input.Email?.Trim() ?? row.Email;
        row.Phone = input.Phone?.Trim() ?? row.Phone;
        row.Phone2 = input.Phone2?.Trim();
        row.DOB = input.Dob?.Date;
        row.Gender = input.Gender?.Trim();
        row.AddressLine = input.AddressLine?.Trim();
        row.City = input.City?.Trim();
        row.State = input.State?.Trim();
        row.County = input.County?.Trim();
        row.ZipCode = input.ZipCode?.Trim();
        row.Age = input.Age?.Trim();
        row.Btc = input.Btc?.Trim();
        row.CrmStatus = input.CrmStatus ?? row.CrmStatus;
        row.CrmStage = stage;
        if (meta.PipelineStage != stage) meta.StageEnteredUtc = now;
        meta.PipelineStage = stage;
        meta.RecordType = row.CrmStatus;
        meta.CrmPriority = input.CrmPriority ?? meta.CrmPriority;
        meta.ContactStatus = input.ContactStatus ?? meta.ContactStatus;
        meta.CrmNextDate = input.CrmNextDate;
        meta.CrmNextText = input.CrmNextText?.Trim();
        meta.CrmTags = input.CrmTags?.Trim();
        meta.AgentNotes = input.AgentNotes?.Trim();
        meta.WaitingOn = input.WaitingOn ?? meta.WaitingOn;
        meta.PinnedBrief = input.PinnedBrief?.Trim();
        meta.MeetingLocation = input.MeetingLocation?.Trim();
        meta.ZoomJoinUrl = input.ZoomJoinUrl?.Trim();
        meta.UsePersonalZoomLink = input.UsePersonalZoomLink ?? meta.UsePersonalZoomLink;
        meta.MeetingTime = input.MeetingTime?.Trim();
        meta.MeetingDurationMinutes = input.MeetingDurationMinutes ?? meta.MeetingDurationMinutes;
        meta.DocChecklist = new() { IdReceived = input.DocIdReceived, AppSent = input.DocAppSent,
            AppSigned = input.DocAppSigned, PolicyDelivered = input.DocPolicyDelivered, ReviewBooked = input.DocReviewBooked };
        // Watchers describe collaboration; they never confer business membership or access.
        if (input.Watchers is not null) meta.Collaboration.Watchers = input.Watchers.Split(',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToList();
        if (!string.IsNullOrWhiteSpace(input.MentionNote)) meta.Collaboration.MentionNotes.Add(new()
            { Note = input.MentionNote.Trim(), CreatedBy = actor, CreatedUtc = now });
        meta.Activities.Add(new() { Type = "Update", IsSystem = true, CreatedBy = actor,
            Note = $"Contact updated: {before} → {row.CrmStatus}/{stage}", Date = now.ToString("O"), CreatedUtc = now });
        row.CrmNotes = ClientCrmMetaSerializer.Serialize(meta, preferences.Stages);
        row.UpdatedUtc = now;
        await db.SaveChangesAsync(ct);
        return ContactPayload(row, preferences);
    }

    public async Task<object?> AddActivityAsync(Guid businessId, BusinessCrmActivityRequest input, string actor, CancellationToken ct)
    {
        var row = await Contacts(businessId).SingleOrDefaultAsync(x => x.LeadId == input.ClientUserId, ct);
        if (row is null) return null;
        RequireRevision(row, input.Revision);
        ValidateMeetingLink(input.MeetingLink);
        var preferences = (await SettingsAsync(businessId, ct)).Preferences;
        var meta = ClientCrmMetaSerializer.Deserialize(row.CrmNotes, preferences.Stages);
        var now = DateTime.UtcNow;
        meta.Activities.Add(new() { Type = input.Type, Note = input.Note.Trim(), Date = (input.Date ?? now).ToString("O"),
            Location = input.Location?.Trim(), MeetingLink = input.MeetingLink?.Trim(), CreatedBy = actor, CreatedUtc = now });
        meta.LastContactChannel = input.Type;
        row.CrmNotes = ClientCrmMetaSerializer.Serialize(meta, preferences.Stages);
        row.UpdatedUtc = now;
        await db.SaveChangesAsync(ct);
        return ContactPayload(row, preferences);
    }

    public async Task<object?> ReorderAsync(Guid businessId, string kind, BusinessCrmReorderRequest input, string actor, CancellationToken ct)
    {
        if (input.Ids.Count == 0 || input.Ids.Distinct(StringComparer.Ordinal).Count() != input.Ids.Count)
            throw new ArgumentException("Choose distinct contacts to reorder.");
        var preferences = (await SettingsAsync(businessId, ct)).Preferences;
        if (!preferences.Stages.Contains(input.Bucket)) throw new ArgumentException("Choose a configured business stage.");
        var rows = await Contacts(businessId).Where(x => input.Ids.Contains(x.LeadId) && x.CrmStatus == kind).ToListAsync(ct);
        // Validate the entire batch before mutating any tracked entity.
        if (rows.Count != input.Ids.Count) return null;
        foreach (var row in rows) RequireRevision(row, input.Revisions.GetValueOrDefault(row.LeadId));
        var now = DateTime.UtcNow;
        var byId = rows.ToDictionary(x => x.LeadId);
        for (var index = 0; index < input.Ids.Count; index++)
        {
            var row = byId[input.Ids[index]];
            var meta = ClientCrmMetaSerializer.Deserialize(row.CrmNotes, preferences.Stages);
            if (row.CrmStage != input.Bucket)
            {
                meta.StageEnteredUtc = now;
                meta.Activities.Add(new() { Type = "Update", IsSystem = true, CreatedBy = actor,
                    Note = $"Stage changed: {row.CrmStage} → {input.Bucket}", Date = now.ToString("O"), CreatedUtc = now });
            }
            row.CrmStage = meta.PipelineStage = input.Bucket;
            row.CrmOrder = index;
            meta.PipelineOrder = index;
            row.CrmNotes = ClientCrmMetaSerializer.Serialize(meta, preferences.Stages);
            row.UpdatedUtc = now;
        }
        await db.SaveChangesAsync(ct);
        return new { ok = true, updated = rows.Count, revisions = rows.ToDictionary(x => x.LeadId, ContactRevision) };
    }

    private static void ValidateMeetingLink(string? link)
    {
        if (!string.IsNullOrWhiteSpace(link) && (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != "https"))
            throw new ArgumentException("Meeting links must use HTTPS.");
    }

    public async Task<object?> BulkUpdateAsync(Guid businessId, BusinessCrmBulkRequest input, string actor, CancellationToken ct)
    {
        var ids = input.ClientUserIds;
        if (ids.Count == 0 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
            throw new ArgumentException("Choose distinct contacts to update.");
        var preferences = (await SettingsAsync(businessId, ct)).Preferences;
        if (input.PipelineStage is not null && !preferences.Stages.Contains(input.PipelineStage))
            throw new ArgumentException("Choose a configured business stage.");
        if (input.CrmPriority is not (null or "Low" or "Normal" or "High" or "Urgent"))
            throw new ArgumentException("Choose a supported priority.");
        var rows = await Contacts(businessId).Where(x => ids.Contains(x.LeadId)).ToListAsync(ct);
        if (rows.Count != ids.Count) return null;
        var metadata = rows.ToDictionary(x => x.LeadId, x => ClientCrmMetaSerializer.Deserialize(x.CrmNotes, preferences.Stages));
        foreach (var row in rows)
        {
            RequireRevision(row, input.Revisions.GetValueOrDefault(row.LeadId));
            if (input.CrmNextDate.HasValue && string.IsNullOrWhiteSpace(input.CrmNextText ?? metadata[row.LeadId].CrmNextText))
                throw new ArgumentException("Describe the next action when setting a follow-up date.");
        }
        var now = DateTime.UtcNow;
        foreach (var row in rows)
        {
            var meta = metadata[row.LeadId];
            if (input.PipelineStage is not null)
            {
                if (row.CrmStage != input.PipelineStage) meta.StageEnteredUtc = now;
                row.CrmStage = meta.PipelineStage = input.PipelineStage;
            }
            if (input.CrmPriority is not null) meta.CrmPriority = input.CrmPriority;
            if (input.CrmNextDate.HasValue) meta.CrmNextDate = input.CrmNextDate.Value.Date;
            if (input.CrmNextText is not null) meta.CrmNextText = input.CrmNextText.Trim();
            if (input.CrmTags is not null) meta.CrmTags = input.CrmTags.Trim();
            if (input.WaitingOn is not null) meta.WaitingOn = input.WaitingOn;
            meta.Activities.Add(new() { Type = "Update", IsSystem = true, CreatedBy = actor,
                Note = string.IsNullOrWhiteSpace(input.SharedNote) ? "Contact updated through bulk edit." : input.SharedNote.Trim(),
                Date = now.ToString("O"), CreatedUtc = now });
            row.CrmNotes = ClientCrmMetaSerializer.Serialize(meta, preferences.Stages);
            row.UpdatedUtc = now;
        }
        await db.SaveChangesAsync(ct);
        return new { ok = true, updated = rows.Count, revisions = rows.ToDictionary(x => x.LeadId, ContactRevision) };
    }

    private static object ContactPayload(WorkstationLeadProfile row, BusinessWorkspacePreferences preferences)
    {
        var meta = ClientCrmMetaSerializer.Deserialize(row.CrmNotes, preferences.Stages);
        return new
        {
            clientUserId = row.LeadId, leadId = row.LeadId, sourceWorkstationLeadId = row.LeadId,
            revision = ContactRevision(row), row.UpdatedUtc, row.CreatedUtc,
            row.FirstName, row.LastName, row.Email, row.Phone, row.Phone2, row.AddressLine, row.City, row.State,
            row.County, row.ZipCode, row.Gender, row.Age, row.Btc, dob = row.DOB?.ToString("yyyy-MM-dd"),
            recordType = row.CrmStatus, row.CrmStatus, crmPriority = meta.CrmPriority ?? "Normal", meta.ContactStatus,
            portalAccessEnabled = false, accountManagementMode = "BusinessContact", agentWorkspaceAccessEnabled = false,
            pipelineStage = row.CrmStage, pipelineStageLabel = row.CrmStage, bucket = row.CrmStage, pipelineOrder = row.CrmOrder,
            crmLastTouch = meta.Activities.Where(x => !x.IsSystem).OrderByDescending(x => x.CreatedUtc).FirstOrDefault()?.Date,
            crmNextDate = meta.CrmNextDate?.ToString("yyyy-MM-dd"), meta.CrmNextText, meta.CrmTags, meta.AgentNotes,
            crmNotes = meta.AgentNotes, meta.WaitingOn, meta.PinnedBrief, meta.MeetingLocation, meta.ZoomJoinUrl,
            meta.UsePersonalZoomLink, meta.MeetingTime, meta.MeetingDurationMinutes, meta.LastContactChannel,
            docChecklist = meta.DocChecklist, opportunityPlanning = meta.OpportunityPlanning, collaboration = meta.Collaboration,
            activities = meta.Activities.OrderByDescending(x => x.CreatedUtc), meta.StageEnteredUtc,
            stageAgeDays = Math.Max(0, (DateTime.UtcNow - meta.StageEnteredUtc).Days),
            attemptsToday = row.CallsToday, attemptsThisWeek = row.CallsWeek, attemptsThisMonth = row.CallsMonth,
            attemptsThisYear = row.CallsYear, attemptsLifetime = row.CallCount
        };
    }
}
