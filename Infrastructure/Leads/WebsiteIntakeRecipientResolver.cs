using System.ComponentModel.DataAnnotations;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shared.Analytics;
using Shared.Crm;

namespace Infrastructure.Leads;

/// <summary>Recipients come from active ownership/assignment records, never the public form.</summary>
public sealed class WebsiteIntakeRecipientResolver(MasterAppDbContext db, IConfiguration configuration)
{
    public async Task<List<BusinessIntakeRecipient>> BusinessOptionsAsync(Guid businessId, CancellationToken ct = default)
    {
        if (businessId == Guid.Empty || !await db.CommerceBusinesses.AnyAsync(x => x.Id == businessId && x.IsActive && x.Status == "Active", ct)) return [];
        var members = await db.CommerceBusinessMembers.AsNoTracking().Where(x => x.CommerceBusinessId == businessId && x.Status == "Active" &&
            (x.RoleKey == "owner" || x.CanManageOrders)).ToListAsync(ct);
        var result = new List<BusinessIntakeRecipient>();
        foreach (var member in members)
        {
            if (member.ClientProfileId is not { } profileId) continue;
            var profile = await db.ClientProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == profileId, ct);
            if (profile is null || !await WebsiteBusinessAccess.CanManageAsActorAsync(db, businessId, profileId, profile.ClientUserId, profile.Email, ct)) continue;
            if (ValidEmail(profile.Email)) result.Add(new("member:" + member.Id.ToString("D"), member.DisplayName + " · team", profile.Email.Trim()));
            var links = await db.AgentClients.AsNoTracking().Where(x => x.ClientUserId == profile.ClientUserId).ToListAsync(ct);
            foreach (var link in links)
            {
                var agent = await db.AgentTrackingProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.AgentUserId == link.AgentUserId && x.Status == "active", ct);
                if (agent is null || !ValidEmail(agent.AgentUpn) ||
                    !await WebsiteBusinessAccess.CanManageAsActorAsync(db, businessId, profileId, agent.AgentUserId, agent.AgentUpn, ct)) continue;
                var key = "agent:" + agent.Id.ToString("D");
                if (result.All(x => x.Key != key)) result.Add(new(key, (agent.DisplayName ?? agent.AgentUpn) + " · assigned agent", agent.AgentUpn.Trim()));
            }
        }
        return result;
    }

    public async Task<string?> ResolveAsync(MarketingOwnerScope owner, CancellationToken ct = default)
    {
        if (owner.CommerceBusinessId is { } id)
        {
            var options = await BusinessOptionsAsync(id, ct);
            var settings = await db.CommerceBusinessStorefrontSettings.AsNoTracking().SingleOrDefaultAsync(x => x.CommerceBusinessId == id, ct);
            var preferences = BusinessWorkspacePreferences.Read(settings?.WorkspacePreferencesJson);
            var key = preferences.NotificationMemberId is { } member ? "member:" + member.ToString("D") :
                preferences.NotificationAgentTrackingProfileId is { } agent ? "agent:" + agent.ToString("D") : null;
            // A removed assignment must block delivery, not silently reroute it.
            if (key is not null) return options.SingleOrDefault(x => x.Key == key)?.Email;
            var business = await db.CommerceBusinesses.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (business is null) return null;
            return options.FirstOrDefault(x => x.Key.StartsWith("member:") && x.Email.Equals(business.OwnerEmail, StringComparison.OrdinalIgnoreCase))?.Email;
        }
        if (owner.AgentTrackingProfileId is { } trackingId)
        {
            var agent = await db.AgentTrackingProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == trackingId && x.Status == "active", ct);
            return ValidEmail(agent?.AgentUpn) ? agent!.AgentUpn.Trim() : null;
        }
        var founder = configuration["Contact:RecipientEmail"];
        return ValidEmail(founder) ? founder!.Trim() : null;
    }

    private static bool ValidEmail(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 254 && !value.Any(char.IsControl) && new EmailAddressAttribute().IsValid(value);
}
