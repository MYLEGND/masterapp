using System.ComponentModel.DataAnnotations;
using Domain.Entities;

namespace AgentPortal.Models;

public class OnboardingInviteInputModel
{
    [Required, StringLength(120)]
    public string FirstName { get; set; } = "";

    [Required, StringLength(120)]
    public string LastName { get; set; } = "";

    [Required, EmailAddress, StringLength(320)]
    public string Email { get; set; } = "";

    [Required, StringLength(120)]
    public string RoleType { get; set; } = "";
}

public class OnboardingDashboardViewModel
{
    public List<OnboardingInvite> Invites { get; set; } = new();
    public List<OnboardingSubmission> Submissions { get; set; } = new();
    public OnboardingInviteInputModel NewInvite { get; set; } = new();
    public string? NewInviteLink { get; set; }
}

public class OnboardingFormViewModel : IValidatableObject
{
    [Required]
    public string Token { get; set; } = "";

    // Personal Information
    [Required, StringLength(120)]
    public string FirstName { get; set; } = "";

    [StringLength(120)]
    public string? MiddleName { get; set; }

    [Required, StringLength(120)]
    public string LastName { get; set; } = "";

    [StringLength(120)]
    public string? PreferredName { get; set; }

    [Required, DataType(DataType.Date)]
    public DateTime? DateOfBirth { get; set; }

    [Required, Phone, StringLength(60)]
    public string Phone { get; set; } = "";

    [Required, EmailAddress, StringLength(320)]
    public string Email { get; set; } = "";

    [Required, StringLength(240)]
    public string CurrentAddress { get; set; } = "";

    [Required, StringLength(160)]
    public string City { get; set; } = "";

    [Required, StringLength(80)]
    public string State { get; set; } = "";

    [Required, StringLength(40)]
    public string Zip { get; set; } = "";

    [StringLength(240)]
    public string? MailingAddress { get; set; }

    [Required, StringLength(160)]
    public string EmergencyContactName { get; set; } = "";

    [Required, Phone, StringLength(60)]
    public string EmergencyContactPhone { get; set; } = "";

    [Required, StringLength(120)]
    public string EmergencyContactRelationship { get; set; } = "";

    // Position / Work
    [Required, StringLength(80)]
    public string RoleType { get; set; } = "";

    [Required, StringLength(160)]
    public string JobTitle { get; set; } = "";

    [StringLength(160)]
    public string? Department { get; set; }

    [StringLength(160)]
    public string? Manager { get; set; }

    [Required, DataType(DataType.Date)]
    public DateTime? StartDate { get; set; }

    [StringLength(80)]
    public string? WorkState { get; set; }

    [StringLength(200)]
    public string? WorkLocation { get; set; }

    [Required, StringLength(80)]
    public string EmploymentType { get; set; } = "";

    [Required, StringLength(80)]
    public string PayType { get; set; } = "";

    [StringLength(2000)]
    public string? WorkNotes { get; set; }

    // Identity / Eligibility
    [Required, Range(typeof(bool), "true", "true", ErrorMessage = "Please confirm the legal name statement.")]
    public bool LegalNameConfirmed { get; set; }

    [StringLength(4, MinimumLength = 4, ErrorMessage = "Use last 4 only.")]
    public string? SsnLast4 { get; set; }

    [StringLength(400)]
    public string? SsnNote { get; set; }

    [StringLength(80)]
    public string? DriverLicenseNumber { get; set; }

    [StringLength(40)]
    public string? DriverLicenseState { get; set; }

    [Required, StringLength(160)]
    public string WorkAuthorizationStatus { get; set; } = "";

    [StringLength(160)]
    public string? CitizenshipStatus { get; set; }

    [Required, Range(typeof(bool), "true", "true", ErrorMessage = "Please acknowledge document requirements.")]
    public bool EligibilityDocumentsAck { get; set; }

    // Tax / Payroll
    [Required, StringLength(120)]
    public string TaxFilingStatus { get; set; } = "";

    [StringLength(120)]
    public string? FederalWithholding { get; set; }

    [StringLength(120)]
    public string? StateWithholding { get; set; }

    [Required, StringLength(160)]
    public string BankName { get; set; } = "";

    [Required, StringLength(80)]
    public string BankAccountType { get; set; } = "";

    [Required, StringLength(64)]
    public string BankRoutingNumber { get; set; } = "";

    [Required, StringLength(64)]
    public string BankAccountNumber { get; set; } = "";

    [Required, Range(typeof(bool), "true", "true", ErrorMessage = "Payroll acknowledgement is required.")]
    public bool PayrollAcknowledgement { get; set; }

    // Agreements
    [Required, Range(typeof(bool), "true", "true", ErrorMessage = "Confirm confidentiality.")]
    public bool ConfidentialityAck { get; set; }

    [Required, Range(typeof(bool), "true", "true", ErrorMessage = "Confirm handbook/policy receipt.")]
    public bool HandbookAck { get; set; }

    [Required, Range(typeof(bool), "true", "true", ErrorMessage = "Confirm technology/device use acknowledgement.")]
    public bool TechnologyAck { get; set; }

    [Required, Range(typeof(bool), "true", "true", ErrorMessage = "Confirm compliance acknowledgement.")]
    public bool ComplianceAck { get; set; }

    [Required, Range(typeof(bool), "true", "true", ErrorMessage = "Confirm compensation understanding.")]
    public bool CompensationAck { get; set; }

    [Required, Range(typeof(bool), "true", "true", ErrorMessage = "Confirm non-solicit/internal policy acknowledgement.")]
    public bool NonSolicitAck { get; set; }

    [Required, Range(typeof(bool), "true", "true", ErrorMessage = "Electronic signature consent is required.")]
    public bool ElectronicSignatureAck { get; set; }

    [Required, StringLength(200)]
    public string ElectronicSignatureName { get; set; } = "";

    [Required, DataType(DataType.Date)]
    public DateTime? ElectronicSignatureDate { get; set; }

    // Licensing / Industry
    [StringLength(80)]
    public string? ResidentStateLicense { get; set; }

    [StringLength(400)]
    public string? NonResidentStates { get; set; }

    [StringLength(400)]
    public string? LicensesHeld { get; set; }

    [StringLength(400)]
    public string? LicenseNumbers { get; set; }

    [StringLength(400)]
    public string? CarrierAppointments { get; set; }

    [StringLength(400)]
    public string? EOCoverage { get; set; }

    [StringLength(2000)]
    public string? SupervisionNotes { get; set; }

    // Background / Disclosures
    [Required]
    public bool HasRegulatoryIssues { get; set; }

    [StringLength(4000)]
    public string? RegulatoryExplanation { get; set; }

    [Required]
    public bool HasCriminalHistory { get; set; }

    [StringLength(4000)]
    public string? CriminalExplanation { get; set; }

    [Required]
    public bool HasAdministrativeActions { get; set; }

    [StringLength(4000)]
    public string? AdministrativeExplanation { get; set; }

    [Required]
    public bool HasPriorTermination { get; set; }

    [StringLength(4000)]
    public string? TerminationExplanation { get; set; }

    public bool HasOtherDisclosures { get; set; }

    [StringLength(4000)]
    public string? OtherDisclosuresExplanation { get; set; }

    // Documents checklist
    public bool HasIdDocument { get; set; }
    public bool HasSsnDocument { get; set; }
    public bool HasVoidedCheck { get; set; }
    public bool HasLicenseCopy { get; set; }
    public bool HasCertifications { get; set; }
    public bool HasResume { get; set; }
    public bool HasSignedAgreements { get; set; }

    [StringLength(2000)]
    public string? DocumentNotes { get; set; }

    [Required, Range(typeof(bool), "true", "true", ErrorMessage = "Certification is required.")]
    public bool CertificationTruthful { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (HasRegulatoryIssues && string.IsNullOrWhiteSpace(RegulatoryExplanation))
            yield return new ValidationResult("Provide details for regulatory issues.", new[] { nameof(RegulatoryExplanation) });

        if (HasCriminalHistory && string.IsNullOrWhiteSpace(CriminalExplanation))
            yield return new ValidationResult("Provide details for criminal history.", new[] { nameof(CriminalExplanation) });

        if (HasAdministrativeActions && string.IsNullOrWhiteSpace(AdministrativeExplanation))
            yield return new ValidationResult("Provide details for administrative actions.", new[] { nameof(AdministrativeExplanation) });

        if (HasPriorTermination && string.IsNullOrWhiteSpace(TerminationExplanation))
            yield return new ValidationResult("Provide details for prior termination/discipline.", new[] { nameof(TerminationExplanation) });

        if (HasOtherDisclosures && string.IsNullOrWhiteSpace(OtherDisclosuresExplanation))
            yield return new ValidationResult("Provide details for other disclosures.", new[] { nameof(OtherDisclosuresExplanation) });
    }
}
