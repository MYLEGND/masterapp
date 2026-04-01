using System.Security.Cryptography;
using System.Text;
using AgentPortal.Models;
using AgentPortal.Services;
using AgentPortal.Security;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace AgentPortal.Controllers;

[Authorize]
[OnboardingOwnerOnly]
public class OnboardingController : Controller
{
    private readonly MasterAppDbContext _db;
    private readonly IConfiguration _config;
    private readonly ClientProvisioningService _provisioning;
    private readonly ILogger<OnboardingController> _logger;
    private readonly IEmailSender _emailSender;

    private const int DefaultInviteLifespanDays = 14;

    public OnboardingController(MasterAppDbContext db, IConfiguration config, ClientProvisioningService provisioning, ILogger<OnboardingController> logger, IEmailSender emailSender)
    {
        _db = db;
        _config = config;
        _provisioning = provisioning;
        _logger = logger;
        _emailSender = emailSender;
    }

    private string BuildOnboardingLink(string token)
    {
        var baseUrl = (_config["Onboarding:PublicBaseUrl"]
            ?? _config["AgentPortal:BaseUrl"]
            ?? string.Empty).Trim().TrimEnd('/');

        if (!string.IsNullOrWhiteSpace(baseUrl) && Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
        {
            return $"{baseUrl}/onboarding/start?token={Uri.EscapeDataString(token)}";
        }

        var generated = Url.Action("Start", "OnboardingPublic", new { token }, Request.Scheme);
        if (!string.IsNullOrWhiteSpace(generated))
            return generated;

        return $"{Request.Scheme}://{Request.Host}/onboarding/start?token={Uri.EscapeDataString(token)}";
    }

    private static string HashToken(string token)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(token.Trim()));
        return Convert.ToBase64String(bytes);
    }

    private async Task<string> CreateUniqueTokenAsync()
    {
        for (var i = 0; i < 5; i++)
        {
            var buffer = RandomNumberGenerator.GetBytes(32);
            var token = Convert.ToBase64String(buffer)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');

            var hash = HashToken(token);
            var exists = await _db.OnboardingInvites.AnyAsync(x => x.TokenHash == hash);
            if (!exists) return token;
        }

        throw new InvalidOperationException("Unable to generate a unique onboarding token.");
    }

    private async Task<OnboardingDashboardViewModel> BuildDashboardAsync()
    {
        var invites = await _db.OnboardingInvites
            .Where(x => x.Status != "Archived" && !x.Submissions.Any())
            .OrderByDescending(x => x.CreatedUtc)
            .ToListAsync();

        var submissions = await _db.OnboardingSubmissions
            .Include(x => x.Invite)
            .OrderByDescending(x => x.SubmittedUtc ?? x.CreatedUtc)
            .ToListAsync();

        var vm = new OnboardingDashboardViewModel
        {
            Invites = invites,
            Submissions = submissions,
            NewInvite = new OnboardingInviteInputModel()
        };

        if (TempData["NewInviteLink"] is string link)
        {
            vm.NewInviteLink = link;
        }

        return vm;
    }

    public async Task<IActionResult> Index()
    {
        var vm = await BuildDashboardAsync();
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateInvite([Bind(Prefix = "NewInvite")] OnboardingInviteInputModel model)
    {
        if (!ModelState.IsValid)
        {
            var vm = await BuildDashboardAsync();
            vm.NewInvite = model;
            return View("Index", vm);
        }

        var token = await CreateUniqueTokenAsync();
        var hash = HashToken(token);

        var invite = new OnboardingInvite
        {
            TokenHash = hash,
            FirstName = model.FirstName.Trim(),
            LastName = model.LastName.Trim(),
            Email = model.Email.Trim(),
            RoleType = model.RoleType.Trim(),
            Status = "Pending",
            CreatedUtc = DateTime.UtcNow,
            ExpiresUtc = DateTime.UtcNow.AddDays(DefaultInviteLifespanDays),
            CreatedBy = OnboardingGuard.OwnerEmail
        };

        _db.OnboardingInvites.Add(invite);
        await _db.SaveChangesAsync();

        var link = BuildOnboardingLink(token);

        var sent = false;
        string? sendError = null;

        try
        {
            sent = await _emailSender.TrySendAsync(
                invite.Email,
                "Complete your onboarding",
                $"<p>Hello {invite.FirstName},</p><p>Please complete your onboarding using this secure link:</p><p><a href=\"{link}\">{link}</a></p><p>This link may expire; if it does, ask the sender for a fresh invite.</p>",
                $"Hello {invite.FirstName},\n\nPlease complete your onboarding using this secure link:\n{link}\n\nThis link may expire; if it does, ask the sender for a fresh invite.");
        }
        catch (Exception ex)
        {
            sendError = ex.Message;
            _logger.LogWarning(ex, "Primary onboarding invite email send failed for {Email}", invite.Email);
        }

        if (!sent)
        {
            try
            {
                await _provisioning.SendOnboardingInviteEmailAsync(invite.Email, invite.FirstName, link);
                sent = true;
            }
            catch (Exception ex)
            {
                sendError = ex.Message;
                _logger.LogError(ex, "Onboarding invite Graph fallback failed for {Email}", invite.Email);
            }
        }

        invite.Status = sent ? "Invited" : "Pending";
        await _db.SaveChangesAsync();

        TempData["NewInviteLink"] = link;
        TempData["Info"] = sent
            ? $"Invite email sent to {invite.Email}."
            : $"Invite created for {invite.Email}, but email delivery failed.";

        if (!sent)
        {
            TempData["Warning"] = string.IsNullOrWhiteSpace(sendError)
                ? "Invite created, but email was not sent. Copy the link above and send manually."
                : $"Invite created, but email was not sent: {sendError}";
        }

        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Details(Guid id)
    {
        var submission = await _db.OnboardingSubmissions
            .Include(x => x.Invite)
            .FirstOrDefaultAsync(x => x.Id == id);

        if (submission == null) return NotFound();

        return View(submission);
    }

    public async Task<IActionResult> DownloadPdf(Guid id)
    {
        var submission = await _db.OnboardingSubmissions
            .Include(x => x.Invite)
            .FirstOrDefaultAsync(x => x.Id == id);

        if (submission == null) return NotFound();

        var pdfBytes = BuildPdf(submission);
        // No FileDownloadName => browsers try to render inline instead of auto-download
        return File(pdfBytes, "application/pdf");
    }

    private static byte[] BuildPdf(Domain.Entities.OnboardingSubmission s)
    {
        string V(string? v) => string.IsNullOrWhiteSpace(v) ? "—" : v.Trim();
        string B(bool? b, string yes = "Yes", string no = "No") => b == null ? "—" : (b.Value ? yes : no);

        string addrStr = string.Join(", ", new[]
        {
            s.CurrentAddress,
            s.City,
            string.IsNullOrWhiteSpace(s.State) ? null : $"{s.State} {s.Zip}"
        }.Where(x => !string.IsNullOrWhiteSpace(x)));

        string ecStr = string.Join(" ", new[]
        {
            s.EmergencyContactName,
            string.IsNullOrWhiteSpace(s.EmergencyContactRelationship) ? null : $"({s.EmergencyContactRelationship})",
            string.IsNullOrWhiteSpace(s.EmergencyContactPhone) ? null : $"— {s.EmergencyContactPhone}"
        }.Where(x => !string.IsNullOrWhiteSpace(x)));

        string sig = string.IsNullOrWhiteSpace(s.ElectronicSignatureName) ? "—"
            : s.ElectronicSignatureName + (s.ElectronicSignatureDate.HasValue ? $" — signed {s.ElectronicSignatureDate.Value:MMM d, yyyy}" : "");

        // Legend palette (no orange): deep navy + rich gold + soft cream
        var navy = Color.FromHex("#0b1529");   // deep navy
        var gold = Color.FromHex("#a68023");   // Legend gold
        var goldSoft = Color.FromHex("#f3e7c5");
        var paper = Color.FromHex("#f8f9fb");

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.Letter);
                page.Margin(32);
                // Use bundled QuestPDF font to avoid missing system fonts on Azure/Linux
                page.DefaultTextStyle(x => x.FontSize(11).FontFamily(Fonts.Lato).FontColor(navy));
                page.PageColor(paper);

                page.Header().Row(row =>
                {
                    row.RelativeItem().Text("Legend™ Onboarding Submission").SemiBold().FontSize(16).FontColor(navy);
                    row.ConstantItem(200).AlignRight().Column(col =>
                    {
                        col.Item().Text(text =>
                        {
                            text.Span("Submitted ").SemiBold().FontColor(gold);
                            text.Span(s.SubmittedUtc?.ToLocalTime().ToString("MMM d, yyyy 'at' h:mm tt") ?? "—");
                        });
                    });
                });

                page.Content().Column(col =>
                {
                    col.Spacing(16);

                    void Section(string title, Action<IContainer> body)
                    {
                        col.Item()
                            .Border(1.2f).BorderColor(gold)
                            .Background(Colors.White)
                            .Column(section =>
                            {
                                section.Item()
                                    .Background(navy)
                                    .Padding(12)
                                    .BorderBottom(1f).BorderColor(gold)
                                    .Text(title).SemiBold().FontSize(12).FontColor(Colors.White);
                                section.Item().Background(Colors.White).Padding(14).Element(body);
                            });
                    }

                    void Field(IContainer c, string label, string value)
                    {
                        c.Background(Colors.White)
                         .Border(1f).BorderColor(gold)
                         .Padding(9)
                         .Column(x =>
                        {
                            x.Item().Text(label).FontSize(9).Bold().FontColor(gold);
                            x.Item().Text(value).FontSize(11).FontColor(navy);
                        });
                    }

                    Section("Personal Information", body =>
                    {
                        body.Column(cc =>
                        {
                            cc.Spacing(10);
                            cc.Item().Row(r =>
                            {
                                r.Spacing(10);
                                r.RelativeItem().Element(e => Field(e, "Full Name", $"{V(s.FirstName)} {V(s.MiddleName)} {V(s.LastName)}"));
                                r.RelativeItem().Element(e => Field(e, "Preferred Name", V(s.PreferredName)));
                            });
                            cc.Item().Row(r =>
                            {
                                r.Spacing(10);
                                r.RelativeItem().Element(e => Field(e, "Date of Birth", s.DateOfBirth?.ToString("MMM d, yyyy") ?? "—"));
                                r.RelativeItem().Element(e => Field(e, "Phone", V(s.Phone)));
                                r.RelativeItem().Element(e => Field(e, "Email", V(s.Email)));
                            });
                            cc.Item().Element(e => Field(e, "Current Address", V(addrStr)));
                            cc.Item().Element(e => Field(e, "Mailing Address", V(s.MailingAddress)));
                            cc.Item().Element(e => Field(e, "Emergency Contact", V(ecStr)));
                        });
                    });

                    Section("Position & Work", body =>
                    {
                        body.Column(cc =>
                        {
                            cc.Spacing(10);
                            cc.Item().Row(r =>
                            {
                                r.Spacing(10);
                                r.RelativeItem().Element(e => Field(e, "Role Type", V(s.RoleType)));
                                r.RelativeItem().Element(e => Field(e, "Job Title", V(s.JobTitle)));
                                r.RelativeItem().Element(e => Field(e, "Department", V(s.Department)));
                            });
                            cc.Item().Row(r =>
                            {
                                r.Spacing(10);
                                r.RelativeItem().Element(e => Field(e, "Manager", V(s.Manager)));
                                r.RelativeItem().Element(e => Field(e, "Start Date", s.StartDate?.ToString("MMM d, yyyy") ?? "—"));
                                r.RelativeItem().Element(e => Field(e, "Work Location", V(s.WorkLocation)));
                            });
                            cc.Item().Row(r =>
                            {
                                r.RelativeItem().Element(e => Field(e, "Employment Type", V(s.EmploymentType)));
                                r.RelativeItem().Element(e => Field(e, "Pay Type", V(s.PayType)));
                                r.RelativeItem().Element(e => Field(e, "Work Notes", V(s.WorkNotes)));
                            });
                        });
                    });

                    Section("Identity & Eligibility", body =>
                    {
                        body.Column(cc =>
                        {
                            cc.Spacing(10);
                            cc.Item().Row(r =>
                            {
                                r.Spacing(10);
                                r.RelativeItem().Element(e => Field(e, "Driver License #", V(s.DriverLicenseNumber)));
                                r.RelativeItem().Element(e => Field(e, "Driver License State", V(s.DriverLicenseState)));
                                r.RelativeItem().Element(e => Field(e, "Citizenship", V(s.CitizenshipStatus)));
                                r.RelativeItem().Element(e => Field(e, "Work Authorization", V(s.WorkAuthorizationStatus)));
                            });
                            cc.Item().Element(e => Field(e, "Eligibility Documents Ack", B(s.EligibilityDocumentsAck, "Acknowledged", "Not provided")));
                            if (!string.IsNullOrWhiteSpace(s.SsnNote))
                                cc.Item().Element(e => Field(e, "SSN Note", V(s.SsnNote)));
                        });
                    });

                    Section("Tax / Payroll", body =>
                    {
                        body.Column(cc =>
                        {
                            cc.Spacing(10);
                            cc.Item().Row(r =>
                            {
                                r.Spacing(10);
                                r.RelativeItem().Element(e => Field(e, "Tax Filing Status", V(s.TaxFilingStatus)));
                                r.RelativeItem().Element(e => Field(e, "Federal Withholding", V(s.FederalWithholding)));
                                r.RelativeItem().Element(e => Field(e, "State Withholding", V(s.StateWithholding)));
                            });
                            cc.Item().Row(r =>
                            {
                                r.Spacing(10);
                                r.RelativeItem().Element(e => Field(e, "Bank Name", V(s.BankName)));
                                r.RelativeItem().Element(e => Field(e, "Bank Account Type", V(s.BankAccountType)));
                                r.RelativeItem().Element(e => Field(e, "Routing Number", V(s.BankRoutingNumber)));
                                r.RelativeItem().Element(e => Field(e, "Account Number", V(s.BankAccountNumber)));
                            });
                            cc.Item().Element(e => Field(e, "Payroll Acknowledgement", B(s.PayrollAcknowledgement, "Acknowledged", "Not provided")));
                        });
                    });

                    Section("Licensing / Industry", body =>
                    {
                        body.Column(cc =>
                        {
                            cc.Spacing(10);
                            cc.Item().Row(r =>
                            {
                                r.Spacing(10);
                                r.RelativeItem().Element(e => Field(e, "Resident State License", V(s.ResidentStateLicense)));
                                r.RelativeItem().Element(e => Field(e, "Non-Resident States", V(s.NonResidentStates)));
                            });
                            cc.Item().Row(r =>
                            {
                                r.Spacing(10);
                                r.RelativeItem().Element(e => Field(e, "Licenses Held", V(s.LicensesHeld)));
                                r.RelativeItem().Element(e => Field(e, "License Numbers", V(s.LicenseNumbers)));
                            });
                            cc.Item().Row(r =>
                            {
                                r.RelativeItem().Element(e => Field(e, "Carrier Appointments", V(s.CarrierAppointments)));
                                r.RelativeItem().Element(e => Field(e, "E&O Coverage", V(s.EOCoverage)));
                            });
                            cc.Item().Element(e => Field(e, "Supervision Notes", V(s.SupervisionNotes)));
                        });
                    });

                    Section("Background / Disclosures", body =>
                    {
                        body.Column(cc =>
                        {
                            cc.Spacing(10);
                            cc.Item().Element(e => Field(e, "Regulatory Issues", B(s.HasRegulatoryIssues, "Yes — See Notes", "None")));
                            if (s.HasRegulatoryIssues == true)
                                cc.Item().Element(e => Field(e, "Regulatory Notes", V(s.RegulatoryExplanation)));

                            cc.Item().Element(e => Field(e, "Criminal History", B(s.HasCriminalHistory, "Yes — See Notes", "None")));
                            if (s.HasCriminalHistory == true)
                                cc.Item().Element(e => Field(e, "Criminal Notes", V(s.CriminalExplanation)));

                            cc.Item().Element(e => Field(e, "Administrative Actions", B(s.HasAdministrativeActions, "Yes — See Notes", "None")));
                            if (s.HasAdministrativeActions == true)
                                cc.Item().Element(e => Field(e, "Administrative Notes", V(s.AdministrativeExplanation)));

                            cc.Item().Element(e => Field(e, "Prior Termination", B(s.HasPriorTermination, "Yes — See Notes", "None")));
                            if (s.HasPriorTermination == true)
                                cc.Item().Element(e => Field(e, "Termination Notes", V(s.TerminationExplanation)));

                            cc.Item().Element(e => Field(e, "Other Disclosures", B(s.HasOtherDisclosures, "Yes — See Notes", "None")));
                            if (s.HasOtherDisclosures == true)
                                cc.Item().Element(e => Field(e, "Other Notes", V(s.OtherDisclosuresExplanation)));
                        });
                    });

                    Section("Document Checklist", body =>
                    {
                        body.Column(cc =>
                        {
                            cc.Spacing(10);
                            cc.Item().Row(r =>
                            {
                                r.Spacing(10);
                                r.RelativeItem().Element(e => Field(e, "Government ID", B(s.HasIdDocument, "Provided", "Missing")));
                                r.RelativeItem().Element(e => Field(e, "SSN / Tax Doc", B(s.HasSsnDocument, "Provided", "Missing")));
                                r.RelativeItem().Element(e => Field(e, "Voided Check", B(s.HasVoidedCheck, "Provided", "Missing")));
                            });
                            cc.Item().Row(r =>
                            {
                                r.RelativeItem().Element(e => Field(e, "License Copy", B(s.HasLicenseCopy, "Provided", "Missing")));
                                r.RelativeItem().Element(e => Field(e, "Certifications", B(s.HasCertifications, "Provided", "Missing")));
                                r.RelativeItem().Element(e => Field(e, "Resume", B(s.HasResume, "Provided", "Missing")));
                            });
                            cc.Item().Element(e => Field(e, "Signed Agreements", B(s.HasSignedAgreements, "Provided", "Missing")));
                            if (!string.IsNullOrWhiteSpace(s.DocumentNotes))
                                cc.Item().Element(e => Field(e, "Document Notes", V(s.DocumentNotes)));
                        });
                    });

                    col.Item().Background(Colors.White).Border(1.2f).BorderColor(gold).Padding(10).Column(cc =>
                    {
                        cc.Item().Text("Certification of truthfulness").SemiBold().FontColor(navy);
                        cc.Item().Text(B(s.CertificationTruthful, "Certified", "Not certified"));
                        cc.Item().Text($"Signature: {sig}");
                    });
                });
            });
        }).GeneratePdf();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteSubmission(Guid id)
    {
        var submission = await _db.OnboardingSubmissions.FirstOrDefaultAsync(x => x.Id == id);
        if (submission == null) return NotFound();

        _db.OnboardingSubmissions.Remove(submission);
        await _db.SaveChangesAsync();
        TempData["Success"] = "Submission deleted.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResendInvite(Guid id)
    {
        var invite = await _db.OnboardingInvites.FirstOrDefaultAsync(x => x.Id == id);
        if (invite == null) return NotFound();

        // Issue a new token/hash for security and extend expiry
        var token = await CreateUniqueTokenAsync();
        invite.TokenHash = HashToken(token);
        invite.ExpiresUtc = DateTime.UtcNow.AddDays(DefaultInviteLifespanDays);
        invite.Status = "Pending";

        var link = BuildOnboardingLink(token);

        var sent = false;
        string? sendError = null;

        try
        {
            sent = await _emailSender.TrySendAsync(
                invite.Email,
                "Complete your onboarding",
                $"<p>Hello {invite.FirstName},</p><p>Please complete your onboarding using this secure link:</p><p><a href=\"{link}\">{link}</a></p><p>This link may expire; if it does, ask the sender for a fresh invite.</p>",
                $"Hello {invite.FirstName},\n\nPlease complete your onboarding using this secure link:\n{link}\n\nThis link may expire; if it does, ask the sender for a fresh invite.");
        }
        catch (Exception ex)
        {
            sendError = ex.Message;
            _logger.LogWarning(ex, "Onboarding resend primary email failed for {Email}", invite.Email);
        }

        if (!sent)
        {
            try
            {
                await _provisioning.SendOnboardingInviteEmailAsync(invite.Email, invite.FirstName, link);
                sent = true;
            }
            catch (Exception ex)
            {
                sendError = ex.Message;
                _logger.LogError(ex, "Onboarding resend Graph fallback failed for {Email}", invite.Email);
            }
        }

        invite.Status = sent ? "Invited" : "Pending";
        await _db.SaveChangesAsync();

        TempData["Info"] = sent
            ? $"Invite resent to {invite.Email}."
            : $"Invite regenerated for {invite.Email}, but email was not sent.";

        if (!sent && !string.IsNullOrWhiteSpace(sendError))
        {
            TempData["Warning"] = $"Email send failed: {sendError}";
        }

        TempData["NewInviteLink"] = link;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteInvite(Guid id)
    {
        var invite = await _db.OnboardingInvites
            .Include(x => x.Submissions)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (invite == null) return NotFound();

        if (invite.Submissions.Any())
        {
            invite.Status = "Archived";
            invite.RevokedUtc = DateTime.UtcNow;
            TempData["Success"] = $"Invite for {invite.Email} archived (submission retained).";
        }
        else
        {
            _db.OnboardingInvites.Remove(invite);
            TempData["Success"] = $"Invite for {invite.Email} deleted.";
        }

        await _db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }
}
