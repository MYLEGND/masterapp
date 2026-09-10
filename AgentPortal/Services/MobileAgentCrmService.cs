using AgentPortal.Models;
using Domain.Entities;
using Domain.Enums;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Mobile;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Services;

/// Projects the portal CRM and booking records. No mobile calendar or CRM writes.
public sealed class MobileAgentCrmService(MasterAppDbContext db)
{
    public async Task<IReadOnlyList<MobileCrmAppointment>> ScheduleAsync(MobileResolvedActor actor, CancellationToken ct)
    {
        RequireAgent(actor);
        var owner = actor.Actor.UserId.ToLowerInvariant();
        var records = await RecordsAsync(owner, ct);
        var now = DateTime.UtcNow.AddDays(-7);
        var appointments = await db.LeadAppointments.AsNoTracking()
            .Where(a => a.OwnerAgentUserId.ToLower() == owner && a.ScheduledStartUtc != null &&
                (a.ScheduledEndUtc ?? a.ScheduledStartUtc) >= now &&
                (a.Status == LeadAppointmentStatus.Booked || a.Status == LeadAppointmentStatus.Confirmed ||
                 a.Status == LeadAppointmentStatus.Rescheduled))
            .OrderBy(a => a.ScheduledStartUtc).ThenBy(a => a.Id).ToListAsync(ct);
        return appointments.Select(a =>
        {
            // Prefer the current client profile, including converted leads whose
            // canonical appointment still points at SourceWorkstationLeadId.
            var record = records.FirstOrDefault(r => Matches(r.ProfileId, a.ClientProfileId))
                ?? records.FirstOrDefault(r => r.ProfileId != null &&
                    (Matches(r.UserId, a.WorkstationLeadId) || Matches(r.SourceLeadId, a.WorkstationLeadId)))
                ?? records.FirstOrDefault(r => Matches(r.Id, a.WorkstationLeadId));
            return new MobileCrmAppointment(a.Id, DateTime.SpecifyKind(a.ScheduledStartUtc!.Value, DateTimeKind.Utc),
                a.ScheduledEndUtc is { } end ? DateTime.SpecifyKind(end, DateTimeKind.Utc) : null,
                a.Status.ToString(), record?.Kind ?? (a.ClientProfileId != null ? "Client" : "Lead"),
                record?.Id, record?.DisplayName ?? "Meeting", SafeMeetingUrl(a.MeetingUrl) ?? record?.MeetingUrl, DateTime.SpecifyKind(a.UpdatedUtc, DateTimeKind.Utc));
        }).ToArray();
    }

    public async Task<MobileCrmRecord?> RecordAsync(MobileResolvedActor actor, string kind, string id, CancellationToken ct)
    {
        RequireAgent(actor);
        return (await RecordsAsync(actor.Actor.UserId.ToLowerInvariant(), ct))
            .FirstOrDefault(r => (kind == "clients" ? r.ProfileId != null && Matches(r.ProfileId, id) :
                (Matches(r.Id, id) || Matches(r.SourceLeadId, id))));
    }

    private async Task<List<MobileCrmRecord>> RecordsAsync(string owner, CancellationToken ct)
    {
        var closed = await CrmArchiveScope.ClosedClientKeysAsync(db, ct);
        var restorable = await db.AccountLifecycleRecords.AsNoTracking()
            .Where(r => r.ParticipantType == MessagingParticipantTypes.Client && r.State == Domain.Accounts.AccountLifecycleStates.Closed && r.RetainClientContact)
            .Select(r => r.ProfileId).ToListAsync(ct);
        var profiles = await db.ClientProfiles.AsNoTracking()
            .Where(p => db.AgentClients.Any(link => link.AgentUserId.ToLower() == owner &&
                link.ClientUserId.ToLower() == p.ClientUserId.ToLower())).ToListAsync(ct);
        var records = profiles.OrderByDescending(p => p.UpdatedUtc).Select(p =>
        {
            var isLead = ClientRecordClassification.IsLead(p.ClientUserId, p.CrmNotes, p.CrmStatus);
            var meta = ClientCrmMetaSerializer.Deserialize(p.CrmNotes);
            return new MobileCrmRecord(isLead ? p.ClientUserId : p.Id.ToString(), isLead ? "Lead" : "Client",
                p.Id.ToString(), p.ClientUserId, meta?.SourceWorkstationLeadId,
                $"{p.FirstName} {p.LastName}".Trim(), p.Email, p.Phone,
                isLead ? ClientRecordClassification.ResolvePipelineStage(p.ClientUserId, p.CrmNotes) : p.CrmStatus ?? "Active",
                "/Clients?clientUserId=" + Uri.EscapeDataString(p.ClientUserId),
                isLead ? null : "/Clients/Edit?clientUserId=" + Uri.EscapeDataString(p.ClientUserId), SafeMeetingUrl(meta?.ZoomJoinUrl),
                isLead ? AgentPortal.Controllers.LeadsController.CanonicalOutcomeCodes.Where(code => code != "Booked" && AgentPortal.Controllers.ClientsController.SupportsOutcome(code)).ToArray() : []) { FirstName = p.FirstName, LastName = p.LastName, Phone2 = meta?.Phone2, AddressLine = meta?.AddressLine, City = meta?.City, State = meta?.State, ZipCode = meta?.ZipCode, UpdatedUtc = DateTime.SpecifyKind(p.UpdatedUtc, DateTimeKind.Utc), CanRestore = restorable.Contains(p.Id), Archived = closed.Contains(p.Id.ToString()) || closed.Contains(p.ClientUserId) || CrmArchiveScope.ArchivedStatus(p.CrmStatus) };
        }).ToList();
        var leads = await db.WorkstationLeadProfiles.AsNoTracking()
            .Where(l => l.AgentUserId.ToLower() == owner).ToListAsync(ct);
        records.AddRange(leads.Where(l => !records.Any(r => Matches(r.SourceLeadId, l.LeadId) || Matches(r.UserId, l.LeadId)))
            .Select(l => new MobileCrmRecord(l.LeadId, "Lead", null, l.LeadId, null,
                $"{l.FirstName} {l.LastName}".Trim(), l.Email, l.Phone, l.CrmStage,
                "/Leads?leadId=" + Uri.EscapeDataString(l.LeadId), null,
                SafeMeetingUrl(ClientCrmMetaSerializer.Deserialize(l.CrmNotes)?.ZoomJoinUrl),
                AgentPortal.Controllers.LeadsController.CanonicalOutcomeCodes.Where(code => code is not ("Booked" or "PolicyPlaced")).ToArray()) { FirstName = l.FirstName, LastName = l.LastName, Phone2 = l.Phone2, AddressLine = l.AddressLine, City = l.City, State = l.State, ZipCode = l.ZipCode, UpdatedUtc = DateTime.SpecifyKind(l.UpdatedUtc, DateTimeKind.Utc), Archived = closed.Contains(l.LeadId) || CrmArchiveScope.ArchivedStatus(l.CrmStatus) || CrmArchiveScope.ArchivedStatus(l.CrmStage) }));
        return records;
    }

    private static bool Matches(string? a, string? b) => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
        (string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase) ||
         (Guid.TryParse(a, out var left) && Guid.TryParse(b, out var right) && left == right));
    private static string? SafeMeetingUrl(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps ? uri.AbsoluteUri : null;
    private static void RequireAgent(MobileResolvedActor actor)
    {
        if (actor.Actor.ParticipantType != MessagingParticipantTypes.Agent) throw new UnauthorizedAccessException();
    }
}

public sealed record MobileCrmRecord(string Id, string Kind, string? ProfileId, string UserId, string? SourceLeadId,
    string DisplayName, string? Email, string? Phone, string Stage, string ManagementPath, string? AccountPath, string? MeetingUrl = null, IReadOnlyList<string>? AvailableOutcomes = null)
{
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Phone2 { get; init; }
    public string? AddressLine { get; init; }
    public string? City { get; init; }
    public string? State { get; init; }
    public string? ZipCode { get; init; }
    public DateTime UpdatedUtc { get; init; }
    public bool Archived { get; init; }
    public bool CanRestore { get; init; }
}
public sealed record MobileCrmAppointment(Guid Id, DateTime StartUtc, DateTime? EndUtc, string Status,
    string Kind, string? RecordId, string DisplayName, string? MeetingUrl, DateTime UpdatedUtc);
