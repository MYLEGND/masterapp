namespace Shared.ClientExperience;

public sealed class ProtectionSnapshotState
{
    public string HouseholdStage { get; set; } = "Foundation";
    public string PrimaryGoal { get; set; } = "Protect income";
    public string HousingStatus { get; set; } = "Own";
    public int DependentsCount { get; set; }
    public int EmergencyFundMonths { get; set; } = 3;
    public int IncomeProtectionYears { get; set; } = 10;
    public string LegalDocsStatus { get; set; } = "Not started";
    public string BeneficiariesStatus { get; set; } = "Needs review";
    public string LegacyPlanStatus { get; set; } = "Needs review";
    public string ReviewCadence { get; set; } = "Semiannual";
    public bool HasLifeInsurance { get; set; }
    public bool HasDisabilityCoverage { get; set; }
    public bool HasLongTermCarePlan { get; set; }
    public bool HasEstateDocuments { get; set; }
    public bool HasEmergencyContacts { get; set; }
    public bool HasSharedDocumentVault { get; set; }
    public bool OwnsBusiness { get; set; }
    public bool HasEmployees { get; set; }
    public bool DrivesForWork { get; set; }
    public bool GivesProfessionalAdvice { get; set; }
    public bool HandlesCustomerData { get; set; }
    public bool HasHomeInsurance { get; set; }
    public bool HasRentersInsurance { get; set; }
    public bool HasAutoInsurance { get; set; }
    public bool HasUmbrellaCoverage { get; set; }
    public bool HasGeneralLiability { get; set; }
    public bool HasProfessionalLiability { get; set; }
    public bool HasCyberCoverage { get; set; }
    public bool HasWorkersComp { get; set; }
    public bool HasCommercialAuto { get; set; }
    public bool HasMortgageProtection { get; set; }
    public bool HasEquityProtection { get; set; }
    public bool HasBusinessDisabilityCoverage { get; set; }
    public bool HasWillInPlace { get; set; }
    public bool HasTrustInPlace { get; set; }
    public List<string> PriorityFocusAreas { get; set; } = new();
    public List<string> ProtectionNeeds { get; set; } = new();
    public List<string> RecentLifeEvents { get; set; } = new();
    public string AgentFollowUpFocus { get; set; } = string.Empty;
    public string ClientNotes { get; set; } = string.Empty;
    public DateTime? LastReviewUtc { get; set; }
    public DateTime? NextReviewUtc { get; set; }
}