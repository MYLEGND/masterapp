using System.Security.Claims;
using ParfaitApp.Models;

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
