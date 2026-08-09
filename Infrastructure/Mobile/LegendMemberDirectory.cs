using Domain.Billing;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Mobile;

/// <summary>
/// Authoritative eligibility boundary for every member-facing directory.
///
/// CRM leads remain available to agents in their CRM workflow, but they are not
/// Legend members. A person enters the shared member directory only after their
/// ClientApp entitlement is active (or in its supported grace period) and their
/// CRM record is a Client or Business Client record. Keeping that decision here
/// prevents discovery, recommendations, messaging pickers, and Founder controls
/// from drifting into separate interpretations of "active member".
/// </summary>
public static class LegendMemberDirectory
{
    private static readonly string[] UnavailableCrmStatuses =
    [
        "dormant",
        "inactive",
        "lead",
        "prospect",
        "deleted",
        "blocked",
        "suspended",
        "cancelled",
        "canceled",
        "paused"
    ];

    /// <summary>
    /// Applies the database-resolvable part of the common-member rule. The
    /// record-type metadata is evaluated by <see cref="IsMemberRecord"/> after
    /// materialization because it is JSON persisted for CRM compatibility.
    /// </summary>
    public static IQueryable<ClientProfile> ActiveSubscribedProfiles(
        MasterAppDbContext db,
        IQueryable<ClientProfile>? source = null)
    {
        ArgumentNullException.ThrowIfNull(db);

        var profiles = source ?? db.ClientProfiles.AsNoTracking();
        return profiles
            .Where(profile => profile.CrmStatus == null ||
                !UnavailableCrmStatuses.Contains(profile.CrmStatus.ToLower()))
            .Where(profile => db.ClientEntitlements.Any(entitlement =>
                entitlement.ClientProfileId == profile.Id &&
                entitlement.EntitlementKey == BillingEntitlementKeys.ClientAppFullAccess &&
                (entitlement.Status == ClientEntitlementStatus.Active ||
                 entitlement.Status == ClientEntitlementStatus.GracePeriod)));
    }

    /// <summary>
    /// Completes the common-member rule for an already materialized client
    /// record. A Lead or Prospect is never a public/member-directory candidate,
    /// even if legacy data accidentally gives it a current entitlement.
    /// </summary>
    public static bool IsMemberRecord(ClientProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return IsMemberRecord(
            profile.ClientUserId,
            profile.CrmNotes,
            profile.CrmStatus);
    }

    public static bool IsMemberRecord(
        string? clientUserId,
        string? crmNotes,
        string? crmStatus) =>
        !string.IsNullOrWhiteSpace(clientUserId) &&
        IsAvailableCrmStatus(crmStatus) &&
        ClientRecordClassification.IsClientOrBusinessClient(
            clientUserId,
            crmNotes,
            crmStatus);

    /// <summary>
    /// Returns the stable member identity used to collapse legacy and current
    /// profile representations before they reach a member-facing result list.
    /// </summary>
    public static string CanonicalIdentityKey(ClientProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return CanonicalIdentityKey(
            profile.ClientUserId,
            profile.ExternalIdentityObjectId);
    }

    public static string CanonicalIdentityKey(
        string? clientUserId,
        string? externalIdentityObjectId)
    {
        var canonical = string.IsNullOrWhiteSpace(externalIdentityObjectId)
            ? clientUserId
            : externalIdentityObjectId;
        return canonical?.Trim().ToLowerInvariant() ?? string.Empty;
    }

    /// <summary>
    /// Applies the record-type check and guarantees one profile per canonical
    /// member identity. The newest canonical record wins deterministically.
    /// </summary>
    public static IReadOnlyList<ClientProfile> Collapse(
        IEnumerable<ClientProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        return profiles
            .Where(IsMemberRecord)
            .GroupBy(CanonicalIdentityKey, StringComparer.Ordinal)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group => group
                .OrderByDescending(profile => profile.UpdatedUtc)
                .ThenByDescending(profile => profile.CreatedUtc)
                .ThenBy(profile => profile.Id)
                .First())
            .ToArray();
    }

    private static bool IsAvailableCrmStatus(string? crmStatus) =>
        string.IsNullOrWhiteSpace(crmStatus) ||
        !UnavailableCrmStatuses.Contains(
            crmStatus.Trim(),
            StringComparer.OrdinalIgnoreCase);
}
