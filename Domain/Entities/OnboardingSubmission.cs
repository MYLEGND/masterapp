namespace Domain.Entities;

public class OnboardingSubmission
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid InviteId { get; set; }
    public OnboardingInvite? Invite { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SubmittedUtc { get; set; }

    // Personal Information
    public string FirstName { get; set; } = "";
    public string? MiddleName { get; set; }
    public string LastName { get; set; } = "";
    public string? PreferredName { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public string CurrentAddress { get; set; } = "";
    public string City { get; set; } = "";
    public string State { get; set; } = "";
    public string? Zip { get; set; }
    public string? MailingAddress { get; set; }
    public string EmergencyContactName { get; set; } = "";
    public string EmergencyContactPhone { get; set; } = "";
    public string EmergencyContactRelationship { get; set; } = "";

    // Position / Work
    public string RoleType { get; set; } = "";
    public string JobTitle { get; set; } = "";
    public string? Department { get; set; }
    public string? Manager { get; set; }
    public DateTime? StartDate { get; set; }
    public string? WorkState { get; set; }
    public string? WorkLocation { get; set; }
    public string EmploymentType { get; set; } = "";
    public string PayType { get; set; } = "";
    public string? WorkNotes { get; set; }

    // Identity / Work Eligibility
    public bool LegalNameConfirmed { get; set; }
    public string? SsnLast4 { get; set; }
    public string? SsnNote { get; set; }
    public string? DriverLicenseNumber { get; set; }
    public string? DriverLicenseState { get; set; }
    public string WorkAuthorizationStatus { get; set; } = "";
    public string? CitizenshipStatus { get; set; }
    public bool EligibilityDocumentsAck { get; set; }

    // Tax / Payroll
    public string TaxFilingStatus { get; set; } = "";
    public string? FederalWithholding { get; set; }
    public string? StateWithholding { get; set; }
    public string BankName { get; set; } = "";
    public string BankAccountType { get; set; } = "";
    public string BankRoutingNumber { get; set; } = "";
    public string BankAccountNumber { get; set; } = "";
    public bool PayrollAcknowledgement { get; set; }

    // Agreements / Acknowledgements
    public bool ConfidentialityAck { get; set; }
    public bool HandbookAck { get; set; }
    public bool TechnologyAck { get; set; }
    public bool ComplianceAck { get; set; }
    public bool CompensationAck { get; set; }
    public bool NonSolicitAck { get; set; }
    public bool ElectronicSignatureAck { get; set; }
    public string ElectronicSignatureName { get; set; } = "";
    public DateTime? ElectronicSignatureDate { get; set; }

    // Licensing / Industry
    public string? ResidentStateLicense { get; set; }
    public string? NonResidentStates { get; set; }
    public string? LicensesHeld { get; set; }
    public string? LicenseNumbers { get; set; }
    public string? CarrierAppointments { get; set; }
    public string? EOCoverage { get; set; }
    public string? SupervisionNotes { get; set; }

    // Background / Disclosures
    public bool? HasRegulatoryIssues { get; set; }
    public string? RegulatoryExplanation { get; set; }
    public bool? HasCriminalHistory { get; set; }
    public string? CriminalExplanation { get; set; }
    public bool? HasAdministrativeActions { get; set; }
    public string? AdministrativeExplanation { get; set; }
    public bool? HasPriorTermination { get; set; }
    public string? TerminationExplanation { get; set; }
    public bool? HasOtherDisclosures { get; set; }
    public string? OtherDisclosuresExplanation { get; set; }

    // Documents checklist
    public bool? HasIdDocument { get; set; }
    public bool? HasSsnDocument { get; set; }
    public bool? HasVoidedCheck { get; set; }
    public bool? HasLicenseCopy { get; set; }
    public bool? HasCertifications { get; set; }
    public bool? HasResume { get; set; }
    public bool? HasSignedAgreements { get; set; }
    public string? DocumentNotes { get; set; }

    // Final review
    public bool CertificationTruthful { get; set; }
}
