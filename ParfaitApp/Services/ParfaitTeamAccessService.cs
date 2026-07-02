using System.Security.Claims;
using System.Text.Json;
using ParfaitApp.Models;
using ParfaitApp.Security;

namespace ParfaitApp.Services;

public interface IParfaitTeamAccessService
{
    Task<ParfaitTeamSignInResult> AuthorizeSignInAsync(ClaimsPrincipal user, CancellationToken ct = default);
    Task<bool> ValidatePrincipalAsync(ClaimsPrincipal user, CancellationToken ct = default);
    Task<ParfaitPageAccessResult> AuthorizePageAsync(ClaimsPrincipal user, string path, string? pageKey = null, CancellationToken ct = default);
    Task<IReadOnlyList<ParfaitInternalPageDefinition>> GetVisiblePagesAsync(ClaimsPrincipal user, CancellationToken ct = default);
    Task<ParfaitInternalPageDefinition?> GetFirstVisiblePageAsync(ClaimsPrincipal user, CancellationToken ct = default);
    Task<ParfaitTeamManagementViewModel> GetTeamManagementViewModelAsync(CancellationToken ct = default);
    Task AddMemberAsync(ParfaitTeamCreateMemberInput input, string inviteUrl, string invitedBy, CancellationToken ct = default);
    Task UpdateMemberAsync(Guid id, ParfaitTeamUpdateMemberInput input, CancellationToken ct = default);
    Task RemoveMemberAsync(Guid id, CancellationToken ct = default);
}

public sealed class ParfaitTeamAccessService : IParfaitTeamAccessService
{
    private const string TeamPageKey = "/internal/settings/team";
    private static readonly object FileLock = new();

    private readonly IWebHostEnvironment _environment;
    private readonly IParfaitInternalPageRegistry _pages;
    private readonly IGraphMailService _graphMail;

    public ParfaitTeamAccessService(
        IWebHostEnvironment environment,
        IParfaitInternalPageRegistry pages,
        IGraphMailService graphMail)
    {
        _environment = environment;
        _pages = pages;
        _graphMail = graphMail;
    }

    private string DataPath => Path.Combine(_environment.ContentRootPath, "App_Data", "parfait-team-access.json");

    public Task<ParfaitTeamSignInResult> AuthorizeSignInAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        var email = GetEmail(user);
        if (!IsCompanyEmail(email))
        {
            return Task.FromResult(new ParfaitTeamSignInResult
            {
                Message = "Only founder-approved @mylegnd.com accounts can access Parfait internal."
            });
        }

        if (ParfaitFounderGuard.IsFounder(user))
        {
            return Task.FromResult(new ParfaitTeamSignInResult
            {
                Allowed = true,
                IsFounder = true,
                Message = "Founder access granted."
            });
        }

        var store = LoadStore();
        var member = store.Members.FirstOrDefault(item =>
            item.Email.Equals(email, StringComparison.OrdinalIgnoreCase));

        if (member is null || !member.IsActive)
        {
            return Task.FromResult(new ParfaitTeamSignInResult
            {
                Message = "This @mylegnd.com account has not been added to the Parfait internal team."
            });
        }

        member.RoleKey = NormalizeRoleKey(member.RoleKey);
        member.AllowedPageKeys = EnsureMemberAllowedPages(member, _pages.GetAllPages());
        member.ObjectId = CleanOptional(GetObjectId(user)) ?? member.ObjectId;
        member.DisplayName = CleanRequired(CleanOptional(GetDisplayName(user)) ?? member.DisplayName, member.Email);
        member.LastSignInUtc = DateTime.UtcNow;
        member.UpdatedUtc = DateTime.UtcNow;
        SaveStore(store);

        return Task.FromResult(new ParfaitTeamSignInResult
        {
            Allowed = true,
            Message = "Team access granted."
        });
    }

    public Task<bool> ValidatePrincipalAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        if (user?.Identity?.IsAuthenticated != true)
            return Task.FromResult(false);

        if (ParfaitFounderGuard.IsFounder(user))
            return Task.FromResult(true);

        var email = GetEmail(user);
        if (!IsCompanyEmail(email))
            return Task.FromResult(false);

        var store = LoadStore();
        var member = store.Members.FirstOrDefault(item =>
            item.Email.Equals(email, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(member is not null && member.IsActive);
    }

    public async Task<ParfaitPageAccessResult> AuthorizePageAsync(ClaimsPrincipal user, string path, string? pageKey = null, CancellationToken ct = default)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return new ParfaitPageAccessResult
            {
                Message = "Sign in is required."
            };
        }

        var page = !string.IsNullOrWhiteSpace(pageKey)
            ? _pages.ResolveByKey(pageKey)
            : _pages.ResolveByPath(path);

        if (ParfaitFounderGuard.IsFounder(user))
        {
            return new ParfaitPageAccessResult
            {
                Allowed = true,
                IsFounder = true,
                Page = page,
                Message = "Founder access granted."
            };
        }

        if (!await ValidatePrincipalAsync(user, ct))
        {
            return new ParfaitPageAccessResult
            {
                Page = page,
                Message = "This account is not active for Parfait internal."
            };
        }

        if (page is null)
        {
            return new ParfaitPageAccessResult
            {
                Message = "This internal route is not available for team accounts."
            };
        }

        if (page.FounderOnly)
        {
            return new ParfaitPageAccessResult
            {
                Page = page,
                Message = "This page is reserved for the founder."
            };
        }

        var member = FindMember(user);
        if (member is null)
        {
            return new ParfaitPageAccessResult
            {
                Page = page,
                Message = "Team member record not found."
            };
        }

        var roleKey = NormalizeRoleKey(member.RoleKey);
        if (IsTeamManagementPage(page.Key) && !RoleCanManageTeam(roleKey))
        {
            return new ParfaitPageAccessResult
            {
                Page = page,
                Message = "Only Full Control admins can manage the Team console."
            };
        }

        var allowedPageKeys = EnsureMemberAllowedPages(member, _pages.GetAllPages());
        var allowed = allowedPageKeys.Any(key =>
            key.Equals(page.Key, StringComparison.OrdinalIgnoreCase));

        return new ParfaitPageAccessResult
        {
            Allowed = allowed,
            Page = page,
            Message = allowed
                ? "Page access granted."
                : $"Access to {page.Title} has not been granted for this team member."
        };
    }

    public Task<IReadOnlyList<ParfaitInternalPageDefinition>> GetVisiblePagesAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        var pages = _pages.GetAllPages()
            .Where(page => page.ShowInNavigation)
            .ToList();

        if (ParfaitFounderGuard.IsFounder(user))
            return Task.FromResult<IReadOnlyList<ParfaitInternalPageDefinition>>(pages);

        var member = FindMember(user);
        if (member is null || !member.IsActive)
            return Task.FromResult<IReadOnlyList<ParfaitInternalPageDefinition>>([]);

        var roleKey = NormalizeRoleKey(member.RoleKey);
        var allowedPageKeys = EnsureMemberAllowedPages(member, pages);
        var visible = pages
            .Where(page =>
                !page.FounderOnly &&
                allowedPageKeys.Any(key => key.Equals(page.Key, StringComparison.OrdinalIgnoreCase)) &&
                (!IsTeamManagementPage(page.Key) || RoleCanManageTeam(roleKey)))
            .ToList();

        return Task.FromResult<IReadOnlyList<ParfaitInternalPageDefinition>>(visible);
    }

    public async Task<ParfaitInternalPageDefinition?> GetFirstVisiblePageAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        var visible = await GetVisiblePagesAsync(user, ct);
        return visible.FirstOrDefault();
    }

    public Task<ParfaitTeamManagementViewModel> GetTeamManagementViewModelAsync(CancellationToken ct = default)
    {
        var pages = _pages.GetAllPages();
        var assignablePages = pages
            .Where(page => !page.FounderOnly)
            .ToList();
        var roleOptions = BuildRoleDefinitions(assignablePages);
        var rolesByKey = roleOptions.ToDictionary(role => role.Key, StringComparer.OrdinalIgnoreCase);

        var founderOnlyPages = pages
            .Where(page => page.FounderOnly)
            .ToList();

        var store = LoadStore();
        var members = store.Members
            .OrderByDescending(member => member.IsActive)
            .ThenBy(member => member.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(member =>
            {
                var roleKey = NormalizeRoleKey(member.RoleKey);
                var role = rolesByKey[roleKey];
                var allowedPageKeys = EnsureMemberAllowedPages(member, assignablePages);
                var allowedPageTitles = ResolveAllowedPageTitles(allowedPageKeys, assignablePages);

                return new ParfaitTeamMemberViewModel
                {
                    Id = member.Id,
                    Email = member.Email,
                    DisplayName = member.DisplayName,
                    RoleKey = role.Key,
                    RoleLabel = role.Label,
                    RoleDescription = role.Description,
                    CanManageTeam = role.CanManageTeam,
                    IsActive = member.IsActive,
                    StatusLabel = member.IsActive ? "Active" : "Disabled",
                    IdentityStatusLabel = BuildIdentityStatusLabel(member),
                    HasLinkedIdentity = !string.IsNullOrWhiteSpace(member.ObjectId),
                    CreatedUtc = member.CreatedUtc,
                    InviteSentUtc = member.InviteSentUtc,
                    LastSignInUtc = member.LastSignInUtc,
                    AllowedPageKeys = allowedPageKeys,
                    AllowedPageTitles = allowedPageTitles
                };
            })
            .ToList();

        return Task.FromResult(new ParfaitTeamManagementViewModel
        {
            FounderEmail = ParfaitFounderGuard.OwnerEmailSummary(),
            FounderIdentityStatus = BuildFounderIdentityStatus(),
            FounderCount = ParfaitFounderGuard.OwnerEmails.Count,
            AssignablePages = assignablePages,
            FounderOnlyPages = founderOnlyPages,
            RoleOptions = roleOptions,
            Members = members
        });
    }

    public async Task AddMemberAsync(ParfaitTeamCreateMemberInput input, string inviteUrl, string invitedBy, CancellationToken ct = default)
    {
        var email = NormalizeEmail(input.Email);
        if (!IsCompanyEmail(email))
            throw new InvalidOperationException("Team access is limited to @mylegnd.com accounts.");

        if (ParfaitFounderGuard.IsOwnerEmail(email))
            throw new InvalidOperationException("That owner account already has full access.");

        var roleKey = NormalizeRoleKey(input.RoleKey);
        var pages = _pages.GetAllPages();
        var roleOptions = BuildRoleDefinitions(pages);
        var role = roleOptions.First(option => option.Key.Equals(roleKey, StringComparison.OrdinalIgnoreCase));
        var allowedPageKeys = ResolveAllowedPageKeysForRole(roleKey, pages);
        var allowedPageTitles = ResolveAllowedPageTitles(allowedPageKeys, pages);

        var store = LoadStore();
        var existing = store.Members.FirstOrDefault(member =>
            member.Email.Equals(email, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
            throw new InvalidOperationException("That team member already exists.");

        var member = new TeamMemberRecord
        {
            Id = Guid.NewGuid(),
            Email = email,
            DisplayName = CleanRequired(input.DisplayName, email),
            RoleKey = roleKey,
            IsActive = true,
            AllowedPageKeys = allowedPageKeys,
            InviteSentUtc = DateTime.UtcNow,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        store.Members.Add(member);
        SaveStore(store);

        try
        {
            await _graphMail.SendParfaitTeamInviteAsync(
                email,
                member.DisplayName,
                role.Label,
                allowedPageTitles,
                inviteUrl,
                invitedBy,
                ct);
        }
        catch (Exception ex)
        {
            var rollbackStore = LoadStore();
            rollbackStore.Members.RemoveAll(item => item.Id == member.Id);
            SaveStore(rollbackStore);
            throw new InvalidOperationException($"Team member invite could not be sent. {ex.Message}");
        }
    }

    public Task UpdateMemberAsync(Guid id, ParfaitTeamUpdateMemberInput input, CancellationToken ct = default)
    {
        var store = LoadStore();
        var member = store.Members.FirstOrDefault(item => item.Id == id);
        if (member is null)
            throw new InvalidOperationException("Team member not found.");

        var pages = _pages.GetAllPages();
        var roleKey = NormalizeRoleKey(input.RoleKey);
        var allowedKeys = roleKey.Equals(ParfaitTeamRoles.FullControl, StringComparison.OrdinalIgnoreCase)
            ? ResolveAllowedPageKeysForRole(roleKey, pages)
            : FilterAllowedPageKeys(input.AllowedPageKeys, roleKey, pages);

        if (allowedKeys.Count == 0)
            allowedKeys = ResolveAllowedPageKeysForRole(roleKey, pages);

        member.DisplayName = CleanRequired(input.DisplayName, member.Email);
        member.RoleKey = roleKey;
        member.IsActive = input.IsActive;
        member.AllowedPageKeys = allowedKeys;
        member.UpdatedUtc = DateTime.UtcNow;

        SaveStore(store);
        return Task.CompletedTask;
    }

    public Task RemoveMemberAsync(Guid id, CancellationToken ct = default)
    {
        var store = LoadStore();
        var removed = store.Members.RemoveAll(member => member.Id == id);
        if (removed == 0)
            throw new InvalidOperationException("Team member not found.");

        SaveStore(store);
        return Task.CompletedTask;
    }

    private TeamMemberRecord? FindMember(ClaimsPrincipal user)
    {
        var email = GetEmail(user);
        if (string.IsNullOrWhiteSpace(email))
            return null;

        return LoadStore().Members.FirstOrDefault(member =>
            member.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
    }

    private List<ParfaitTeamRoleDefinition> BuildRoleDefinitions(IReadOnlyList<ParfaitInternalPageDefinition> allPages)
    {
        return
        [
            new ParfaitTeamRoleDefinition
            {
                Key = ParfaitTeamRoles.FullControl,
                Label = "Full Control",
                Description = "All internal pages, team access, and permission management.",
                CanManageTeam = true,
                DefaultPageKeys = ResolveAllowedPageKeysForRole(ParfaitTeamRoles.FullControl, allPages)
            },
            new ParfaitTeamRoleDefinition
            {
                Key = ParfaitTeamRoles.Manager,
                Label = "Manager",
                Description = "Operations, growth, business profile, and reporting without team governance.",
                DefaultPageKeys = ResolveAllowedPageKeysForRole(ParfaitTeamRoles.Manager, allPages)
            },
            new ParfaitTeamRoleDefinition
            {
                Key = ParfaitTeamRoles.Support,
                Label = "Support",
                Description = "Daily customer, order, and storefront support without admin controls.",
                DefaultPageKeys = ResolveAllowedPageKeysForRole(ParfaitTeamRoles.Support, allPages)
            },
            new ParfaitTeamRoleDefinition
            {
                Key = ParfaitTeamRoles.Analyst,
                Label = "Analyst",
                Description = "Growth and analytics visibility for reporting, content, and campaign reads.",
                DefaultPageKeys = ResolveAllowedPageKeysForRole(ParfaitTeamRoles.Analyst, allPages)
            }
        ];
    }

    private List<string> ResolveAllowedPageKeysForRole(string roleKey, IReadOnlyList<ParfaitInternalPageDefinition> allPages)
    {
        var assignablePages = allPages
            .Where(page => !page.FounderOnly)
            .ToList();

        IEnumerable<string> keys = NormalizeRoleKey(roleKey) switch
        {
            ParfaitTeamRoles.FullControl => assignablePages.Select(page => page.Key),
            ParfaitTeamRoles.Manager => assignablePages
                .Where(page => !IsTeamManagementPage(page.Key))
                .Select(page => page.Key),
            ParfaitTeamRoles.Support => ResolveExistingKeys(assignablePages,
                "/internal/dashboard",
                "/internal/commerce/products",
                "/internal/commerce/orders",
                "/internal/commerce/orders"),
            ParfaitTeamRoles.Analyst => ResolveExistingKeys(assignablePages,
                "/internal/dashboard",
                "/internal/analytics",
                "/internal/automations",
                "/internal/commerce/products",
                "/internal/analytics"),
            _ => ResolveExistingKeys(assignablePages, "/internal/dashboard")
        };

        return keys
            .Select(NormalizePathKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<string> FilterAllowedPageKeys(IEnumerable<string>? rawKeys, string roleKey, IReadOnlyList<ParfaitInternalPageDefinition> allPages)
    {
        if (NormalizeRoleKey(roleKey).Equals(ParfaitTeamRoles.FullControl, StringComparison.OrdinalIgnoreCase))
            return ResolveAllowedPageKeysForRole(roleKey, allPages);

        var assignable = allPages
            .Where(page => !page.FounderOnly)
            .ToDictionary(page => page.Key, page => page, StringComparer.OrdinalIgnoreCase);

        var allowedKeys = (rawKeys ?? [])
            .Select(NormalizePathKey)
            .Where(key => assignable.ContainsKey(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!RoleCanManageTeam(roleKey))
            allowedKeys.RemoveAll(IsTeamManagementPage);

        return allowedKeys
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<string> EnsureMemberAllowedPages(TeamMemberRecord member, IReadOnlyList<ParfaitInternalPageDefinition> allPages)
    {
        var roleKey = NormalizeRoleKey(member.RoleKey);
        var allowedPageKeys = FilterAllowedPageKeys(member.AllowedPageKeys, roleKey, allPages);
        return allowedPageKeys.Count == 0
            ? ResolveAllowedPageKeysForRole(roleKey, allPages)
            : allowedPageKeys;
    }

    private static List<string> ResolveAllowedPageTitles(IEnumerable<string> allowedPageKeys, IReadOnlyList<ParfaitInternalPageDefinition> allPages)
    {
        return allPages
            .Where(page => allowedPageKeys.Any(key => key.Equals(page.Key, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(page => page.GroupOrder)
            .ThenBy(page => page.Order)
            .Select(page => page.Title)
            .ToList();
    }

    private TeamStore LoadStore()
    {
        EnsureDataFile();

        lock (FileLock)
        {
            var json = File.ReadAllText(DataPath);
            return JsonSerializer.Deserialize<TeamStore>(json) ?? new TeamStore();
        }
    }

    private void SaveStore(TeamStore store)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DataPath)!);
        store.UpdatedUtc = DateTime.UtcNow;

        lock (FileLock)
        {
            File.WriteAllText(
                DataPath,
                JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private void EnsureDataFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DataPath)!);
        if (File.Exists(DataPath))
            return;

        SaveStore(new TeamStore());
    }

    private static IEnumerable<string> ResolveExistingKeys(
        IEnumerable<ParfaitInternalPageDefinition> allPages,
        params string[] candidates)
    {
        var pagesByKey = allPages.ToDictionary(page => page.Key, page => page, StringComparer.OrdinalIgnoreCase);
        return candidates
            .Select(NormalizePathKey)
            .Where(key => pagesByKey.ContainsKey(key));
    }

    private static string BuildFounderIdentityStatus()
    {
        return string.IsNullOrWhiteSpace(ParfaitFounderGuard.FounderOid)
            ? "Owner access is locked to the configured @mylegnd.com owner emails."
            : "Owner access is locked to the configured Microsoft identity and approved owner emails.";
    }

    private static string BuildIdentityStatusLabel(TeamMemberRecord member)
    {
        if (!string.IsNullOrWhiteSpace(member.ObjectId))
            return "Microsoft identity verified and linked.";

        if (member.InviteSentUtc.HasValue)
            return "Invite sent. Identity will lock after first Microsoft sign-in.";

        return "Awaiting invite delivery and first Microsoft sign-in.";
    }

    private static bool RoleCanManageTeam(string? roleKey)
    {
        return NormalizeRoleKey(roleKey).Equals(ParfaitTeamRoles.FullControl, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTeamManagementPage(string? pageKey)
    {
        return NormalizePathKey(pageKey).Equals(TeamPageKey, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetEmail(ClaimsPrincipal user)
    {
        return NormalizeEmail(
            user.FindFirstValue(ClaimTypes.Email) ??
            user.FindFirstValue("email") ??
            user.FindFirstValue("preferred_username") ??
            user.FindFirstValue("upn") ??
            user.Identity?.Name ??
            string.Empty);
    }

    private static string? GetObjectId(ClaimsPrincipal user)
    {
        return user.FindFirstValue("oid") ??
               user.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier");
    }

    private static string? GetDisplayName(ClaimsPrincipal user)
    {
        return user.FindFirstValue("name") ??
               user.FindFirstValue(ClaimTypes.Name) ??
               user.Identity?.Name;
    }

    private static bool IsCompanyEmail(string email)
    {
        return !string.IsNullOrWhiteSpace(email) &&
               email.EndsWith("@mylegnd.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeEmail(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static string NormalizePathKey(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        return normalized.StartsWith('/') ? normalized.TrimEnd('/') : "/" + normalized.TrimEnd('/');
    }

    private static string NormalizeRoleKey(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            ParfaitTeamRoles.FullControl => ParfaitTeamRoles.FullControl,
            ParfaitTeamRoles.Manager => ParfaitTeamRoles.Manager,
            ParfaitTeamRoles.Support => ParfaitTeamRoles.Support,
            ParfaitTeamRoles.Analyst => ParfaitTeamRoles.Analyst,
            _ => ParfaitTeamRoles.Support
        };
    }

    private static string CleanRequired(string? value, string fallback)
    {
        var cleaned = value?.Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }

    private static string? CleanOptional(string? value)
    {
        var cleaned = value?.Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private sealed class TeamStore
    {
        public List<TeamMemberRecord> Members { get; set; } = [];
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }

    private sealed class TeamMemberRecord
    {
        public Guid Id { get; set; }
        public string Email { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? ObjectId { get; set; }
        public string RoleKey { get; set; } = ParfaitTeamRoles.Support;
        public bool IsActive { get; set; } = true;
        public List<string> AllowedPageKeys { get; set; } = [];
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime? InviteSentUtc { get; set; }
        public DateTime? LastSignInUtc { get; set; }
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }
}
