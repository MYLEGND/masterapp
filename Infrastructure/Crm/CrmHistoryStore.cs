using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Shared.Crm;

namespace Infrastructure.Crm;

/// <summary>The single filed event authority; submission answers remain in their canonical intake snapshot.</summary>
public sealed class CrmHistoryStore(MasterAppDbContext db)
{
    public static string LeadKey(string id) => "lead:" + id;
    public static string ClientKey(Guid id) => "client:" + id.ToString("N");
    public static Guid EntryId(string relationship, string source, string sourceId) => new(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { relationship, source, sourceId }))).AsSpan(0, 16));

    public async Task<CrmHistoryEntry> StageAsync(CrmHistoryEntry entry, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(entry.RelationshipKey) || entry.RelationshipKey.Length > 100 ||
            string.IsNullOrWhiteSpace(entry.SourceId) || entry.SourceId.Length > 200 || entry.ContentJson.Length > 100_000)
            throw new ArgumentException("History identity and content must be bounded.");
        entry.Id = EntryId(entry.RelationshipKey, entry.Source, entry.SourceId);
        var existing = db.Set<CrmHistoryEntry>().Local.FirstOrDefault(x => x.Id == entry.Id)
            ?? await db.Set<CrmHistoryEntry>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == entry.Id, ct);
        if (existing is not null) return existing;
        entry.FiledUtc = DateTime.UtcNow;
        if (entry.RecordedUtc == default) entry.RecordedUtc = DateTime.UtcNow;
        db.Add(entry);
        return entry;
    }

    public Task<CrmHistoryEntry> StageSubmissionAsync(WebsiteLeadIntakeLink link, CancellationToken ct = default) => StageAsync(new()
    {
        RelationshipKey = link.HistoryClientProfileId.HasValue ? ClientKey(link.HistoryClientProfileId.Value)
            : LeadKey(link.WorkstationLeadId ?? throw new InvalidOperationException("Submission association is unresolved.")), Source = "WebsiteSubmission",
        SourceId = link.Id.ToString("N"), SubmissionId = link.Id, Type = "FormSubmission",
        RecordedBy = "Website submission", RecordedUtc = link.CapturedUtc, OccurredUtc = link.SubmittedUtc,
        TimePrecision = "instant", Product = link.ProductType ?? link.InterestType,
        FormName = link.SourcePageKey ?? "Submitted form — historical name unavailable",
        FormVersion = SubmissionVersion(link.SnapshotJson),
        // References only: no second independently editable answer store.
        ContentJson = JsonSerializer.Serialize(new { form = link.SourcePageKey, submission = link.WebsiteLeadPublicId,
            originalOwner = link.AgentUserId, originalBusinessId = link.CommerceBusinessId,
            completeness = "Original captured fields only; missing question or authorization wording is unavailable" }),
        EvidenceStatus = SubmissionAuthorization(link.SnapshotJson)
    }, ct);

    private static string? SubmissionVersion(string? snapshot)
    {
        try { using var json = JsonDocument.Parse(snapshot ?? "{}"); return json.RootElement.TryGetProperty("SubmittedFormEvidence", out var evidence) &&
            evidence.TryGetProperty("formDefinitionVersion", out var version) && version.ValueKind == JsonValueKind.String ? version.GetString() : null; }
        catch (JsonException) { return null; }
    }
    private static string SubmissionAuthorization(string? snapshot)
    {
        try
        {
            using var json = JsonDocument.Parse(snapshot ?? "{}");
            if (!json.RootElement.TryGetProperty("SubmittedFormEvidence", out var evidence) || !evidence.TryGetProperty("authorizations", out var authorizations) || authorizations.ValueKind != JsonValueKind.Array)
                return "Authorization evidence unavailable";
            var states = authorizations.EnumerateArray().Select(x => x.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String ? status.GetString() : "Evidence unavailable").Distinct();
            var summary = string.Join("; ", states); return summary.Length > 80 ? "Multiple authorization states — review each disclosure" : summary;
        }
        catch (JsonException) { return "Authorization evidence unavailable"; }
    }

    public async Task CaptureTrackedAsync(CancellationToken ct = default)
    {
        // Keep the relationship anchor while filed evidence exists. Application
        // rollback, archive or a released hold does not authorize evidence disposal.
        var deletedKeys = db.ChangeTracker.Entries<WorkstationLeadProfile>().Where(x => x.State == EntityState.Deleted)
            .Select(x => LeadKey(x.Entity.LeadId)).Concat(db.ChangeTracker.Entries<ClientProfile>().Where(x => x.State == EntityState.Deleted)
                .Select(x => ClientCrmMetaSerializer.Deserialize(x.Entity.CrmNotes).SourceWorkstationLeadId is { Length: > 0 } source
                    ? LeadKey(source) : ClientKey(x.Entity.Id))).Distinct().ToArray();
        foreach (var key in deletedKeys)
            if (db.Set<CrmHistoryEntry>().Local.Any(x => x.RelationshipKey == key) ||
                await db.Set<CrmHistoryEntry>().AnyAsync(x => x.RelationshipKey == key, ct))
                throw new InvalidOperationException("A relationship with filed history must be archived, not deleted. Preservation and authorized disposition require their existing controlled process.");
        foreach (var changedOwner in db.ChangeTracker.Entries<WorkstationLeadProfile>()
            .Where(x => x.State == EntityState.Modified && x.Property(p => p.AgentUserId).IsModified).ToArray())
        {
            var key = LeadKey(changedOwner.Entity.LeadId);
            var lastChange = await db.Set<CrmHistoryEntry>().AsNoTracking().Where(x => x.RelationshipKey == key && x.Source == "RelationshipAssignment")
                .OrderByDescending(x => x.FiledUtc).ThenBy(x => x.Id).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
            var from = changedOwner.Property(x => x.AgentUserId).OriginalValue;
            var to = changedOwner.Entity.AgentUserId;
            await BackfillLegacySelfNotesAsync(key, from, new[] { changedOwner.Entity.LeadId }, ct);
            await StageAsync(new() { RelationshipKey = key, Source = "RelationshipAssignment",
                SourceId = EntryId(key, "Assignment", JsonSerializer.Serialize(new { from, to, prior = lastChange })).ToString("N"),
                Type = "Assignment", RecordedBy = "Existing relationship assignment writer", OccurredUtc = DateTime.UtcNow,
                TimePrecision = "instant", ContentJson = JsonSerializer.Serialize(new { previousOwner = from, currentOwner = to,
                    recorderEvidence = "Source writer changed the persisted assignment; independent human actor evidence is not supplied by this legacy boundary." }),
                EvidenceStatus = "System-observed assignment" }, ct);
        }
        // Preserve owner-scoped legacy notes before existing client assignment writers
        // remove the old grant. The original grant is evidence, not a new read grant.
        foreach (var assignment in db.ChangeTracker.Entries<AgentClient>().Where(x => x.State == EntityState.Deleted).ToArray())
        {
            var client = await db.ClientProfiles.SingleOrDefaultAsync(x => x.ClientUserId == assignment.Entity.ClientUserId, ct);
            if (client is null) continue;
            var key = await ResolveClientKeyAsync(client, ct);
            var meta = ClientCrmMetaSerializer.Deserialize(client.CrmNotes);
            await BackfillLegacySelfNotesAsync(key, assignment.Entity.AgentUserId,
                new[] { client.ClientUserId, meta.SourceLeadClientUserId, meta.SourceWorkstationLeadId }, ct);
        }
        foreach (var assignment in db.ChangeTracker.Entries<AgentClient>().Where(x => x.State is EntityState.Added or EntityState.Deleted).ToArray())
        {
            var client = db.ClientProfiles.Local.FirstOrDefault(x => x.ClientUserId == assignment.Entity.ClientUserId)
                ?? await db.ClientProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.ClientUserId == assignment.Entity.ClientUserId, ct);
            if (client is null) continue;
            var key = await ResolveClientKeyAsync(client, ct);
            var action = assignment.State == EntityState.Added ? "Access granted" : "Access revoked";
            var prior = await db.Set<CrmHistoryEntry>().AsNoTracking().Where(x => x.RelationshipKey == key && x.Source == "ClientAssignment")
                .OrderByDescending(x => x.FiledUtc).ThenBy(x => x.Id).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
            await StageAsync(new() { RelationshipKey = key, Source = "ClientAssignment",
                SourceId = EntryId(key, "ClientAssignment", JsonSerializer.Serialize(new { assignment.Entity.AgentUserId, action, prior })).ToString("N"),
                Type = "Assignment", RecordedBy = "Existing client access writer", OccurredUtc = DateTime.UtcNow, TimePrecision = "instant",
                ContentJson = JsonSerializer.Serialize(new { agent = assignment.Entity.AgentUserId, action,
                    recorderEvidence = "System-observed persisted access change; no independent human recorder supplied by legacy writer." }) }, ct);
        }
        // Existing calendar, conversion and CRM writers all commit through this
        // boundary. Their new events are filed in the same database transaction.
        var leads = db.ChangeTracker.Entries<WorkstationLeadProfile>()
            .Where(x => x.State == EntityState.Added || x.State == EntityState.Modified && x.Property(p => p.CrmNotes).IsModified).ToArray();
        foreach (var tracked in leads)
        {
            var key = LeadKey(tracked.Entity.LeadId);
            if (tracked.State == EntityState.Modified)
                await BackfillMetadataAsync(key, "WorkstationLead", tracked.Entity.LeadId, tracked.Property(p => p.CrmNotes).OriginalValue, ct);
            await BackfillMetadataAsync(key, "WorkstationLead", tracked.Entity.LeadId, tracked.Entity.CrmNotes, ct);
        }
        var clients = db.ChangeTracker.Entries<ClientProfile>()
            .Where(x => x.State == EntityState.Added || x.State == EntityState.Modified && x.Property(p => p.CrmNotes).IsModified).ToArray();
        foreach (var tracked in clients)
        {
            var client = tracked.Entity;
            var key = await ResolveClientKeyAsync(client, ct);
            if (tracked.State == EntityState.Modified)
                await BackfillMetadataAsync(key, "ClientProfile", client.Id.ToString("N"), tracked.Property(p => p.CrmNotes).OriginalValue, ct);
            await BackfillMetadataAsync(key, "ClientProfile", client.Id.ToString("N"), client.CrmNotes, ct);
        }
    }

    public async Task<string> ResolveClientKeyAsync(ClientProfile client, CancellationToken ct = default)
    {
        var meta = ClientCrmMetaSerializer.Deserialize(client.CrmNotes);
        if (!string.IsNullOrWhiteSpace(meta.SourceWorkstationLeadId))
        {
            var source = db.WorkstationLeadProfiles.Local.FirstOrDefault(x => x.LeadId == meta.SourceWorkstationLeadId)
                ?? await db.WorkstationLeadProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.LeadId == meta.SourceWorkstationLeadId, ct);
            if (source is not null && ClientCrmMetaSerializer.Deserialize(source.CrmNotes).ConvertedClientProfileId == client.Id)
                return LeadKey(source.LeadId);
        }
        return ClientKey(client.Id);
    }

    public async Task BackfillMetadataAsync(string key, string source, string recordId, string? raw, CancellationToken ct = default)
    {
        var meta = ClientCrmMetaSerializer.Deserialize(raw);
        using var document = ReadLegacyDocument(raw);
        var originals = document.RootElement.TryGetProperty("activities", out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().ToArray() : [];
        var ordinal = 0;
        foreach (var item in meta.Activities ?? [])
        {
            if (item is null) { ordinal++; continue; }
            // Original IDs deduplicate conversion copies; no new timestamps or inferred actors.
            var original = ordinal < originals.Length ? originals[ordinal] : default;
            var hasId = original.ValueKind == JsonValueKind.Object && original.TryGetProperty("id", out var oldId) && oldId.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(oldId.GetString());
            var stableId = hasId ? item.Id : EntryId(key, source + ":" + recordId, "legacy-index:" + ordinal).ToString("N");
            var hasTime = original.ValueKind == JsonValueKind.Object && original.TryGetProperty("createdUtc", out var oldTime) && oldTime.ValueKind == JsonValueKind.String && oldTime.TryGetDateTime(out _);
            ordinal++;
            var payload = JsonSerializer.Serialize(new { legacy = original.ValueKind == JsonValueKind.Undefined ? (object)item : original, origin = source, recordId, originalRecordedTimeAvailable = hasTime });
            var originalId = EntryId(key, "LegacyActivity", stableId);
            var filed = db.Set<CrmHistoryEntry>().Local.FirstOrDefault(x => x.Id == originalId)
                ?? await db.Set<CrmHistoryEntry>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == originalId, ct);
            // Conversion copies may have different provenance but identical event content.
            var changed = filed is not null && LegacyContent(filed.ContentJson) != LegacyContent(payload);
            var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
            await StageAsync(new() { RelationshipKey = key, Source = changed ? "LegacyActivityRevision" : "LegacyActivity", SourceId = changed ? originalId.ToString("N") + ":" + revision : stableId,
                RelatedEntryId = changed ? originalId : null,
                Type = changed ? "Addendum" : item.Type, RecordedBy = changed ? "Legacy source revision; recorder unavailable" : item.CreatedBy ?? "Unknown historical recorder",
                RecordedUtc = !changed && hasTime ? item.CreatedUtc : DateTime.UtcNow, OccurredDate = string.IsNullOrWhiteSpace(item.Date) ? null : item.Date,
                TimePrecision = string.IsNullOrWhiteSpace(item.Date) ? "unknown" : "date",
                ContentJson = payload,
                EvidenceStatus = "Legacy evidence" }, ct);
        }
        var unparsed = document.RootElement.TryGetProperty("_unparsedLegacy", out var unparsedValue) ? unparsedValue.GetString() : null;
        var legacyText = unparsed ?? (!string.IsNullOrWhiteSpace(meta.AgentNotes) ? meta.AgentNotes : !string.IsNullOrWhiteSpace(raw) && !raw.TrimStart().StartsWith("{") ? raw : null);
        if (!string.IsNullOrWhiteSpace(legacyText))
            await StageAsync(new() { RelationshipKey = key, Source = "LegacyInternalNote", SourceId = source + ":" + recordId + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(legacyText))).ToLowerInvariant(),
                Type = "Note", RecordedBy = "Unknown historical recorder", TimePrecision = "unknown",
                ContentJson = JsonSerializer.Serialize(new { legacyInternalText = legacyText, origin = source, recordId }),
                EvidenceStatus = "Legacy evidence" }, ct);
    }
    private static string LegacyContent(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("legacy").GetRawText();
    }
    private static JsonDocument ReadLegacyDocument(string? raw)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) || !raw.TrimStart().StartsWith("{") ? "{}" : raw); }
        catch (JsonException) { return JsonDocument.Parse(JsonSerializer.Serialize(new { _unparsedLegacy = raw })); }
    }

    public async Task BackfillLegacySelfNotesAsync(string relationshipKey, string agentId, IEnumerable<string?> sourceKeys, CancellationToken ct = default)
    {
        if (!db.Database.IsRelational()) return;
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened) await connection.OpenAsync(ct);
        try
        {
            await using var exists = connection.CreateCommand();
            exists.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            exists.CommandText = db.Database.IsSqlite()
                ? "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='AgentLeadSelfNotes'"
                : "SELECT COUNT(*) FROM sys.tables WHERE name='AgentLeadSelfNotes' AND schema_id=SCHEMA_ID('dbo')";
            if (Convert.ToInt32(await exists.ExecuteScalarAsync(ct)) == 0) return;
            var keys = sourceKeys.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
            foreach (var key in keys)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
                command.CommandText = "SELECT AgentUserId, LeadId, NoteDate, WentWell, CouldBetter, CreatedUtc FROM AgentLeadSelfNotes WHERE LeadId=@id AND AgentUserId=@agent";
                var agent = agentId;
                foreach (var pair in new[] { ("@id", key), ("@agent", agent) })
                { var parameter = command.CreateParameter(); parameter.ParameterName = pair.Item1; parameter.Value = pair.Item2!; command.Parameters.Add(parameter); }
                var rows = new List<CrmHistoryEntry>();
                await using (var reader = await command.ExecuteReaderAsync(ct))
                {
                    while (await reader.ReadAsync(ct))
                    {
                        var day = reader.GetString(2);
                        rows.Add(new CrmHistoryEntry { RelationshipKey = relationshipKey,
                            Source = "LegacySelfNote", SourceId = EntryId(relationshipKey, "LegacySelfNote", System.Text.Json.JsonSerializer.Serialize(new { agent, key, day })).ToString("N"),
                            RecordedBy = reader.GetString(0), RecordedUtc = reader.GetDateTime(5),
                            OccurredDate = day, TimePrecision = "date", Type = "Note", EvidenceStatus = "Legacy internal reflection",
                            ContentJson = System.Text.Json.JsonSerializer.Serialize(new { legacyInternal = true, originalOwner = agent, sourceRelationshipId = key,
                                wentWell = reader.IsDBNull(3) ? null : reader.GetString(3),
                                couldBetter = reader.IsDBNull(4) ? null : reader.GetString(4) }) });
                    }
                }
                foreach (var row in rows) await StageAsync(row, ct);
            }
        }
        finally { if (opened) await connection.CloseAsync(); }
    }

}
