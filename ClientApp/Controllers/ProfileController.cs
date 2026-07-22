using ClientApp.Models;
using ClientApp.Services;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared.Auth;

namespace ClientApp.Controllers;

[Authorize]
public class ProfileController : Controller
{
    private readonly MasterAppDbContext _db;
    private readonly EffectiveClientContextService _clientContext;
    private readonly IAzureUserUpdater _azureUserUpdater;

    public ProfileController(
        MasterAppDbContext db,
        EffectiveClientContextService clientContext,
        IAzureUserUpdater azureUserUpdater)
    {
        _db = db;
        _clientContext = clientContext;
        _azureUserUpdater = azureUserUpdater;
    }

    private static string Norm(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    private static string? NormalizeEmail(string? email)
    {
        var value = Norm(email);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool NeedsSignificantOther(string? maritalStatus) =>
        string.Equals(maritalStatus?.Trim(), "Married", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(maritalStatus?.Trim(), "Domestic Partnership", StringComparison.OrdinalIgnoreCase);

    private static bool HasChildData(HouseholdChildViewModel? child) =>
        child != null &&
        (!string.IsNullOrWhiteSpace(child.FirstName) ||
         !string.IsNullOrWhiteSpace(child.LastName) ||
         child.DOB.HasValue ||
         !string.IsNullOrWhiteSpace(child.Email) ||
         !string.IsNullOrWhiteSpace(child.Phone));

    private async Task<HouseholdMember?> LoadSignificantOtherAsync(string clientId)
    {
        var clientIdNorm = Norm(clientId);

        var significantOther = await _db.HouseholdMembers
            .AsNoTracking()
            .Where(member => member.ClientUserId == clientIdNorm)
            .Where(member => member.RelationshipType == "SignificantOther" || member.RelationshipType == "Spouse")
            .OrderByDescending(member => member.UpdatedUtc)
            .ThenByDescending(member => member.CreatedUtc)
            .FirstOrDefaultAsync();

        if (significantOther != null)
            return significantOther;

        return await _db.HouseholdMembers
            .AsNoTracking()
            .Where(member => member.ClientUserId == clientIdNorm)
            .Where(member =>
                (member.RelationshipType ?? string.Empty).ToLower() == "significantother" ||
                (member.RelationshipType ?? string.Empty).ToLower() == "spouse")
            .OrderByDescending(member => member.UpdatedUtc)
            .ThenByDescending(member => member.CreatedUtc)
            .FirstOrDefaultAsync();
    }

    private async Task<List<HouseholdChildViewModel>> LoadChildrenAsync(string clientId)
    {
        var clientIdNorm = Norm(clientId);

        return await _db.HouseholdMembers
            .AsNoTracking()
            .Where(member => member.ClientUserId == clientIdNorm && member.RelationshipType == "Child")
            .OrderBy(member => member.CreatedUtc)
            .ThenBy(member => member.FirstName)
            .ThenBy(member => member.LastName)
            .Select(member => new HouseholdChildViewModel
            {
                Id = member.Id,
                FirstName = member.FirstName,
                LastName = member.LastName,
                DOB = member.DOB,
                Email = member.Email,
                Phone = member.Phone
            })
            .ToListAsync();
    }

    private async Task SaveChildrenAsync(string clientId, IEnumerable<HouseholdChildViewModel>? children)
    {
        var clientIdNorm = Norm(clientId);
        var existingChildren = await _db.HouseholdMembers
            .Where(member => member.ClientUserId == clientIdNorm && member.RelationshipType == "Child")
            .ToListAsync();

        if (existingChildren.Count > 0)
            _db.HouseholdMembers.RemoveRange(existingChildren);

        var now = DateTime.UtcNow;
        var newChildren = (children ?? Enumerable.Empty<HouseholdChildViewModel>())
            .Where(HasChildData)
            .Select(child => new HouseholdMember
            {
                ClientUserId = clientIdNorm,
                RelationshipType = "Child",
                FirstName = (child.FirstName ?? string.Empty).Trim(),
                LastName = (child.LastName ?? string.Empty).Trim(),
                DOB = child.DOB?.Date,
                Email = string.IsNullOrWhiteSpace(child.Email) ? string.Empty : child.Email.Trim().ToLowerInvariant(),
                Phone = (child.Phone ?? string.Empty).Trim(),
                CreatedUtc = now,
                UpdatedUtc = now
            })
            .ToList();

        if (newChildren.Count > 0)
            _db.HouseholdMembers.AddRange(newChildren);
    }

    private async Task<EditClientViewModel> BuildProfileViewModelAsync(ClientProfile profile)
    {
        var significantOther = await LoadSignificantOtherAsync(profile.ClientUserId);

        return new EditClientViewModel
        {
            ClientUserId = profile.ClientUserId ?? string.Empty,
            FirstName = profile.FirstName ?? string.Empty,
            LastName = profile.LastName ?? string.Empty,
            Email = profile.Email ?? string.Empty,
            Phone = profile.Phone ?? string.Empty,
            MaritalStatus = profile.MaritalStatus ?? string.Empty,
            DOB = profile.DOB,
            SignificantOtherFirstName = significantOther?.FirstName ?? profile.SignificantOtherFirstName,
            SignificantOtherLastName = significantOther?.LastName ?? profile.SignificantOtherLastName,
            SignificantOtherDOB = significantOther?.DOB ?? profile.SignificantOtherDOB,
            SignificantOtherEmail = significantOther?.Email ?? profile.SignificantOtherEmail,
            SignificantOtherPhone = significantOther?.Phone ?? profile.SignificantOtherPhone,
            Children = await LoadChildrenAsync(profile.ClientUserId ?? string.Empty)
        };
    }

    private ViewResult ClientProfileView(
        EditClientViewModel model,
        string? notice = null,
        string? warning = null)
    {
        ViewBag.ViewMode = "client";
        ViewBag.ViewingClientName = $"{model.FirstName} {model.LastName}".Trim();
        ViewBag.ProfileSaveNotice = notice;
        ViewBag.ProfileSaveWarning = warning;
        return View("Index", model);
    }

    private ViewResult AgentProfileView(EditClientViewModel model)
    {
        ViewBag.ViewMode = "agent";
        ViewBag.ViewingClientName = $"{model.FirstName} {model.LastName}".Trim();
        return View("Index", model);
    }

    [HttpGet("/profile")]
    public async Task<IActionResult> MyProfile()
    {
        var context = await _clientContext.ResolveAsync(User, Request.Cookies);
        if (context == null)
            return NotFound("No client profile found for this user.");

        var model = await BuildProfileViewModelAsync(context.Profile);
        return context.IsAgentView
            ? AgentProfileView(model)
            : ClientProfileView(model);
    }

    [HttpPost("/profile")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(EditClientViewModel model)
    {
        var context = await _clientContext.ResolveAsync(User, Request.Cookies);
        if (context == null || context.IsAgentView)
            return Forbid();

        if (!string.Equals(Norm(model.ClientUserId), context.ClientUserId, StringComparison.Ordinal))
            return Forbid();

        if (!ModelState.IsValid)
            return ClientProfileView(model);

        var email = NormalizeEmail(model.Email);
        if (string.IsNullOrWhiteSpace(email))
        {
            ModelState.AddModelError(nameof(EditClientViewModel.Email), "Email is required.");
            return ClientProfileView(model);
        }

        var emailInUse = await _db.ClientProfiles
            .AsNoTracking()
            .AnyAsync(profile => profile.NormalizedEmail == email && profile.ClientUserId != context.ClientUserId);

        if (emailInUse)
        {
            ModelState.AddModelError(nameof(EditClientViewModel.Email), "That email is already used by another client.");
            return ClientProfileView(model);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync();
        var profile = context.Profile;

        profile.FirstName = (model.FirstName ?? string.Empty).Trim();
        profile.LastName = (model.LastName ?? string.Empty).Trim();
        profile.Email = email;
        profile.NormalizedEmail = email;
        profile.Phone = (model.Phone ?? string.Empty).Trim();
        profile.MaritalStatus = (model.MaritalStatus ?? string.Empty).Trim();
        profile.UpdatedUtc = DateTime.UtcNow;

        if (NeedsSignificantOther(profile.MaritalStatus))
        {
            var significantOthers = await _db.HouseholdMembers
                .Where(member =>
                    member.ClientUserId == context.ClientUserId &&
                    (member.RelationshipType == "SignificantOther" || member.RelationshipType == "Spouse"))
                .OrderByDescending(member => member.UpdatedUtc)
                .ThenByDescending(member => member.CreatedUtc)
                .ToListAsync();

            var significantOther = significantOthers.FirstOrDefault();
            if (significantOther == null)
            {
                significantOther = new HouseholdMember
                {
                    ClientUserId = context.ClientUserId,
                    RelationshipType = "SignificantOther",
                    CreatedUtc = DateTime.UtcNow
                };
                _db.HouseholdMembers.Add(significantOther);
            }
            else if (significantOthers.Count > 1)
            {
                _db.HouseholdMembers.RemoveRange(significantOthers.Skip(1));
            }

            significantOther.RelationshipType = "SignificantOther";
            significantOther.FirstName = (model.SignificantOtherFirstName ?? string.Empty).Trim();
            significantOther.LastName = (model.SignificantOtherLastName ?? string.Empty).Trim();
            significantOther.DOB = model.SignificantOtherDOB?.Date;
            significantOther.Email = string.IsNullOrWhiteSpace(model.SignificantOtherEmail)
                ? string.Empty
                : model.SignificantOtherEmail.Trim().ToLowerInvariant();
            significantOther.Phone = (model.SignificantOtherPhone ?? string.Empty).Trim();
            significantOther.UpdatedUtc = DateTime.UtcNow;

            profile.SignificantOtherFirstName = significantOther.FirstName;
            profile.SignificantOtherLastName = significantOther.LastName;
            profile.SignificantOtherDOB = significantOther.DOB;
            profile.SignificantOtherEmail = string.IsNullOrWhiteSpace(significantOther.Email) ? null : significantOther.Email;
            profile.SignificantOtherPhone = string.IsNullOrWhiteSpace(significantOther.Phone) ? null : significantOther.Phone;
        }
        else
        {
            profile.SignificantOtherFirstName = null;
            profile.SignificantOtherLastName = null;
            profile.SignificantOtherDOB = null;
            profile.SignificantOtherEmail = null;
            profile.SignificantOtherPhone = null;

            var significantOthers = await _db.HouseholdMembers
                .Where(member =>
                    member.ClientUserId == context.ClientUserId &&
                    (member.RelationshipType == "SignificantOther" || member.RelationshipType == "Spouse"))
                .ToListAsync();

            if (significantOthers.Count > 0)
                _db.HouseholdMembers.RemoveRange(significantOthers);
        }

        await SaveChildrenAsync(context.ClientUserId, model.Children);
        await _db.SaveChangesAsync();

        var updateResult = await _azureUserUpdater.UpdateEmailAsync(
            context.ClientUserId,
            profile.Email,
            HttpContext.RequestAborted);

        if (!updateResult.Success && !updateResult.Skipped)
        {
            await transaction.RollbackAsync();
            ModelState.AddModelError(
                string.Empty,
                string.IsNullOrWhiteSpace(updateResult.Message)
                    ? "We couldn't update your sign-in email in Azure. No changes were saved. Please try again."
                    : updateResult.Message);
            return ClientProfileView(model);
        }

        await transaction.CommitAsync();

        model.DOB = profile.DOB;
        model.Email = profile.Email;
        model.FirstName = profile.FirstName;
        model.LastName = profile.LastName;
        model.Phone = profile.Phone;
        model.MaritalStatus = profile.MaritalStatus;

        return ClientProfileView(
            model,
            "Profile saved.",
            updateResult.Skipped
                ? updateResult.Message ?? "Profile saved locally; your sign-in email did not need to change."
                : null);
    }

    [HttpGet("/profile/{clientUserId}")]
    public async Task<IActionResult> ClientProfile(string clientUserId)
    {
        var canonicalAgentId = Norm(User.GetStableUserId());
        var clientId = Norm(clientUserId);

        if (string.IsNullOrWhiteSpace(canonicalAgentId) || string.IsNullOrWhiteSpace(clientId))
            return Forbid();

        if (string.Equals(canonicalAgentId, clientId, StringComparison.OrdinalIgnoreCase))
            return RedirectToAction(nameof(MyProfile));

        var clientExists = await _db.ClientProfiles
            .AsNoTracking()
            .AnyAsync(profile => profile.ClientUserId == clientId);

        if (!clientExists)
            return NotFound("Client profile not found.");

        var link = await _db.AgentClients
            .AsNoTracking()
            .FirstOrDefaultAsync(agentClient =>
                agentClient.AgentUserId == canonicalAgentId &&
                agentClient.ClientUserId == clientId);

        if (link == null)
        {
            var candidates = User.GetUserIdCandidates()
                .Select(Norm)
                .Distinct()
                .ToArray();

            link = await _db.AgentClients
                .AsNoTracking()
                .FirstOrDefaultAsync(agentClient =>
                    agentClient.ClientUserId == clientId &&
                    candidates.Contains(agentClient.AgentUserId));
        }

        if (link == null)
            return Forbid();

        var profile = await _db.ClientProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.ClientUserId == clientId);

        if (profile == null)
            return NotFound("Client profile not found.");

        return AgentProfileView(await BuildProfileViewModelAsync(profile));
    }
}
