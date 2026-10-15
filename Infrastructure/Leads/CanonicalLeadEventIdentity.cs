using Domain.Entities;
using Infrastructure.Analytics;
using Shared.Meta;

namespace Infrastructure.Leads;

/// <summary>Stable confirmed-lead outcome identity shared by source persistence and delivery receipts.</summary>
public static class CanonicalLeadEventIdentity
{
    public static string Resolve(WebsiteLead lead)
    {
        ArgumentNullException.ThrowIfNull(lead);
        if (lead.LeadId == Guid.Empty) throw new ArgumentException("A persisted lead identity is required.", nameof(lead));
        // Required historical adapter: preserve an already issued browser/server destination ID.
        var historical = MetaLeadTrackingJson.Read(lead.MetadataJson)?.EventId;
        if (!string.IsNullOrWhiteSpace(historical)) return historical;
        return CanonicalAdvertisingEventProjection.ScopeEventId(lead.CommerceBusinessId, "lead_" + lead.LeadId.ToString("N"));
    }
}
