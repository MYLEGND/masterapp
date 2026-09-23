using ClientApp.Infrastructure;
using ClientApp.Models;
using ClientApp.Services;
using Domain.Accounts;
using Domain.Billing;
using Domain.Entities;
using Domain.Enums;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Identity;
using Infrastructure.Businesses;
using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared.Auth;

namespace ClientApp.Controllers;

[Authorize]
[BypassClientSubscriptionRequirement]
public class ProfileController : Controller
{
    private readonly MasterAppDbContext _db;
    private readonly EffectiveClientContextService _clientContext;
    private readonly IClientEntraLifecycleService _entraLifecycle;
    private readonly IClientSubscriptionIdentitySyncService _subscriptionIdentitySync;
    private readonly IAccountLifecycleService _accountLifecycle;
    private readonly ClientIdentityContinuationService _continuations;
    private readonly ICommerceBusinessProvisioningService _businessProvisioning;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ProfileController>? _logger;

    public ProfileController(
        MasterAppDbContext db,
        EffectiveClientContextService clientContext,
        IClientEntraLifecycleService entraLifecycle,
        IClientSubscriptionIdentitySyncService subscriptionIdentitySync,
        IAccountLifecycleService accountLifecycle,
        ClientIdentityContinuationService continuations,
        ICommerceBusinessProvisioningService businessProvisioning,
        IConfiguration configuration,
        ILogger<ProfileController>? logger = null)
    {
        _db = db;
        _clientContext = clientContext;
        _entraLifecycle = entraLifecycle;
        _subscriptionIdentitySync = subscriptionIdentitySync;
        _accountLifecycle = accountLifecycle;
        _continuations = continuations;
        _businessProvisioning = businessProvisioning;
        _configuration = configuration;
        _logger = logger;
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

    private static bool IsBusinessClient(ClientProfile profile) =>
        string.Equals(
            ClientRecordClassification.Resolve(profile.ClientUserId, profile.CrmNotes),
            ClientRecordClassification.BusinessClient,
            StringComparison.Ordinal);

    private string LegendWebsiteBaseUrl() =>
        (_configuration["LegendWebsiteBaseUrl"] ?? "https://www.mylegnd.com").TrimEnd('/');

    private async Task<List<BusinessWebsiteProfileSummary>> LoadBusinessWebsitesAsync(
        ClientProfile profile,
        CancellationToken cancellationToken)
    {

        var businesses = await WebsiteBusinessAccess.QueryManagedBusinesses(_db, profile.Id)
            .OrderBy(business => business.DisplayName)
            .ToListAsync(cancellationToken);

        var businessIds = businesses.Select(business => business.Id).ToArray();
        var activeDomains = await _db.Set<WebsiteDomainBinding>()
            .AsNoTracking()
            .Where(binding =>
                businessIds.Contains(binding.CommerceBusinessId) &&
                binding.Status.ToLower() == "active" &&
                binding.CertificateStatus.ToLower() == "active")
            .OrderBy(binding => binding.CreatedUtc)
            .ToListAsync(cancellationToken);

        var primaryDomainByBusiness = activeDomains
            .GroupBy(binding => binding.CommerceBusinessId)
            .ToDictionary(group => group.Key, group => group.First().Hostname);

        var baseUrl = LegendWebsiteBaseUrl();
        return businesses.Select(business => new BusinessWebsiteProfileSummary(
            business.Id,
            business.DisplayName,
            $"{baseUrl}/business-preview/?businessId={business.Id:D}",
            primaryDomainByBusiness.TryGetValue(business.Id, out var hostname) ? hostname : null))
            .ToList();
    }

    private async Task<CommerceBusiness?> AuthorizedBusinessAsync(
        EffectiveClientContext context,
        Guid businessId,
        CancellationToken cancellationToken)
    {

        if (!await WebsiteBusinessAccess.CanManageAsActorAsync(
                _db,
                businessId,
                context.Profile.Id,
                User.GetCanonicalUserId(),
                context.IsAgentView
                    ? context.AgentEmail
                    : context.Profile.NormalizedEmail ?? context.Profile.Email,
                cancellationToken))
            return null;

        return await _db.CommerceBusinesses
            .AsNoTracking()
            .SingleOrDefaultAsync(
                business => business.Id == businessId &&
                            business.IsActive &&
                            business.Status == "Active",
                cancellationToken);
    }

    private async Task<ViewResult> ProfileViewAsync(
        EditClientViewModel model,
        EffectiveClientContext context,
        string? notice = null,
        string? warning = null)
    {
        ViewBag.ViewMode = context.IsAgentView ? "agent" : "client";
        ViewBag.ViewingClientName = context.AccountDisplayName;
        ViewBag.ProfileSaveNotice = notice ?? TempData["BusinessWebsiteNotice"]?.ToString();
        ViewBag.ProfileSaveWarning = warning ?? TempData["BusinessWebsiteWarning"]?.ToString();
        ViewBag.IsBusinessClient = IsBusinessClient(context.Profile);
        ViewBag.BusinessWebsites = await LoadBusinessWebsitesAsync(
            context.Profile,
            HttpContext.RequestAborted);

        ViewBag.EditableBusinessIds = await _db.CommerceBusinessMembers.AsNoTracking()
            .Where(member => member.ClientProfileId == context.Profile.Id && member.Status == "Active" &&
                member.RoleKey == "owner" && member.CanManageTeam)
            .Select(member => member.CommerceBusinessId).ToArrayAsync(HttpContext.RequestAborted);

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

            var userId = User.GetCanonicalUserId();
            if (!string.IsNullOrWhiteSpace(userId))
            {
                var lifecycle = await _accountLifecycle.GetAsync(
                    new AccountLifecycleSubject(
                        userId,
                        MessagingParticipantTypes.Client,
                        context.ClientProfileId),
                    HttpContext.RequestAborted);
                ViewBag.AccountLifecycleState = lifecycle.State;
                ViewBag.CanResumeAccount = lifecycle.CanResume;
            }
        }

        return View("Index", model);
    }

    [HttpGet("/profile")]
    public async Task<IActionResult> MyProfile()
    {
        var context = await _clientContext.ResolveAsync(User, Request.Cookies);
        if (context == null)
            return NotFound("No profile found for this account.");

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
            ModelState.AddModelError(nameof(EditClientViewModel.Email), "That email is already used by another member.");
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
            // Ordinary profile edits must not depend on an external login mutation.
            if (AccountEmailChange.IsChanged(previousEmail, profile.NormalizedEmail))
                await _entraLifecycle.SynchronizeClientIdentityAsync(
                    profile.Id,
                    HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger?.LogError(ex, "Profile login-address synchronization failed. ClientProfileId={ClientProfileId} TraceId={TraceId}",
                profile.Id, HttpContext.TraceIdentifier);
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
        model.Email = profile.Email ?? string.Empty;
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

    [HttpPost("/profile/account-access")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AccountAccess(string operation, string? confirmation)
    {
        var context = await _clientContext.ResolveAsync(User, Request.Cookies, allowRelink: false);
        if (context is null || context.IsAgentView)
            return Forbid();

        var userId = User.GetCanonicalUserId();
        if (string.IsNullOrWhiteSpace(userId))
            return Forbid();

        var subject = new AccountLifecycleSubject(
            userId,
            MessagingParticipantTypes.Client,
            context.ClientProfileId);
        AccountLifecycleOperationResult result;
        if (string.Equals(operation, "pause", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(confirmation?.Trim(), "PAUSE", StringComparison.Ordinal))
            {
                var invalidModel = await BuildProfileViewModelAsync(context.Profile);
                return await ProfileViewAsync(
                    invalidModel,
                    context,
                    warning: "Type PAUSE to confirm that you want to pause your account.");
            }

            result = await _accountLifecycle.PauseAsync(subject, HttpContext.TraceIdentifier, HttpContext.RequestAborted);
        }
        else if (string.Equals(operation, "resume", StringComparison.OrdinalIgnoreCase))
        {
            result = await _accountLifecycle.ResumeAsync(subject, HttpContext.TraceIdentifier, HttpContext.RequestAborted);
        }
        else
        {
            return BadRequest();
        }

        var model = await BuildProfileViewModelAsync(context.Profile);
        return await ProfileViewAsync(
            model,
            context,
            result.Succeeded ? result.Message : null,
            result.Succeeded ? null : result.Message);
    }

    [HttpPost("/profile/business-website/setup")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetupBusinessWebsite(string businessName, string? legalName)
    {
        var context = await _clientContext.ResolveAsync(User, Request.Cookies, allowRelink: false);
        if (context is null || !IsBusinessClient(context.Profile))
            return Forbid();

        businessName = (businessName ?? string.Empty).Trim();
        legalName = string.IsNullOrWhiteSpace(legalName) ? businessName : legalName.Trim();
        if (businessName.Length is < 2 or > 160 || legalName.Length > 200)
        {
            TempData["BusinessWebsiteWarning"] = "Enter a valid business name before setting up the website.";
            return RedirectToAction(nameof(MyProfile));
        }

        var email = NormalizeEmail(context.Profile.NormalizedEmail ?? context.Profile.Email);
        if (string.IsNullOrWhiteSpace(email))
            return Forbid();

        var existing = await WebsiteBusinessAccess.QueryManagedBusinesses(_db, context.Profile.Id)
            .AnyAsync(HttpContext.RequestAborted);

        if (existing)
        {
            TempData["BusinessWebsiteNotice"] = "Your business website scope is already active.";
            return RedirectToAction(nameof(MyProfile));
        }

        await _businessProvisioning.CreateAsync(
            new CommerceBusinessProvisioningRequest(
                DisplayName: businessName,
                LegalName: legalName,
                BusinessType: "BusinessClient",
                OwnerEmail: email,
                OwnerDisplayName: $"{context.Profile.FirstName} {context.Profile.LastName}".Trim(),
                CanManageStorefront: true,
                CanManageCatalog: false,
                CanManageOrders: false,
                CanManageAnalytics: true,
                CanManageTeam: true,
                Storefront: new CommerceBusinessStorefrontProvisioning(
                    businessName,
                    $"{businessName} business website.",
                    "Draft"),
                OwnerClientProfileId: context.Profile.Id),
            HttpContext.RequestAborted);

        TempData["BusinessWebsiteNotice"] = "Business website scope created. You can now preview and edit it.";
        return RedirectToAction(nameof(MyProfile));
    }

    [HttpPost("/profile/business/{businessId:guid}/entity-name")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveBusinessEntityName(Guid businessId, string entityName)
    {
        var context = await _clientContext.ResolveAsync(User, Request.Cookies, allowRelink: false);
        if (context == null || await AuthorizedBusinessAsync(context, businessId, HttpContext.RequestAborted) == null)
            return Forbid();
        try
        {
            if (!ModelState.IsValid) throw new InvalidOperationException("Enter a valid entity name.");
            await _businessProvisioning.UpdateEntityNameAsync(businessId, context.Profile.Id, entityName,
                HttpContext.RequestAborted, context.IsAgentView ? User.GetCanonicalUserId() : null);
            TempData["BusinessWebsiteNotice"] = "Entity name saved. Publish your website to update its published pages.";
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (DbUpdateConcurrencyException) { TempData["BusinessWebsiteWarning"] = "Business details changed in another session. Reload and try again."; }
        catch (InvalidOperationException ex) { TempData["BusinessWebsiteWarning"] = ex.Message; }
        return RedirectToAction(nameof(MyProfile));
    }

    [HttpGet("/profile/business/{businessId:guid}/owners")]
    public async Task<IActionResult> BusinessOwners(Guid businessId)
    {
        var context = await _clientContext.ResolveAsync(User, Request.Cookies, allowRelink: false);
        if (context == null || await AuthorizedBusinessAsync(context, businessId, HttpContext.RequestAborted) is not { } business)
            return Forbid();
        if (!await _db.CommerceBusinessMembers.AnyAsync(x => x.CommerceBusinessId == businessId && x.ClientProfileId == context.Profile.Id && x.Status == "Active" && x.RoleKey == "owner" && x.CanManageTeam))
            return Forbid();
        ViewBag.BusinessId = businessId;
        ViewBag.EntityName = business.DisplayName;
        return View("BusinessOwners", await _db.CommerceBusinessMembers.AsNoTracking()
            .Where(x => x.CommerceBusinessId == businessId && x.Status == "Active" && x.RoleKey == "owner").ToListAsync());
    }

    [HttpPost("/profile/business/{businessId:guid}/owners")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveBusinessOwners(Guid businessId, string entityName, List<BusinessOwnerInput> owners)
    {
        var context = await _clientContext.ResolveAsync(User, Request.Cookies, allowRelink: false);
        if (context == null || await AuthorizedBusinessAsync(context, businessId, HttpContext.RequestAborted) == null)
            return Forbid();
        try
        {
            if (!ModelState.IsValid) throw new InvalidOperationException("Check owner account IDs, emails and percentages.");
            await _businessProvisioning.UpdateOwnershipAsync(businessId, context.Profile.Id, entityName, owners, HttpContext.RequestAborted, context.IsAgentView ? User.GetCanonicalUserId() : null);
            TempData["BusinessWebsiteNotice"] = "Entity name and linked owners saved.";
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (DbUpdateConcurrencyException) { TempData["BusinessWebsiteWarning"] = "Ownership changed in another session. Reload and try again."; }
        catch (InvalidOperationException ex) { TempData["BusinessWebsiteWarning"] = ex.Message; }
        return RedirectToAction(nameof(BusinessOwners), new { businessId });
    }

    [HttpGet("/profile/business-website/session/{businessId:guid}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> BusinessWebsiteSession(Guid businessId)
    {
        var context = await _clientContext.ResolveAsync(User, Request.Cookies, allowRelink: false);
        if (context is null) return Forbid();

        var business = await AuthorizedBusinessAsync(context, businessId, HttpContext.RequestAborted);
        if (business is null) return Forbid();

        var actorUserId = User.GetCanonicalUserId();
        var actorEmail = context.IsAgentView
            ? context.AgentEmail
            : context.Profile.NormalizedEmail ?? context.Profile.Email;

        var handoff = await _continuations.CreateWebsiteEditorHandoffAsync(
            context.Profile.Id,
            business.Id,
            actorUserId,
            actorEmail ?? string.Empty,
            HttpContext.RequestAborted);

        var apiBase = (_configuration["WebsiteContentApiBaseUrl"] ?? "https://protect.mylegnd.com").TrimEnd('/');
        return Json(new
        {
            handoffUrl = $"{apiBase}/api/website-content/handoff",
            state = handoff.OpaqueState,
            expiresUtc = handoff.ExpiresUtc
        });
    }

    [HttpGet("/profile/{clientUserId}")]
    public async Task<IActionResult> ClientProfile(string clientUserId)
    {
        var clientId = Norm(clientUserId);

        if (string.IsNullOrWhiteSpace(clientId))
            return NotFound("Profile not found.");

        var profile = await _db.ClientProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.ClientUserId == clientId);

        if (profile == null)
            return NotFound("Profile not found.");

        // All managed access goes through the same ownership-checked support context as AgentPortal.
        return LocalRedirect($"/support/view-as-client/{profile.Id}?returnUrl={Uri.EscapeDataString("/profile")}");
    }
}
