using System.Security.Claims;
using System.Text.Json;
using Domain.Entities;
using Infrastructure.Businesses;
using Infrastructure.Data;
using Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using ParfaitApp.Models;
using ParfaitApp.Security;

namespace ParfaitApp.Services;

// Membership and native page restrictions share the canonical member row. The old
// team file is retained as historical evidence, but is never read or written here.
public sealed class ParfaitTeamAccessService(MasterAppDbContext db, ParfaitBusinessScopeService parfait,
    IParfaitInternalPageRegistry pages, BusinessWorkspaceService workspace, CommerceStoreContextService stores,
    IHttpContextAccessor http, Lazy<IClientEntraLifecycleService> identities, IGraphMailService mail,
    IConfiguration configuration) : IParfaitTeamAccessService
{
    private static readonly string[] Capabilities = ["website", "catalog", "orders", "automations", "analytics", "crm", "team", "settings"];
    private ClaimsPrincipal User => http.HttpContext?.User ?? new ClaimsPrincipal();
    private static string Actor(ClaimsPrincipal user) => user.FindFirstValue("oid") ?? user.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier") ?? "";
    private async Task<CommerceBusinessMember?> MemberAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        if (user.Identity?.IsAuthenticated != true || !Guid.TryParse(Actor(user), out _)) return null;
        var business = await parfait.FindExistingParfaitAsync(ct);
        if (business is null || business.Status != "Active") return null;
        foreach (var capability in Capabilities)
        {
            var member = await BusinessWorkspaceAccess.ResolveActorMembershipAsync(db, business.Id, Actor(user), capability, ct: ct);
            if (member is not null) return member;
        }
        return null;
    }
    private static string? Capability(string key) => key.ToLowerInvariant() switch
    {
        "/internal/dashboard" or "/internal/analytics" => "analytics",
        "/internal/commerce/products" => "catalog",
        "/internal/commerce/orders" or "/internal/commerce/orders/shipping" or "/internal/commerce/orders/reference-label" => "orders",
        "/internal/automations" => "automations",
        "/internal/settings/business-profile" => "settings",
        "/internal/settings/shipping" => "settings",
        "/internal/settings/receptionist" => "settings",
        "/internal/settings/team" => "team",
        _ => null
    };
    private static HashSet<string>? Restrictions(CommerceBusinessMember member)
    {
        if (member.ParfaitPageGrantsJson is null) return null;
        try { return JsonSerializer.Deserialize<HashSet<string>>(member.ParfaitPageGrantsJson)?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? []; }
        catch (JsonException) { return []; }
    }
    private static bool Allows(CommerceBusinessMember member, ParfaitInternalPageDefinition page)
    {
        var capability = Capability(page.Key);
        if (page.FounderOnly || capability is null || !BusinessWorkspaceAccess.Allows(member, capability)) return false;
        var restrictions = Restrictions(member);
        // Hidden order operations inherit the same visible order-page restriction.
        var key = page.Key.StartsWith("/internal/commerce/orders/", StringComparison.OrdinalIgnoreCase) ? "/internal/commerce/orders" : page.Key;
        return restrictions is null || restrictions.Contains(key);
    }
    public async Task<ParfaitTeamSignInResult> AuthorizeSignInAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        var founder = ParfaitFounderGuard.IsFounder(user);
        var allowed = founder || await MemberAsync(user, ct) is not null;
        return new() { Allowed = allowed, IsFounder = founder, Message = allowed ? "Team access granted." : "This Microsoft account does not have active Parfait team access." };
    }
    public async Task<bool> ValidatePrincipalAsync(ClaimsPrincipal user, CancellationToken ct = default) =>
        user.Identity?.IsAuthenticated == true && (ParfaitFounderGuard.IsFounder(user) || await MemberAsync(user, ct) is not null);
    public async Task<ParfaitPageAccessResult> AuthorizePageAsync(ClaimsPrincipal user, string path, string? pageKey = null, CancellationToken ct = default)
    {
        var page = string.IsNullOrWhiteSpace(pageKey) ? pages.ResolveByPath(path) : pages.ResolveByKey(pageKey);
        var founder = user.Identity?.IsAuthenticated == true && ParfaitFounderGuard.IsFounder(user);
        var member = founder ? null : await MemberAsync(user, ct);
        var allowed = page is not null && (founder || (member is not null && Allows(member, page)));
        return new() { Allowed = allowed, IsFounder = founder, Page = page, Message = allowed ? "Page access granted." : "This page is not available for this account." };
    }
    public async Task<IReadOnlyList<ParfaitInternalPageDefinition>> GetVisiblePagesAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        var all = pages.GetAllPages().Where(p => p.ShowInNavigation).ToList();
        if (ParfaitFounderGuard.IsFounder(user)) return all;
        var member = await MemberAsync(user, ct);
        return member is null ? [] : all.Where(p => Allows(member, p)).ToList();
    }
    public async Task<ParfaitInternalPageDefinition?> GetFirstVisiblePageAsync(ClaimsPrincipal user, CancellationToken ct = default) => (await GetVisiblePagesAsync(user, ct)).FirstOrDefault();
    private async Task<CommerceManagementScope> AdminScopeAsync(CancellationToken ct)
    {
        var scope = await workspace.NativeParfaitTeamScopeAsync(ct) ?? throw new InvalidOperationException("Parfait is not available.");
        if (!ParfaitFounderGuard.IsFounder(User) && !(await AuthorizePageAsync(User, "/internal/settings/team", ct: ct)).Allowed)
            throw new UnauthorizedAccessException();
        return scope;
    }
    private static bool Protected(CommerceBusinessMember member) => new[] { "owner", "account", "platform-owner" }.Contains(member.RoleKey, StringComparer.OrdinalIgnoreCase);
    private static string Preset(CommerceBusinessMember member) => Protected(member) ? ParfaitTeamRoles.FullControl :
        member.CanManageStorefront && member.CanManageAnalytics ? ParfaitTeamRoles.Manager : member.CanManageOrders || member.CanManageCatalog ? ParfaitTeamRoles.Support : ParfaitTeamRoles.Analyst;
    private BusinessTeamPermissions Permissions(string role, IEnumerable<string>? selected, string? displayName = null, bool? active = null)
    {
        if (role is not (ParfaitTeamRoles.Manager or ParfaitTeamRoles.Support or ParfaitTeamRoles.Analyst))
            throw new InvalidOperationException("Ownership is managed through canonical business ownership controls.");
        var manager = role == ParfaitTeamRoles.Manager; var support = role == ParfaitTeamRoles.Support;
        var keys = selected?.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (keys is not null && keys.Any(k => pages.ResolveByKey(k) is not { FounderOnly: false } || Capability(k) is null))
            throw new InvalidOperationException("Select only available native pages.");
        return new(manager || support, manager || support, manager || support, manager || !support, null,
            keys is null ? null : JsonSerializer.Serialize(keys), true, displayName, active);
    }
    public async Task<ParfaitTeamManagementViewModel> GetTeamManagementViewModelAsync(CancellationToken ct = default)
    {
        var scope = await AdminScopeAsync(ct);
        var all = pages.GetAllPages();
        var members = await db.CommerceBusinessMembers.AsNoTracking().Where(m => m.CommerceBusinessId == scope.CommerceBusinessId).OrderBy(m => m.DisplayName).ToListAsync(ct);
        var owners = members.Where(m => m.Status == "Active" && Protected(m)).ToList();
        var ownerProfileIds = owners.Where(m => m.ClientProfileId.HasValue).Select(m => m.ClientProfileId!.Value).ToList();
        var ownerProfiles = await db.ClientProfiles.AsNoTracking().Where(p => ownerProfileIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Email, ct);
        var ownerEmails = owners.Select(m => m.ClientProfileId.HasValue && ownerProfiles.TryGetValue(m.ClientProfileId.Value, out var email) && !string.IsNullOrWhiteSpace(email) ? email : m.Email)
            .Where(email => !string.IsNullOrWhiteSpace(email)).Distinct(StringComparer.OrdinalIgnoreCase);
        return new() {
            FounderEmail = string.Join(", ", ownerEmails), FounderCount = owners.Count,
            FounderIdentityStatus = "Founder access requires the configured Microsoft identity.",
            AssignablePages = all.Where(p => !p.FounderOnly && Capability(p.Key) is not null).ToList(), FounderOnlyPages = all.Where(p => p.FounderOnly).ToList(),
            RoleOptions = [new() {Key="manager",Label="Manager",Description="Store operations and reporting."}, new() {Key="support",Label="Support",Description="Catalog and order operations."}, new() {Key="analyst",Label="Analyst",Description="Reporting only."}],
            Members = members.Select(m => new ParfaitTeamMemberViewModel {
                Id=m.Id, Email=m.Email, DisplayName=!string.IsNullOrWhiteSpace(m.DisplayName)?m.DisplayName:!string.IsNullOrWhiteSpace(m.Email)?m.Email:"Team member", RoleKey=Protected(m)?m.RoleKey:Preset(m), RoleLabel=Protected(m)?m.RoleKey:Preset(m),
                RoleDescription=Protected(m)?"Protected canonical business ownership.":"Canonical capabilities with native page restrictions.",
                CanManageTeam=BusinessWorkspaceAccess.Allows(m,"team"), IsActive=m.Status=="Active", StatusLabel=m.Status,
                IdentityStatusLabel=m.ClientProfileId.HasValue?"Linked Microsoft account; membership "+m.Status.ToLowerInvariant()+".":"No linked identity.",
                HasLinkedIdentity=m.ClientProfileId.HasValue, CreatedUtc=m.CreatedUtc, ExpectedUpdatedTicks=m.UpdatedUtc.Ticks, IsProtected=Protected(m),
                AllowedPageKeys=all.Where(p=>Allows(m,p)).Select(p=>p.Key).ToList(), AllowedPageTitles=all.Where(p=>Allows(m,p)).Select(p=>p.Title).ToList()
            }).ToList()
        };
    }
    public async Task AddMemberAsync(ParfaitTeamCreateMemberInput input, string inviteUrl, string invitedBy, CancellationToken ct = default)
    {
        var scope = await AdminScopeAsync(ct);
        var name=(input.DisplayName??"").Trim();
        if (string.IsNullOrEmpty(name)) throw new InvalidOperationException("Enter the member's name.");
        var split=name.Split(' ',2);
        IClientEntraLifecycleService identityService;
        try { identityService = identities.Value; }
        catch (InvalidOperationException) { throw new InvalidOperationException("Microsoft account invitations are not configured. Contact the administrator."); }
        var invitation=await workspace.InviteMemberAsync(stores,scope,Actor(User),split[0],split.Length>1?split[1]:"",input.Email,identityService,ct,Permissions(input.RoleKey,null));
        if (!Uri.TryCreate(configuration["AgentPortalBaseUrl"]??"https://portal.mylegnd.com",UriKind.Absolute,out var portal) || portal.Scheme!="https" || !string.IsNullOrEmpty(portal.UserInfo))
            throw new InvalidOperationException("Invitation created, but the central sign-in origin is invalid.");
        var link=Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(portal.GetLeftPart(UriPartial.Authority)+"/Account/BusinessInvitation","token",invitation.Token);
        try { await mail.SendParfaitTeamInviteAsync(input.Email,name,input.RoleKey,[],link,invitedBy,ct,invitation.IdentityRedemptionUrl); }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw new InvalidOperationException("Invitation created, but email delivery failed. The account remains pending; retry its invitation."); }
    }
    public async Task UpdateMemberAsync(Guid id, ParfaitTeamUpdateMemberInput input, CancellationToken ct = default)
    {
        var scope=await AdminScopeAsync(ct);
        if (!await workspace.SaveMemberPermissionsAsync(stores,scope,Actor(User),id,input.ExpectedUpdatedTicks,
            Permissions(input.RoleKey,input.AllowedPageKeys,input.DisplayName,input.IsActive),ct)) throw new InvalidOperationException("Team access changed. Reload before saving.");
    }
    public async Task RemoveMemberAsync(Guid id, CancellationToken ct = default)
    {
        var scope=await AdminScopeAsync(ct);
        var ticks=long.TryParse(http.HttpContext?.Request.Form["ExpectedUpdatedTicks"].ToString(),out var value)?value:0;
        if (!await workspace.RemoveMemberAsync(stores,scope,Actor(User),id,ticks,ct)) throw new InvalidOperationException("Team access changed. Reload before removing.");
    }
}
