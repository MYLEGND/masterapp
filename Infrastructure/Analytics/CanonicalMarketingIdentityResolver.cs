using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;
using Shared.Crm;

namespace Infrastructure.Analytics;

/// <summary>
/// Provider-neutral customer identity resolved from one canonical AnalyticsEvent and its
/// durable lead/CRM lineage. Provider adapters may transform this identity, but must not
/// independently select a different customer for the same canonical outcome.
/// </summary>
public sealed record CanonicalMarketingIdentity(
    AnalyticsEvent Source,
    WebsiteLead? WebsiteLead,
    Guid? WebsiteLeadId,
    string? WorkstationLeadId,
    string? ClientUserId,
    string? Email,
    string? Phone,
    string? FirstName,
    string? LastName,
    DateTime? DateOfBirth,
    string? Gender,
    string? City,
    string? State,
    string? PostalCode,
    string? Country,
    string? ClientIpAddress,
    string? ClientUserAgent,
    string? Fbclid,
    string? Fbc,
    string? Fbp,
    string? Obref,
    IReadOnlyList<string> ExternalIds)
{
    public bool HasContactData =>
        !string.IsNullOrWhiteSpace(Email) ||
        !string.IsNullOrWhiteSpace(Phone) ||
        !string.IsNullOrWhiteSpace(FirstName) ||
        !string.IsNullOrWhiteSpace(LastName) ||
        DateOfBirth.HasValue ||
        !string.IsNullOrWhiteSpace(Gender) ||
        !string.IsNullOrWhiteSpace(City) ||
        !string.IsNullOrWhiteSpace(State) ||
        !string.IsNullOrWhiteSpace(PostalCode) ||
        !string.IsNullOrWhiteSpace(Country);
}

/// <summary>Single raw-identity authority shared by Meta and OpenAI delivery.</summary>
public static class CanonicalMarketingIdentityResolver
{
    public static async Task<CanonicalMarketingIdentity> ResolveAsync(
        MasterAppDbContext db,
        AnalyticsEvent source,
        Guid? preferredWebsiteLeadId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(source);

        var sourceLeadKey = Clean(CanonicalAdvertisingEventProjection.ReadString(source.MetadataJson, "leadId"));
        var sourceClientUserId = Clean(CanonicalAdvertisingEventProjection.ReadString(source.MetadataJson, "clientUserId"));
        var websiteLeadId = preferredWebsiteLeadId is { } preferred && preferred != Guid.Empty
            ? preferred
            : ReadGuid(source.MetadataJson, "WebsiteLeadId")
              ?? ReadGuid(source.MetadataJson, "LeadId");

        WebsiteLeadIntakeLink? intake = null;
        WebsiteLead? websiteLead = null;

        if (websiteLeadId.HasValue)
        {
            websiteLead = await db.WebsiteLeads.AsNoTracking()
                .SingleOrDefaultAsync(x => x.LeadId == websiteLeadId.Value, ct);
            if (websiteLead is not null && !OwnerMatches(source, websiteLead))
                websiteLead = null;
        }

        if (websiteLead is null && !string.IsNullOrWhiteSpace(sourceLeadKey))
        {
            intake = await db.WebsiteLeadIntakeLinks.AsNoTracking()
                .Where(x => x.WorkstationLeadId == sourceLeadKey)
                .OrderByDescending(x => x.SubmittedUtc)
                .ThenByDescending(x => x.CapturedUtc)
                .FirstOrDefaultAsync(ct);
            if (intake is not null)
            {
                var candidate = await db.WebsiteLeads.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.LeadId == intake.WebsiteLeadPublicId, ct);
                if (candidate is not null && OwnerMatches(source, candidate))
                    websiteLead = candidate;
                else
                    intake = null;
            }
        }

        if (websiteLead is null && !string.IsNullOrWhiteSpace(sourceClientUserId))
        {
            var client = await db.ClientProfiles.AsNoTracking()
                .Where(x => x.ClientUserId == sourceClientUserId)
                .Select(x => new { x.CrmNotes })
                .FirstOrDefaultAsync(ct);
            var sourceWorkstationLeadId = Clean(ClientCrmMetaSerializer.Deserialize(client?.CrmNotes).SourceWorkstationLeadId);
            if (sourceWorkstationLeadId is not null)
            {
                intake = await db.WebsiteLeadIntakeLinks.AsNoTracking()
                    .Where(x => x.WorkstationLeadId == sourceWorkstationLeadId)
                    .OrderByDescending(x => x.SubmittedUtc)
                    .ThenByDescending(x => x.CapturedUtc)
                    .FirstOrDefaultAsync(ct);
                if (intake is not null)
                {
                    var candidate = await db.WebsiteLeads.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.LeadId == intake.WebsiteLeadPublicId, ct);
                    if (candidate is not null && OwnerMatches(source, candidate))
                        websiteLead = candidate;
                    else
                        intake = null;
                }
            }
        }

        if (websiteLead is null &&
            (!string.IsNullOrWhiteSpace(source.SessionId) || !string.IsNullOrWhiteSpace(source.VisitorId)))
        {
            var from = source.EventUtc.AddDays(-7);
            var to = source.EventUtc.AddDays(2);
            var query = db.WebsiteLeads.AsNoTracking()
                .Where(x => x.CreatedUtc >= from && x.CreatedUtc <= to &&
                            x.CommerceBusinessId == source.CommerceBusinessId);

            if (source.CommerceBusinessId.HasValue)
                query = query.Where(x => x.AgentTrackingProfileId == null);
            else if (source.AgentTrackingProfileId.HasValue)
                query = query.Where(x => x.AgentTrackingProfileId == source.AgentTrackingProfileId);
            else
                query = query.Where(x => x.AgentTrackingProfileId == null);

            query = !string.IsNullOrWhiteSpace(source.SessionId)
                ? query.Where(x => x.SessionId == source.SessionId)
                : query.Where(x => x.VisitorId == source.VisitorId);

            websiteLead = await query.OrderByDescending(x => x.CreatedUtc).FirstOrDefaultAsync(ct);
        }

        websiteLeadId = websiteLead?.LeadId ?? intake?.WebsiteLeadPublicId ?? websiteLeadId;
        if (intake is null && websiteLeadId.HasValue)
        {
            intake = await db.WebsiteLeadIntakeLinks.AsNoTracking()
                .Where(x => x.WebsiteLeadPublicId == websiteLeadId.Value)
                .OrderByDescending(x => x.SubmittedUtc)
                .ThenByDescending(x => x.CapturedUtc)
                .FirstOrDefaultAsync(ct);
        }

        var workstationLeadId = Clean(intake?.WorkstationLeadId) ?? (!Guid.TryParse(sourceLeadKey, out _) ? sourceLeadKey : null);
        var clientUserId = sourceClientUserId;

        var crm = await ResolveCrmAsync(db, source, workstationLeadId, clientUserId, ct);
        if (clientUserId is null)
            clientUserId = crm.ClientUserId;

        var metadataEmail = ReadCustomer(source.MetadataJson, "email");
        var metadataPhone = ReadCustomer(source.MetadataJson, "phone");
        var metadataFirstName = ReadCustomer(source.MetadataJson, "firstName");
        var metadataLastName = ReadCustomer(source.MetadataJson, "lastName");
        var metadataCity = ReadCustomer(source.MetadataJson, "city");
        var metadataState = ReadCustomer(source.MetadataJson, "state") ?? ReadCustomer(source.MetadataJson, "region");
        var metadataPostal = ReadCustomer(source.MetadataJson, "postalCode") ?? ReadCustomer(source.MetadataJson, "zipCode");
        var metadataCountry = ReadCustomer(source.MetadataJson, "country") ?? ReadCustomer(source.MetadataJson, "countryCode");

        var obref = OpenAiBrowserReference.Normalize(
            CanonicalAdvertisingEventProjection.ReadString(source.MetadataJson, "obref"))
            ?? OpenAiBrowserReference.Normalize(CanonicalAdvertisingEventProjection.ReadString(websiteLead?.MetadataJson, "obref"));

        if (obref is null && websiteLead is not null &&
            (!string.IsNullOrWhiteSpace(websiteLead.SessionId) || !string.IsNullOrWhiteSpace(websiteLead.VisitorId)))
        {
            var relatedQuery = db.AnalyticsEvents.AsNoTracking()
                .Where(x => x.Id != source.Id &&
                            x.CommerceBusinessId == source.CommerceBusinessId &&
                            x.AgentTrackingProfileId == source.AgentTrackingProfileId &&
                            x.EventUtc >= websiteLead.CreatedUtc.AddDays(-1) &&
                            x.EventUtc <= websiteLead.CreatedUtc.AddDays(1));

            relatedQuery = !string.IsNullOrWhiteSpace(websiteLead.SessionId)
                ? relatedQuery.Where(x => x.SessionId == websiteLead.SessionId)
                : relatedQuery.Where(x => x.VisitorId == websiteLead.VisitorId);

            var related = await relatedQuery.OrderByDescending(x => x.Id).Take(100).ToListAsync(ct);
            obref = related.Select(x => OpenAiBrowserReference.Normalize(
                    CanonicalAdvertisingEventProjection.ReadString(x.MetadataJson, "obref")))
                .FirstOrDefault(x => x is not null);
        }

        var externalIds = new[]
        {
            websiteLeadId?.ToString("N"),
            workstationLeadId,
            clientUserId
        }
        .Select(Clean)
        .Where(x => x is not null)
        .Cast<string>()
        .Distinct(StringComparer.Ordinal)
        .Take(3)
        .ToArray();

        return new CanonicalMarketingIdentity(
            source,
            websiteLead,
            websiteLeadId,
            workstationLeadId,
            clientUserId,
            FirstNonBlank(websiteLead?.Email, crm.Email, metadataEmail),
            FirstNonBlank(websiteLead?.Phone, crm.Phone, metadataPhone),
            FirstNonBlank(websiteLead?.FirstName, crm.FirstName, metadataFirstName),
            FirstNonBlank(websiteLead?.LastName, crm.LastName, metadataLastName),
            crm.DateOfBirth,
            crm.Gender,
            FirstNonBlank(crm.City, metadataCity),
            FirstNonBlank(crm.State, metadataState),
            FirstNonBlank(crm.PostalCode, metadataPostal),
            FirstNonBlank(crm.Country, metadataCountry),
            FirstNonBlank(websiteLead?.ClientIpAddress, source.IpAddress,
                CanonicalAdvertisingEventProjection.ReadString(source.MetadataJson, "sourceClientIpAddress")),
            FirstNonBlank(websiteLead?.ClientUserAgent, source.UserAgent,
                CanonicalAdvertisingEventProjection.ReadString(source.MetadataJson, "sourceClientUserAgent")),
            FirstNonBlank(websiteLead?.Fbclid, source.Fbclid,
                CanonicalAdvertisingEventProjection.ReadString(source.MetadataJson, "fbclid")),
            FirstNonBlank(websiteLead?.Fbc,
                CanonicalAdvertisingEventProjection.ReadString(source.MetadataJson, "fbc")),
            FirstNonBlank(websiteLead?.Fbp,
                CanonicalAdvertisingEventProjection.ReadString(source.MetadataJson, "fbp")),
            obref,
            externalIds);
    }

    private static bool OwnerMatches(AnalyticsEvent source, WebsiteLead lead)
    {
        if (source.CommerceBusinessId.HasValue)
            return lead.CommerceBusinessId == source.CommerceBusinessId &&
                   !lead.AgentTrackingProfileId.HasValue;
        if (source.AgentTrackingProfileId.HasValue)
            return !lead.CommerceBusinessId.HasValue &&
                   lead.AgentTrackingProfileId == source.AgentTrackingProfileId;
        return !lead.CommerceBusinessId.HasValue && !lead.AgentTrackingProfileId.HasValue;
    }

    private static async Task<CrmIdentity> ResolveCrmAsync(
        MasterAppDbContext db,
        AnalyticsEvent source,
        string? workstationLeadId,
        string? clientUserId,
        CancellationToken ct)
    {
        if (!source.CommerceBusinessId.HasValue && !string.IsNullOrWhiteSpace(clientUserId))
        {
            var client = await db.ClientProfiles.AsNoTracking()
                .Where(x => x.ClientUserId == clientUserId)
                .Select(x => new
                {
                    x.ClientUserId, x.Email, x.Phone, x.FirstName, x.LastName, x.DOB, x.CrmNotes
                })
                .FirstOrDefaultAsync(ct);
            if (client is not null)
            {
                var meta = ClientCrmMetaSerializer.Deserialize(client.CrmNotes);
                return new(client.ClientUserId, client.Email, client.Phone, client.FirstName, client.LastName,
                    client.DOB ?? meta.DOB, meta.Gender, meta.City, meta.State, meta.ZipCode, null);
            }
        }

        var key = Clean(workstationLeadId) ?? Clean(clientUserId);
        if (key is not null)
        {
            string? agentUserId = null;
            if (source.AgentTrackingProfileId.HasValue)
            {
                agentUserId = await db.AgentTrackingProfiles.AsNoTracking()
                    .Where(x => x.Id == source.AgentTrackingProfileId.Value)
                    .Select(x => x.AgentUserId)
                    .FirstOrDefaultAsync(ct);
            }

            var leadQuery = db.WorkstationLeadProfiles.AsNoTracking()
                .Where(x => x.LeadId == key && x.CommerceBusinessId == source.CommerceBusinessId);
            if (!string.IsNullOrWhiteSpace(agentUserId))
                leadQuery = leadQuery.Where(x => x.AgentUserId == agentUserId);

            var lead = await leadQuery.Select(x => new CrmIdentity(
                    null, x.Email, x.Phone, x.FirstName, x.LastName, x.DOB, x.Gender,
                    x.City, x.State, x.ZipCode, null))
                .FirstOrDefaultAsync(ct);
            if (lead is not null) return lead;
        }

        return new(null, null, null, null, null, null, null, null, null, null, null);
    }

    private sealed record CrmIdentity(
        string? ClientUserId,
        string? Email,
        string? Phone,
        string? FirstName,
        string? LastName,
        DateTime? DateOfBirth,
        string? Gender,
        string? City,
        string? State,
        string? PostalCode,
        string? Country);

    private static Guid? ReadGuid(string? json, string name)
        => Guid.TryParse(CanonicalAdvertisingEventProjection.ReadString(json, name), out var value) &&
           value != Guid.Empty ? value : null;

    private static string? ReadCustomer(string? json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ReadCustomer(doc.RootElement, propertyName, 0);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadCustomer(JsonElement root, string propertyName, int depth)
    {
        if (depth > 5 || root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("customer", out var customer) &&
            customer.ValueKind == JsonValueKind.Object &&
            customer.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String)
            return Clean(value.GetString());

        foreach (var containerName in new[] { "payload", "metadata", "analyticsMetadata" })
            if (root.TryGetProperty(containerName, out var container) &&
                ReadCustomer(container, propertyName, depth + 1) is { } nested)
                return nested;
        return null;
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.Select(Clean).FirstOrDefault(x => x is not null);

    private static string? Clean(string? value)
    {
        var clean = value?.Trim();
        return string.IsNullOrWhiteSpace(clean) ? null : clean;
    }
}
