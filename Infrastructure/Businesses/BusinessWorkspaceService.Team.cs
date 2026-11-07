using Domain.Billing;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Identity;
using Infrastructure.WebsiteEditing;
using Microsoft.EntityFrameworkCore;
using ParfaitApp.Services;
using Shared.Auth;

namespace Infrastructure.Businesses;

public sealed record BusinessTeamInvitation(string Token, DateTime ExpiresUtc, string? IdentityRedemptionUrl);

public sealed record BusinessTeamPermissions(bool Storefront, bool Catalog, bool Orders, bool Analytics, bool? Crm,
    string? NativePageGrantsJson = null, bool UpdateNativePages = false, string? DisplayName = null, bool? Active = null);

public sealed partial class BusinessWorkspaceService
{
    public const string NativeParfaitTeamScope = "parfait-native";

    public async Task<CommerceManagementScope?> NativeParfaitTeamScopeAsync(CancellationToken ct = default)
    {
        var business = await db.CommerceBusinesses.AsNoTracking().SingleOrDefaultAsync(b =>
            b.Key == ParfaitBusinessScopeService.ParfaitBusinessKey && b.IsActive && b.Status == "Active", ct);
        if (business is null) return null;
        var configured = configuration?["Commerce:PublicBaseUrl"]?.Trim();
        if (string.IsNullOrEmpty(configured)) configured = "https://shopparfait.com";
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var origin) || origin.Scheme != "https" ||
            !string.IsNullOrEmpty(origin.UserInfo)) return null;
        return new(business.Id, business.Key, Guid.Empty, NativeParfaitTeamScope, business.Id.ToString("N"), origin.GetLeftPart(UriPartial.Authority));
    }

    private async Task<bool> CanManageTeamScopeAsync(CommerceStoreContextService stores, CommerceManagementScope scope,
        string actor, CancellationToken ct)
    {
        if (scope.WebsiteSiteKey != NativeParfaitTeamScope)
            return await stores.ResolveManagementActorAsync(scope, actor, "team", ct: ct) is not null;
        if (await NativeParfaitTeamScopeAsync(ct) != scope) return false;
        var founder = configuration?["Founder:Oid"] ?? configuration?["FOUNDER_OID"] ?? Environment.GetEnvironmentVariable("FOUNDER_OID");
        return Shared.Auth.FounderAuthority.IsConfiguredFounderIdentity(actor, founder) ||
            await BusinessWorkspaceAccess.ResolveActorMembershipAsync(db, scope.CommerceBusinessId, actor, "team", ct: ct) is not null;
    }

    public async Task<bool> SaveMemberPermissionsAsync(CommerceStoreContextService stores, CommerceManagementScope scope,
        string actor, Guid memberId, long expectedTicks, BusinessTeamPermissions permissions, CancellationToken ct)
    {
        if (!await CanManageTeamScopeAsync(stores, scope, actor, ct)) throw new UnauthorizedAccessException();
        if (permissions.UpdateNativePages && scope.WebsiteSiteKey != NativeParfaitTeamScope) throw new UnauthorizedAccessException();
        return await SaveMemberPermissionsCoreAsync(scope.CommerceBusinessId, memberId, expectedTicks, permissions, ct);
    }

    public async Task<bool> SaveTicketMemberPermissionsAsync(CommerceStoreContextService stores, string ticket,
        WebsiteEditorTicketProtector tickets, Guid memberId, long expectedTicks, BusinessTeamPermissions permissions, CancellationToken ct)
    {
        var identity = tickets.TryUnprotect(ticket);
        if (identity is null || string.IsNullOrWhiteSpace(identity.ActorUserId) || permissions.UpdateNativePages) throw new UnauthorizedAccessException();
        var store = await stores.ResolveForWebsiteTicketAsync(ticket, tickets, configuration!, ct);
        if (store is null) throw new UnauthorizedAccessException();
        if (identity.SiteKey == WebsiteEditorSiteKeys.Business && (!identity.ActorClientProfileId.HasValue ||
            identity.CommerceBusinessId != store.CommerceBusinessId || await BusinessWorkspaceAccess.ResolveAsync(db,
                store.CommerceBusinessId, identity.ActorClientProfileId.Value, identity.ActorUserId, identity.ActorEmail, "team", ct) is null))
            throw new UnauthorizedAccessException();
        return await SaveMemberPermissionsCoreAsync(store.CommerceBusinessId, memberId, expectedTicks, permissions, ct);
    }

    private async Task<bool> SaveMemberPermissionsCoreAsync(Guid businessId, Guid memberId, long expectedTicks,
        BusinessTeamPermissions permissions, CancellationToken ct)
    {
        var member = await db.CommerceBusinessMembers.SingleOrDefaultAsync(m => m.Id == memberId && m.CommerceBusinessId == businessId, ct);
        if (member is null) throw new KeyNotFoundException();
        if (ProtectedMember(member)) throw new UnauthorizedAccessException();
        if (expectedTicks <= 0 || member.UpdatedUtc.Ticks != expectedTicks) return false;
        member.CanManageStorefront = permissions.Storefront;
        member.CanManageCatalog = permissions.Catalog && permissions.Storefront;
        member.CanManageOrders = permissions.Orders && permissions.Storefront;
        member.CanManageAnalytics = permissions.Analytics;
        if (permissions.Crm.HasValue) member.CanManageCrm = permissions.Crm.Value;
        if (permissions.UpdateNativePages)
        {
            member.ParfaitPageGrantsJson = permissions.NativePageGrantsJson;
            if (permissions.DisplayName is not null) member.DisplayName = permissions.DisplayName.Trim();
            // A pending/revoked invitation cannot be activated through permission editing.
            if (permissions.Active == false && member.Status == "Active") member.Status = "Disabled";
            else if (permissions.Active == true && member.Status == "Disabled") member.Status = "Active";
        }
        member.UpdatedUtc = DateTime.UtcNow;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return false; }
        return true;
    }

    private static bool ProtectedMember(CommerceBusinessMember member) =>
        new[] { "owner", "account", "platform-owner" }.Contains(member.RoleKey, StringComparer.OrdinalIgnoreCase);

    public async Task<BusinessTeamInvitation> InviteMemberAsync(CommerceStoreContextService stores,
        CommerceManagementScope scope, string actor, string firstName, string lastName, string email,
        IClientEntraLifecycleService identities, CancellationToken ct, BusinessTeamPermissions? nativePermissions = null)
    {
        if (!await CanManageTeamScopeAsync(stores, scope, actor, ct))
            throw new UnauthorizedAccessException();
        if (nativePermissions is not null && scope.WebsiteSiteKey != NativeParfaitTeamScope) throw new UnauthorizedAccessException();
        email = email.Trim().ToLowerInvariant();
        if (!new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email) || email.Length > 320 ||
            string.IsNullOrWhiteSpace(firstName) || firstName.Length > 100 || lastName.Length > 100)
            throw new ArgumentException("Enter the member's name and current account email.");
        var member = await db.CommerceBusinessMembers.SingleOrDefaultAsync(m => m.CommerceBusinessId == scope.CommerceBusinessId && m.NormalizedEmail.ToLower() == email, ct);
        if (member is not null) await db.Entry(member).ReloadAsync(ct);
        if (member is not null && (ProtectedMember(member) || member.Status == "Active"))
            throw new InvalidOperationException("This member is already linked. Use their existing permissions or ownership controls.");
        var profiles = await db.ClientProfiles.Where(p => p.Email.ToLower() == email).Take(2).ToListAsync(ct);
        if (profiles.Count > 1) throw new InvalidOperationException("Resolve the duplicate account identity before inviting this member.");
        var profile = profiles.SingleOrDefault();
        if (profile is not null && !await EligibleTeamProfileAsync(profile, ct))
            throw new InvalidOperationException("This account is not an eligible active personal identity.");
        if (member?.ClientProfileId is Guid boundId && boundId != profile?.Id)
            throw new InvalidOperationException("This membership belongs to a different profile. Its identity cannot be rebound by email.");
        if (profile is not null)
        {
            var boundMembers = await db.CommerceBusinessMembers.Where(m => m.CommerceBusinessId == scope.CommerceBusinessId && m.ClientProfileId == profile.Id).Take(2).ToListAsync(ct);
            if (boundMembers.Count > 1 || (member is not null && boundMembers.Any(m => m.Id != member.Id)))
                throw new InvalidOperationException("Resolve duplicate memberships before inviting this account.");
            member ??= boundMembers.SingleOrDefault();
            if (member is not null) await db.Entry(member).ReloadAsync(ct);
            if (member is not null && (ProtectedMember(member) || member.Status == "Active"))
                throw new InvalidOperationException("This account already has a membership. Use its existing controls.");
        }
        string? redemption = null;
        if (profile is null)
        {
            // Reuse Entra provisioning; this does not create a subscription or grant
            // application/CRM permissions. Membership remains pending until exact OID acceptance.
            var identity = await identities.EnsureBusinessTeamIdentityAsync(firstName.Trim(), lastName.Trim(), email, ct);
            var id = IdentityKey.Normalize(identity.ObjectId);
            if (!Guid.TryParse(id, out _) || await db.ClientProfiles.AnyAsync(p => p.ClientUserId == id || p.ExternalIdentityObjectId == id, ct))
                throw new InvalidOperationException("The identity is already associated with another account. Resolve its current profile first.");
            profile = new ClientProfile { FirstName = firstName.Trim(), LastName = lastName.Trim(), Email = email,
                NormalizedEmail = email, ClientUserId = id, ExternalIdentityObjectId = id,
                AccountManagementMode = ClientAccountManagementModes.SelfManaged };
            db.ClientProfiles.Add(profile);
            redemption = identity.RequiresRedemption ? identity.RedemptionUrl : null;
        }
        // Recheck after a potentially slow provider operation. No provider receipt is
        // treated as authority to grant membership after the inviter was revoked.
        if (!await CanManageTeamScopeAsync(stores, scope, actor, ct))
            throw new UnauthorizedAccessException();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (member is null)
        {
            member = new CommerceBusinessMember { CommerceBusinessId = scope.CommerceBusinessId,
                Email = email, NormalizedEmail = email.ToUpperInvariant(), DisplayName = (firstName.Trim() + " " + lastName.Trim()).Trim(),
                RoleKey = "staff", CanManageStorefront = false, CanManageCatalog = false, CanManageOrders = false,
                CanManageAnalytics = false, CanManageTeam = false, CanManageCrm = false };
            db.CommerceBusinessMembers.Add(member);
        }
        if (member.Status == "Revoked")
        {
            member.CanManageStorefront = member.CanManageCatalog = member.CanManageOrders = false;
            member.CanManageAnalytics = member.CanManageCrm = member.CanManageTeam = false;
        }
        member.Email = email; member.NormalizedEmail = email.ToUpperInvariant(); member.RoleKey = "staff";
        member.ClientProfileId = profile.Id;
        member.Status = "Pending";
        if (nativePermissions is not null)
        {
            member.CanManageStorefront = nativePermissions.Storefront;
            member.CanManageCatalog = nativePermissions.Storefront && nativePermissions.Catalog;
            member.CanManageOrders = nativePermissions.Storefront && nativePermissions.Orders;
            member.CanManageAnalytics = nativePermissions.Analytics;
            member.CanManageCrm = nativePermissions.Crm == true;
            member.ParfaitPageGrantsJson = nativePermissions.NativePageGrantsJson;
        }
        member.UpdatedUtc = DateTime.UtcNow;
        await db.ClientIdentityContinuations.Where(i => i.CommerceBusinessMemberId == member.Id &&
            i.Purpose == ClientIdentityContinuationPurpose.BusinessMembership && i.ConsumedUtc == null)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.ConsumedUtc, DateTime.UtcNow), ct);
        var token = WebsiteEditorHandoffToken.Create();
        var expires = DateTime.UtcNow.AddDays(7);
        db.ClientIdentityContinuations.Add(new ClientIdentityContinuation
        {
            Purpose = ClientIdentityContinuationPurpose.BusinessMembership, ClientProfileId = profile.Id,
            CommerceBusinessId = scope.CommerceBusinessId, CommerceBusinessMemberId = member.Id,
            ActorUserId = actor, DestinationOrigin = scope.DestinationOrigin,
            WebsiteSiteKey = scope.WebsiteSiteKey, WebsiteOwnerKey = scope.WebsiteOwnerKey,
            IntendedNormalizedEmail = email, TokenHash = WebsiteEditorHandoffToken.Hash(token),
            ReturnUrl = "/internal/login", ExpiresUtc = expires
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(token, expires, redemption);
    }

    public async Task<string?> AcceptMemberInvitationAsync(string token, string authenticatedActor,
        CommerceStoreContextService stores, CancellationToken ct)
    {
        var hash = WebsiteEditorHandoffToken.Hash(token);
        var now = DateTime.UtcNow;
        var invitation = await db.ClientIdentityContinuations.AsNoTracking().SingleOrDefaultAsync(i =>
            i.TokenHash == hash && i.Purpose == ClientIdentityContinuationPurpose.BusinessMembership &&
            i.ConsumedUtc == null && i.ExpiresUtc > now, ct);
        if (invitation?.CommerceBusinessMemberId is not Guid memberId || invitation.ClientProfileId is not Guid profileId ||
            invitation.CommerceBusinessId is not Guid businessId) return null;
        var actor = IdentityKey.Normalize(authenticatedActor);
        var profile = await db.ClientProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.Id == profileId, ct);
        if (profile is null || !await EligibleTeamProfileAsync(profile, ct) || string.IsNullOrWhiteSpace(actor) ||
            !IdentityKey.NormalizeSet(new[] { profile.ClientUserId, profile.ExternalIdentityObjectId }).Contains(actor)) return null;
        foreach (var subject in IdentityKey.NormalizeSet(new[] { profile.ClientUserId, profile.ExternalIdentityObjectId }))
            if (!(await AccountLifecycleService.ReadAsync(db, new Domain.Accounts.AccountLifecycleSubject(subject,
                Domain.Messaging.MessagingParticipantTypes.Client, profileId), ct)).AllowsFullAccess) return null;
        CommerceManagementScope? scope;
        if (invitation.WebsiteSiteKey == NativeParfaitTeamScope)
        {
            scope = await NativeParfaitTeamScopeAsync(ct);
            if (scope is null || scope.CommerceBusinessId != businessId || scope.WebsiteOwnerKey != invitation.WebsiteOwnerKey ||
                scope.DestinationOrigin != invitation.DestinationOrigin) return null;
        }
        else
        {
            var state = await db.Set<WebsiteContentState>().AsNoTracking().SingleOrDefaultAsync(s =>
                s.SiteKey == invitation.WebsiteSiteKey && s.OwnerKey == invitation.WebsiteOwnerKey && s.ProvisionedStoreBusinessId == businessId, ct);
            if (state is null || string.IsNullOrWhiteSpace(invitation.DestinationOrigin)) return null;
            var businessKey = await db.CommerceBusinesses.Where(b => b.Id == businessId).Select(b => b.Key).SingleAsync(ct);
            scope = new(businessId, businessKey, state.Id, state.SiteKey, state.OwnerKey, invitation.DestinationOrigin);
        }
        if (string.IsNullOrWhiteSpace(invitation.ActorUserId) || !await CanManageTeamScopeAsync(stores, scope, invitation.ActorUserId, ct)) return null;
        now = DateTime.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var consumed = await db.ClientIdentityContinuations.Where(i => i.Id == invitation.Id && i.ConsumedUtc == null && i.ExpiresUtc > now)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.ConsumedUtc, now), ct);
        if (consumed != 1) return null;
        var changed = await db.CommerceBusinessMembers.Where(m => m.Id == memberId && m.CommerceBusinessId == businessId &&
            m.ClientProfileId == profileId && m.Status == "Pending" && m.RoleKey == "staff")
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, "Active").SetProperty(m => m.UpdatedUtc, now), ct);
        if (changed != 1) return null;
        await transaction.CommitAsync(ct);
        return scope.DestinationOrigin + "/internal/login" + (scope.WebsiteSiteKey == WebsiteEditorSiteKeys.Protect
            ? "?businessKey=" + Uri.EscapeDataString(scope.BusinessKey) : "");
    }

    private async Task<bool> EligibleTeamProfileAsync(ClientProfile profile, CancellationToken ct)
    {
        if (ClientRecordClassification.Resolve(profile.ClientUserId, profile.CrmNotes) != ClientRecordClassification.Client)
            return false;
        var ids = IdentityKey.NormalizeSet(new[] { profile.ClientUserId, profile.ExternalIdentityObjectId });
        if (ids.Count == 0) return false;
        foreach (var id in ids)
        {
            if (await db.ClientProfiles.AnyAsync(p => p.Id != profile.Id &&
                (p.ClientUserId.ToLower() == id || (p.ExternalIdentityObjectId ?? "").ToLower() == id), ct)) return false;
            if (!(await AccountLifecycleService.ReadAsync(db, new Domain.Accounts.AccountLifecycleSubject(id,
                Domain.Messaging.MessagingParticipantTypes.Client, profile.Id), ct)).AllowsFullAccess) return false;
        }
        return true;
    }

    public async Task<bool> RemoveMemberAsync(CommerceStoreContextService stores, CommerceManagementScope scope,
        string actor, Guid memberId, long expectedTicks, CancellationToken ct)
    {
        if (!await CanManageTeamScopeAsync(stores, scope, actor, ct))
            throw new UnauthorizedAccessException();
        var businessId = scope.CommerceBusinessId;
        var member = await db.CommerceBusinessMembers.AsNoTracking().SingleOrDefaultAsync(m => m.Id == memberId && m.CommerceBusinessId == businessId, ct);
        if (member is null || ProtectedMember(member)) throw new UnauthorizedAccessException();
        if (member.UpdatedUtc.Ticks != expectedTicks) return false;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var changed = await db.CommerceBusinessMembers.Where(m => m.Id == member.Id && m.UpdatedUtc == member.UpdatedUtc)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, "Revoked").SetProperty(m => m.UpdatedUtc, DateTime.UtcNow), ct);
        if (changed != 1) return false;
        await db.ClientIdentityContinuations.Where(i => i.CommerceBusinessMemberId == memberId && i.ConsumedUtc == null &&
            i.Purpose == ClientIdentityContinuationPurpose.BusinessMembership)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.ConsumedUtc, DateTime.UtcNow), ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}
