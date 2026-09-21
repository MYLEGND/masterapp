using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.WebsiteEditing;

public static class WebsiteTicketAuthorization
{
    public static async Task<WebsiteEditorTicket?> ResolveAsync(MasterAppDbContext db, WebsiteEditorTicketProtector tickets, IConfiguration configuration, string token, CancellationToken cancellationToken = default)
    {
        var ticket = tickets.TryUnprotect(token);
        if (ticket is null || string.IsNullOrWhiteSpace(ticket.ActorUserId)) return null;
        if (ticket.SiteKey == WebsiteEditorSiteKeys.Business)
        {
            if (!ticket.ActorClientProfileId.HasValue || !ticket.CommerceBusinessId.HasValue ||
                !await WebsiteBusinessAccess.CanManageAsync(db, ticket.CommerceBusinessId.Value, ticket.ActorClientProfileId.Value, cancellationToken)) return null;
            var profile = await db.ClientProfiles.AsNoTracking().SingleAsync(p => p.Id == ticket.ActorClientProfileId.Value, cancellationToken);
            return string.Equals(profile.ClientUserId, ticket.ActorUserId, StringComparison.OrdinalIgnoreCase) ? ticket : null;
        }
        var actor = ticket.ActorUserId.Trim().ToLowerInvariant();
        if (ticket.SiteKey == WebsiteEditorSiteKeys.Legend)
        {
            var founder = configuration["Founder:Oid"] ?? configuration["FOUNDER_OID"] ?? Environment.GetEnvironmentVariable("FOUNDER_OID");
            return ticket.IsFounder && ticket.OwnerUserId == WebsiteEditorSiteKeys.GlobalOwnerKey &&
                Shared.Auth.FounderAuthority.IsConfiguredFounderIdentity(actor, founder) ? ticket : null;
        }
        var agent = await db.AgentTrackingProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.AgentUserId.ToLower() == actor && p.Status.ToLower() == "active", cancellationToken);
        if (agent is null) return null;
        return string.Equals(agent.AgentUserId, ticket.OwnerUserId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(agent.Slug, ticket.AgentSlug, StringComparison.OrdinalIgnoreCase) ? ticket : null;
    }
}
