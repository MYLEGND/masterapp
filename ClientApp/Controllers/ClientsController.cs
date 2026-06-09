using System;
using ClientApp.Models;
using ClientApp.Services;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClientApp.Controllers
{
    [Authorize]
    public class ClientsController : Controller
    {
        private readonly MasterAppDbContext _db;
        private readonly EffectiveClientContextService _clientContext;
        private readonly IAzureUserUpdater _azureUserUpdater;

        public ClientsController(
            MasterAppDbContext db,
            EffectiveClientContextService clientContext,
            IAzureUserUpdater azureUserUpdater)
        {
            _db = db;
            _clientContext = clientContext;
            _azureUserUpdater = azureUserUpdater;
        }

        private static string Norm(string? v) => (v ?? "").Trim().ToLowerInvariant();
        private static string? NormalizeEmail(string? email)
        {
            var v = (email ?? "").Trim().ToLowerInvariant();
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }

        private static bool NeedsSO(string? maritalStatus)
        {
            if (string.IsNullOrWhiteSpace(maritalStatus)) return false;

            return maritalStatus.Equals("Married", StringComparison.OrdinalIgnoreCase)
                || maritalStatus.Equals("Domestic Partnership", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasChildData(HouseholdChildViewModel? child)
        {
            if (child == null) return false;

            return !string.IsNullOrWhiteSpace(child.FirstName)
                || !string.IsNullOrWhiteSpace(child.LastName)
                || child.DOB.HasValue
                || !string.IsNullOrWhiteSpace(child.Email)
                || !string.IsNullOrWhiteSpace(child.Phone);
        }

        private async Task<List<HouseholdChildViewModel>> LoadChildrenAsync(string? clientUserId)
        {
            var clientUserIdNorm = Norm(clientUserId);
            if (string.IsNullOrWhiteSpace(clientUserIdNorm))
                return new List<HouseholdChildViewModel>();

            return await _db.HouseholdMembers
                .AsNoTracking()
                .Where(x => x.ClientUserId == clientUserIdNorm && x.RelationshipType == "Child")
                .OrderBy(x => x.CreatedUtc)
                .ThenBy(x => x.FirstName)
                .ThenBy(x => x.LastName)
                .Select(x => new HouseholdChildViewModel
                {
                    Id = x.Id,
                    FirstName = x.FirstName,
                    LastName = x.LastName,
                    DOB = x.DOB,
                    Email = x.Email,
                    Phone = x.Phone
                })
                .ToListAsync();
        }

        private async Task SaveChildrenAsync(string clientUserId, IEnumerable<HouseholdChildViewModel>? children)
        {
            var clientUserIdNorm = Norm(clientUserId);
            var existing = await _db.HouseholdMembers
                .Where(x => x.ClientUserId == clientUserIdNorm && x.RelationshipType == "Child")
                .ToListAsync();

            if (existing.Count > 0)
                _db.HouseholdMembers.RemoveRange(existing);

            var now = DateTime.UtcNow;
            var newChildren = (children ?? Enumerable.Empty<HouseholdChildViewModel>())
                .Where(HasChildData)
                .Select(child => new HouseholdMember
                {
                    ClientUserId = clientUserIdNorm,
                    RelationshipType = "Child",
                    FirstName = (child.FirstName ?? "").Trim(),
                    LastName = (child.LastName ?? "").Trim(),
                    DOB = child.DOB?.Date,
                    Email = string.IsNullOrWhiteSpace(child.Email) ? "" : child.Email.Trim().ToLowerInvariant(),
                    Phone = (child.Phone ?? "").Trim(),
                    CreatedUtc = now,
                    UpdatedUtc = now
                })
                .ToList();

            if (newChildren.Count > 0)
                _db.HouseholdMembers.AddRange(newChildren);
        }

        [HttpGet("/profile/edit")]
        public async Task<IActionResult> Edit()
        {
            var context = await _clientContext.ResolveAsync(User, Request.Cookies);
            if (context == null || context.IsAgentView) return Forbid();

            var profile = context.Profile;

            var so = await _db.HouseholdMembers
                .AsNoTracking()
                .Where(x =>
                    x.ClientUserId == profile.ClientUserId &&
                    (x.RelationshipType == "SignificantOther" || x.RelationshipType == "Spouse"))
                .OrderByDescending(x => x.UpdatedUtc)
                .ThenByDescending(x => x.CreatedUtc)
                .FirstOrDefaultAsync();

            var vm = new EditClientViewModel
            {
                ClientUserId = profile.ClientUserId ?? "",
                FirstName = profile.FirstName ?? "",
                LastName = profile.LastName ?? "",
                Email = profile.Email ?? "",
                Phone = profile.Phone ?? "",
                MaritalStatus = profile.MaritalStatus ?? "",
                DOB = profile.DOB,

                SignificantOtherFirstName = so?.FirstName ?? profile.SignificantOtherFirstName,
                SignificantOtherLastName  = so?.LastName ?? profile.SignificantOtherLastName,
                SignificantOtherDOB       = so?.DOB ?? profile.SignificantOtherDOB,
                SignificantOtherEmail     = so?.Email ?? profile.SignificantOtherEmail,
                SignificantOtherPhone     = so?.Phone ?? profile.SignificantOtherPhone,
                Children = await LoadChildrenAsync(profile.ClientUserId)
            };

            return View(vm);
        }

        [HttpPost("/profile/edit")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(EditClientViewModel model)
        {
            var context = await _clientContext.ResolveAsync(User, Request.Cookies);
            if (context == null || context.IsAgentView) return Forbid();

            if (!string.Equals(Norm(model.ClientUserId), context.ClientUserId, StringComparison.Ordinal))
                return Forbid();

            if (!ModelState.IsValid)
                return View(model);

            var profile = context.Profile;

            var emailNorm = NormalizeEmail(model.Email);
            if (string.IsNullOrWhiteSpace(emailNorm))
            {
                ModelState.AddModelError(nameof(EditClientViewModel.Email), "Email is required.");
                return View(model);
            }

            var emailCollision = await _db.ClientProfiles.AsNoTracking()
                .AnyAsync(x => x.NormalizedEmail == emailNorm && x.ClientUserId != context.ClientUserId);

            if (emailCollision)
            {
                ModelState.AddModelError(nameof(EditClientViewModel.Email),
                    "That email is already used by another client.");
                return View(model);
            }

            await using var tx = await _db.Database.BeginTransactionAsync();

            profile.FirstName = (model.FirstName ?? "").Trim();
            profile.LastName = (model.LastName ?? "").Trim();
            profile.Email = emailNorm ?? "";
            profile.NormalizedEmail = emailNorm;
            profile.Phone = (model.Phone ?? "").Trim();
            profile.MaritalStatus = (model.MaritalStatus ?? "").Trim();
            profile.UpdatedUtc = DateTime.UtcNow;

            if (NeedsSO(profile.MaritalStatus))
            {
                if (string.IsNullOrWhiteSpace(model.SignificantOtherFirstName))
                    ModelState.AddModelError(nameof(EditClientViewModel.SignificantOtherFirstName), "Required for this status.");
                if (string.IsNullOrWhiteSpace(model.SignificantOtherLastName))
                    ModelState.AddModelError(nameof(EditClientViewModel.SignificantOtherLastName), "Required for this status.");
                if (model.SignificantOtherDOB == null)
                    ModelState.AddModelError(nameof(EditClientViewModel.SignificantOtherDOB), "Required for this status.");

                if (!ModelState.IsValid)
                    return View(model);

                var spouseRows = await _db.HouseholdMembers
                    .Where(x =>
                        x.ClientUserId == context.ClientUserId &&
                        (x.RelationshipType == "SignificantOther" || x.RelationshipType == "Spouse"))
                    .OrderByDescending(x => x.UpdatedUtc)
                    .ThenByDescending(x => x.CreatedUtc)
                    .ToListAsync();

                var so = spouseRows.FirstOrDefault();

                if (so == null)
                {
                    so = new HouseholdMember
                    {
                        ClientUserId = context.ClientUserId,
                        RelationshipType = "SignificantOther",
                        CreatedUtc = DateTime.UtcNow
                    };

                    _db.HouseholdMembers.Add(so);
                }
                else if (spouseRows.Count > 1)
                {
                    _db.HouseholdMembers.RemoveRange(spouseRows.Skip(1));
                }

                so.RelationshipType = "SignificantOther";
                so.FirstName = (model.SignificantOtherFirstName ?? "").Trim();
                so.LastName = (model.SignificantOtherLastName ?? "").Trim();
                so.DOB = model.SignificantOtherDOB;
                so.Email = string.IsNullOrWhiteSpace(model.SignificantOtherEmail)
                    ? ""
                    : model.SignificantOtherEmail.Trim().ToLowerInvariant();
                so.Phone = string.IsNullOrWhiteSpace(model.SignificantOtherPhone)
                    ? ""
                    : model.SignificantOtherPhone.Trim();
                so.UpdatedUtc = DateTime.UtcNow;

                // mirror onto ClientProfile too
                profile.SignificantOtherFirstName = so.FirstName;
                profile.SignificantOtherLastName = so.LastName;
                profile.SignificantOtherDOB = so.DOB;
                profile.SignificantOtherEmail = string.IsNullOrWhiteSpace(so.Email) ? null : so.Email;
                profile.SignificantOtherPhone = string.IsNullOrWhiteSpace(so.Phone) ? null : so.Phone;
            }
            else
            {
                profile.SignificantOtherFirstName = null;
                profile.SignificantOtherLastName = null;
                profile.SignificantOtherDOB = null;
                profile.SignificantOtherEmail = null;
                profile.SignificantOtherPhone = null;

                var spouseRows = await _db.HouseholdMembers
                    .Where(x =>
                        x.ClientUserId == context.ClientUserId &&
                        (x.RelationshipType == "SignificantOther" || x.RelationshipType == "Spouse"))
                    .ToListAsync();

                if (spouseRows.Count > 0)
                    _db.HouseholdMembers.RemoveRange(spouseRows);
            }

            await SaveChildrenAsync(context.ClientUserId, model.Children);

            await _db.SaveChangesAsync();

            // Attempt to push the new email to the Azure tenant (UPN/sign-in) so login stays in sync.
            var updateResult = await _azureUserUpdater.UpdateEmailAsync(context.ClientUserId, profile.Email, HttpContext.RequestAborted);

            if (!updateResult.Success && !updateResult.Skipped)
            {
                await tx.RollbackAsync();
                ModelState.AddModelError("",
                    string.IsNullOrWhiteSpace(updateResult.Message)
                        ? "We couldn't update your sign-in email in Azure. No changes were saved. Please try again."
                        : updateResult.Message);
                return View(model);
            }

            await tx.CommitAsync();

            if (updateResult.Skipped)
                TempData["ProfileSavedWarning"] = updateResult.Message
                    ?? "Profile saved locally, but the Azure sign-in email did not need to change.";

            return Redirect("/profile");
        }
    }
}
