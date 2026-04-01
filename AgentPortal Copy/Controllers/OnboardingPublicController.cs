using System.Security.Cryptography;
using System.Text;
using AgentPortal.Models;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;

namespace AgentPortal.Controllers;

[AllowAnonymous]
[EnableRateLimiting("anon-public")]
public class OnboardingPublicController : Controller
{
    private readonly MasterAppDbContext _db;
    private readonly ILogger<OnboardingPublicController> _logger;

    public OnboardingPublicController(MasterAppDbContext db, ILogger<OnboardingPublicController> logger)
    {
        _db = db;
        _logger = logger;
    }

    private static string HashToken(string token)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(token.Trim()));
        return Convert.ToBase64String(bytes);
    }

    private async Task<OnboardingInvite?> FindInviteAsync(string token, bool allowSubmitted = false)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var hash = HashToken(token);

        var invite = await _db.OnboardingInvites.FirstOrDefaultAsync(x => x.TokenHash == hash);
        if (invite == null) return null;

        if (string.Equals(invite.Status, "Revoked", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(invite.Status, "Archived", StringComparison.OrdinalIgnoreCase))
            return null;

        if (invite.ExpiresUtc.HasValue && invite.ExpiresUtc.Value < DateTime.UtcNow)
        {
            if (!string.Equals(invite.Status, "Expired", StringComparison.OrdinalIgnoreCase))
            {
                invite.Status = "Expired";
                await _db.SaveChangesAsync();
            }
            return null;
        }

        if (!allowSubmitted && string.Equals(invite.Status, "Submitted", StringComparison.OrdinalIgnoreCase))
            return null;

        return invite;
    }

    [HttpGet("/onboarding/start")]
    public async Task<IActionResult> Start(string token)
    {
        var invite = await FindInviteAsync(token, allowSubmitted: true);
        if (invite == null)
            return View("InvalidInvite");

        if (string.Equals(invite.Status, "Submitted", StringComparison.OrdinalIgnoreCase))
            return View("AlreadySubmitted");

        var vm = new OnboardingFormViewModel
        {
            Token = token,
            FirstName = invite.FirstName,
            LastName = invite.LastName,
            Email = invite.Email,
            RoleType = invite.RoleType,
            StartDate = DateTime.UtcNow.Date,
            EmploymentType = "Full-Time",
            PayType = "Salary",
            TaxFilingStatus = "Single"
        };

        ViewData["Invitee"] = $"{invite.FirstName} {invite.LastName}";
        return View(vm);
    }

    [HttpPost("/onboarding/start")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Start(OnboardingFormViewModel vm)
    {
        var invite = await FindInviteAsync(vm.Token);
        if (invite == null)
        {
            ModelState.AddModelError(string.Empty, "The onboarding link is invalid or has expired.");
        }

        if (!ModelState.IsValid)
        {
            return View(vm);
        }

        var submission = new OnboardingSubmission
        {
            InviteId = invite!.Id,
            CreatedUtc = DateTime.UtcNow,
            SubmittedUtc = DateTime.UtcNow,

            // Keep identity fields tied to the invitation so submissions always map
            // back to the invited person in the owner dashboard.
            FirstName = invite.FirstName,
            MiddleName = vm.MiddleName?.Trim(),
            LastName = invite.LastName,
            PreferredName = vm.PreferredName?.Trim(),
            DateOfBirth = vm.DateOfBirth,
            Phone = vm.Phone.Trim(),
            Email = invite.Email,
            CurrentAddress = vm.CurrentAddress.Trim(),
            City = vm.City.Trim(),
            State = vm.State.Trim(),
            Zip = vm.Zip.Trim(),
            MailingAddress = vm.MailingAddress?.Trim(),
            EmergencyContactName = vm.EmergencyContactName.Trim(),
            EmergencyContactPhone = vm.EmergencyContactPhone.Trim(),
            EmergencyContactRelationship = vm.EmergencyContactRelationship.Trim(),

            RoleType = invite.RoleType,
            JobTitle = vm.JobTitle.Trim(),
            Department = vm.Department?.Trim(),
            Manager = vm.Manager?.Trim(),
            StartDate = vm.StartDate,
            WorkState = vm.WorkState?.Trim(),
            WorkLocation = vm.WorkLocation?.Trim(),
            EmploymentType = vm.EmploymentType.Trim(),
            PayType = vm.PayType.Trim(),
            WorkNotes = vm.WorkNotes?.Trim(),

            LegalNameConfirmed = vm.LegalNameConfirmed,
            SsnLast4 = vm.SsnLast4?.Trim(),
            SsnNote = vm.SsnNote?.Trim(),
            DriverLicenseNumber = vm.DriverLicenseNumber?.Trim(),
            DriverLicenseState = vm.DriverLicenseState?.Trim(),
            WorkAuthorizationStatus = vm.WorkAuthorizationStatus.Trim(),
            CitizenshipStatus = vm.CitizenshipStatus?.Trim(),
            EligibilityDocumentsAck = vm.EligibilityDocumentsAck,

            TaxFilingStatus = vm.TaxFilingStatus.Trim(),
            FederalWithholding = vm.FederalWithholding?.Trim(),
            StateWithholding = vm.StateWithholding?.Trim(),
            BankName = vm.BankName.Trim(),
            BankAccountType = vm.BankAccountType.Trim(),
            BankRoutingNumber = vm.BankRoutingNumber.Trim(),
            BankAccountNumber = vm.BankAccountNumber.Trim(),
            PayrollAcknowledgement = vm.PayrollAcknowledgement,

            ConfidentialityAck = vm.ConfidentialityAck,
            HandbookAck = vm.HandbookAck,
            TechnologyAck = vm.TechnologyAck,
            ComplianceAck = vm.ComplianceAck,
            CompensationAck = vm.CompensationAck,
            NonSolicitAck = vm.NonSolicitAck,
            ElectronicSignatureAck = vm.ElectronicSignatureAck,
            ElectronicSignatureName = vm.ElectronicSignatureName.Trim(),
            ElectronicSignatureDate = vm.ElectronicSignatureDate,

            ResidentStateLicense = vm.ResidentStateLicense?.Trim(),
            NonResidentStates = vm.NonResidentStates?.Trim(),
            LicensesHeld = vm.LicensesHeld?.Trim(),
            LicenseNumbers = vm.LicenseNumbers?.Trim(),
            CarrierAppointments = vm.CarrierAppointments?.Trim(),
            EOCoverage = vm.EOCoverage?.Trim(),
            SupervisionNotes = vm.SupervisionNotes?.Trim(),

            HasRegulatoryIssues = vm.HasRegulatoryIssues,
            RegulatoryExplanation = vm.RegulatoryExplanation?.Trim(),
            HasCriminalHistory = vm.HasCriminalHistory,
            CriminalExplanation = vm.CriminalExplanation?.Trim(),
            HasAdministrativeActions = vm.HasAdministrativeActions,
            AdministrativeExplanation = vm.AdministrativeExplanation?.Trim(),
            HasPriorTermination = vm.HasPriorTermination,
            TerminationExplanation = vm.TerminationExplanation?.Trim(),
            HasOtherDisclosures = vm.HasOtherDisclosures,
            OtherDisclosuresExplanation = vm.OtherDisclosuresExplanation?.Trim(),

            HasIdDocument = vm.HasIdDocument,
            HasSsnDocument = vm.HasSsnDocument,
            HasVoidedCheck = vm.HasVoidedCheck,
            HasLicenseCopy = vm.HasLicenseCopy,
            HasCertifications = vm.HasCertifications,
            HasResume = vm.HasResume,
            HasSignedAgreements = vm.HasSignedAgreements,
            DocumentNotes = vm.DocumentNotes?.Trim(),

            CertificationTruthful = vm.CertificationTruthful
        };

        _db.OnboardingSubmissions.Add(submission);

        invite!.Status = "Submitted";
        invite.SubmittedUtc = submission.SubmittedUtc;

        await _db.SaveChangesAsync();

        TempData["OnboardingToken"] = vm.Token;
        return RedirectToAction(nameof(Submitted), new { id = submission.Id, token = vm.Token });
    }

    [HttpGet("/onboarding/submitted/{id:guid}")]
    public async Task<IActionResult> Submitted(Guid id, string? token)
    {
        // Require the original invite token to view the submitted confirmation
        var invite = await FindInviteAsync(token ?? string.Empty, allowSubmitted: true);
        if (invite == null)
            return View("InvalidInvite");

        var submission = await _db.OnboardingSubmissions
            .Include(x => x.Invite)
            .FirstOrDefaultAsync(x => x.Id == id && x.InviteId == invite.Id);

        if (submission == null) return View("InvalidInvite");

        return View(submission);
    }
}
