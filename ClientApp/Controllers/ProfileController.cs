using ClientApp.Models;
using ClientApp.Services;
using Domain.Billing;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClientApp.Controllers;

[Authorize]
public class ProfileController : Controller
{
    private readonly MasterAppDbContext _db;
    private readonly EffectiveClientContextService _clientContext;
    private readonly IClientEntraLifecycleService _entraLifecycle;
    private readonly IClientSubscriptionIdentitySyncService _subscriptionIdentitySync;

    public ProfileController(
        MasterAppDbContext db,
        EffectiveClientContextService clientContext,
        IClientEntraLifecycleService entraLifecycle,
        IClientSubscriptionIdentitySyncService subscriptionIdentitySync)
    {
        _db = db;
        _clientContext = clientContext;
        _entraLifecycle = entraLifecycle;
        _subscriptionIdentitySync = subscriptionIdentitySync;
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
            AccountManagementMode = ClientAccountManagementModes.Normalize(profile.AccountManagementMode),
            DOB = profile.DOB,
            SignificantOtherFirstName = profile.SignificantOtherFirstName ?? significantOther?.FirstName,
            SignificantOtherLastName = profile.SignificantOtherLastName ?? significantOther?.LastName,
            SignificantOtherDOB = profile.SignificantOtherDOB ?? significantOther?.DOB,
            SignificantOtherEmail = profile.SignificantOtherEmail ?? significantOther?.Email,
            SignificantOtherPhone = profile.SignificantOtherPhone ?? significantOther?.Phone,
            Children = await LoadChildrenAsync(profile.ClientUserId ?? string.Empty)
        };
    }

    private async Task<ViewResult> ProfileViewAsync(
        EditClientViewModel model,
        EffectiveClientContext context,
        string? notice = null,
        string? warning = null)
    {
        ViewBag.ViewMode = context.IsAgentView ? "agent" : "client";
        ViewBag.ViewingClientName = $"{model.FirstName} {model.LastName}".Trim();
        ViewBag.ProfileSaveNotice = notice;
        ViewBag.ProfileSaveWarning = warning;

        if (!context.IsAgentView)
        {
            var latestSubscription = await _db.ClientSubscriptions
                .AsNoTracking()
                .Where(subscription => subscription.ClientProfileId == context.ClientProfileId)
                .OrderByDescending(subscription => subscription.UpdatedUtc)
                .Select(subscription => new { subscription.Status })
                .FirstOrDefaultAsync(HttpContext.RequestAborted);

            ViewBag.HasClientSubscription = latestSubscription is not null;
            ViewBag.CanCancelSubscription = latestSubscription?.Status is
                ClientSubscriptionStatus.Active or ClientSubscriptionStatus.GracePeriod;
            ViewBag.SubscriptionNotice = TempData["SubscriptionNotice"]?.ToString();
        }

        return View("Index", model);
    }

    [HttpGet("/profile")]
    public async Task<IActionResult> MyProfile()
    {
        var context = await _clientContext.ResolveAsync(User, Request.Cookies);
        if (context == null)
            return NotFound("No client profile found for this user.");

        var model = await BuildProfileViewModelAsync(context.Profile);
        return await ProfileViewAsync(model, context);
    }

    [HttpPost("/profile")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(EditClientViewModel model)
    {
        var context = await _clientContext.ResolveAsync(User, Request.Cookies);
        if (context == null)
            return Forbid();

        if (!string.Equals(Norm(model.ClientUserId), context.ClientUserId, StringComparison.Ordinal))
            return Forbid();

        if (!ModelState.IsValid)
            return await ProfileViewAsync(model, context);

        if (!context.IsAgentView && !ClientAccountManagementModes.IsValid(model.AccountManagementMode))
        {
            ModelState.AddModelError(
                nameof(EditClientViewModel.AccountManagementMode),
                "Choose Shared Account or Self Managed.");
            return await ProfileViewAsync(model, context);
        }

        var email = NormalizeEmail(model.Email);
        if (string.IsNullOrWhiteSpace(email))
        {
            ModelState.AddModelError(nameof(EditClientViewModel.Email), "Email is required.");
            return await ProfileViewAsync(model, context);
        }

        var emailInUse = await _db.ClientProfiles
            .AsNoTracking()
            .AnyAsync(profile => profile.NormalizedEmail == email && profile.ClientUserId != context.ClientUserId);

        if (emailInUse)
        {
            ModelState.AddModelError(nameof(EditClientViewModel.Email), "That email is already used by another client.");
            return await ProfileViewAsync(model, context);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync();
        var profile = context.Profile;
        var previousEmail = profile.NormalizedEmail ?? profile.Email;

        profile.FirstName = (model.FirstName ?? string.Empty).Trim();
        profile.LastName = (model.LastName ?? string.Empty).Trim();
        profile.Email = email;
        profile.NormalizedEmail = email;
        profile.Phone = (model.Phone ?? string.Empty).Trim();
        profile.MaritalStatus = (model.MaritalStatus ?? string.Empty).Trim();
        if (!context.IsAgentView)
            profile.AccountManagementMode = ClientAccountManagementModes.Normalize(model.AccountManagementMode);
        profile.UpdatedUtc = DateTime.UtcNow;

        if (NeedsSignificantOther(profile.MaritalStatus))
        {
            // These fields are legacy relationship detail used only to seed a
            // separately reviewed partner invitation. They do not create,
            // activate, or mutate a household membership.
            profile.SignificantOtherFirstName = (model.SignificantOtherFirstName ?? string.Empty).Trim();
            profile.SignificantOtherLastName = (model.SignificantOtherLastName ?? string.Empty).Trim();
            profile.SignificantOtherDOB = model.SignificantOtherDOB?.Date;
            profile.SignificantOtherEmail = string.IsNullOrWhiteSpace(model.SignificantOtherEmail)
                ? null
                : model.SignificantOtherEmail.Trim().ToLowerInvariant();
            profile.SignificantOtherPhone = (model.SignificantOtherPhone ?? string.Empty).Trim();
        }
        else
        {
            profile.SignificantOtherFirstName = null;
            profile.SignificantOtherLastName = null;
            profile.SignificantOtherDOB = null;
            profile.SignificantOtherEmail = null;
            profile.SignificantOtherPhone = null;

        }

        await SaveChildrenAsync(context.ClientUserId, model.Children);
        await _db.SaveChangesAsync();

        try
        {
            await _entraLifecycle.SynchronizeClientIdentityAsync(
                profile.Id,
                HttpContext.RequestAborted);
        }
        catch
        {
            await transaction.RollbackAsync();
            ModelState.AddModelError(
                string.Empty,
                "We couldn't update your sign-in email. No changes were saved. Please try again.");
            return await ProfileViewAsync(model, context);
        }

        await _subscriptionIdentitySync.SynchronizeAfterEmailChangeAsync(
            profile.Id,
            previousEmail,
            profile.NormalizedEmail,
            HttpContext.RequestAborted);
        await _db.SaveChangesAsync(HttpContext.RequestAborted);

        await transaction.CommitAsync();

        model.DOB = profile.DOB;
        model.Email = profile.Email;
        model.FirstName = profile.FirstName;
        model.LastName = profile.LastName;
        model.Phone = profile.Phone;
        model.MaritalStatus = profile.MaritalStatus;
        model.AccountManagementMode = ClientAccountManagementModes.Normalize(profile.AccountManagementMode);

        return await ProfileViewAsync(
            model,
            context,
            "Profile saved.");
    }

    [HttpGet("/profile/{clientUserId}")]
    public async Task<IActionResult> ClientProfile(string clientUserId)
    {
        var clientId = Norm(clientUserId);

        if (string.IsNullOrWhiteSpace(clientId))
            return NotFound("Client profile not found.");

        var profile = await _db.ClientProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.ClientUserId == clientId);

        if (profile == null)
            return NotFound("Client profile not found.");

        // All managed access goes through the same ownership-checked support context as AgentPortal.
        return LocalRedirect($"/support/view-as-client/{profile.Id}?returnUrl={Uri.EscapeDataString("/profile")}");
    }
}
