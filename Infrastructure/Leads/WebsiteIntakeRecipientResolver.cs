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
        if (owner.CommerceBusinessId is { } businessId)
        {
            var businessEmail = await db.CommerceBusinesses.AsNoTracking()
                .Where(x => x.Id == businessId && x.IsActive && x.Status == "Active")
                .Select(x => x.OwnerEmail)
                .SingleOrDefaultAsync(ct);
            return ValidEmail(businessEmail) ? businessEmail!.Trim() : null;
        }

        if (owner.AgentTrackingProfileId is { } trackingId)
        {
            var tracking = await db.AgentTrackingProfiles.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == trackingId && x.Status == "active", ct);
            return tracking is null ? null : await ResolveAgentPrimaryEmailAsync(tracking, ct);
        }

        if (owner != MarketingOwnerScope.Founder)
            return null;

        var founderEmail = FirstValidEmail(
            configuration["Founder:Email"],
            configuration["Founder:Upn"]);
        if (founderEmail is null)
            return null;

        var normalizedFounder = founderEmail.ToLowerInvariant();
        var founderTracking = await db.AgentTrackingProfiles.AsNoTracking()
            .Where(x => x.Status == "active" && x.AgentUpn.ToLower() == normalizedFounder)
            .OrderByDescending(x => x.UpdatedUtc)
            .FirstOrDefaultAsync(ct);
        if (founderTracking is not null)
            return await ResolveAgentPrimaryEmailAsync(founderTracking, ct);

        var founderAccount = await db.AgentProfiles.AsNoTracking()
            .Where(x => x.IsActive &&
                (x.NormalizedEmail == normalizedFounder || x.AgentUpn.ToLower() == normalizedFounder))
            .OrderByDescending(x => x.UpdatedUtc)
            .Select(x => new { x.NormalizedEmail, x.AgentUpn })
            .FirstOrDefaultAsync(ct);

        return FirstValidEmail(
            founderAccount?.NormalizedEmail,
            founderAccount?.AgentUpn,
            founderEmail);
    }

    private async Task<string?> ResolveAgentPrimaryEmailAsync(AgentTrackingProfile tracking, CancellationToken ct)
    {
        var account = await db.AgentProfiles.AsNoTracking()
            .Where(x => x.IsActive && x.AgentUserId == tracking.AgentUserId)
            .OrderByDescending(x => x.UpdatedUtc)
            .Select(x => new { x.NormalizedEmail, x.AgentUpn })
            .FirstOrDefaultAsync(ct);

        return FirstValidEmail(
            account?.NormalizedEmail,
            account?.AgentUpn,
            tracking.AgentUpn);
    }

    private static string? FirstValidEmail(params string?[] candidates)
    {
        foreach (var candidate in candidates)
            if (ValidEmail(candidate))
                return candidate!.Trim();
        return null;
    }

    private static bool ValidEmail(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 254 && !value.Any(char.IsControl) && new EmailAddressAttribute().IsValid(value);
}
