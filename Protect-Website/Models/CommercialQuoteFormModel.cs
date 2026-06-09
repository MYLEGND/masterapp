using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Protect_Website.Models
{
    public class CommercialQuoteFormModel
    {
        // ===================== WIZARD STATE =====================
        // Keeps the user on the same step after a failed submit
        public int CurrentStep { get; set; } = 1;

        // ===================== MUST-HAVE (REQUIRED) =====================
        [Required(ErrorMessage = "Risk State is required")]
        public string State { get; set; } = "";

        [Required(ErrorMessage = "Business Name is required")]
        public string BusinessName { get; set; } = "";

        [Required(ErrorMessage = "Business description is required")]
        public string BusinessDescription { get; set; } = "";

        // These 3 are quote-critical
        [Required(ErrorMessage = "Gross Sales is required")]
        public decimal? GrossSales { get; set; }

        [Required(ErrorMessage = "Total Payroll is required")]
        public decimal? TotalPayroll { get; set; }

        [Required(ErrorMessage = "Number of Employees is required")]
        public string NumberOfEmployees { get; set; } = "";

        // Contact
        [Required(ErrorMessage = "Insured First Name is required")]
        public string InsuredFirstName { get; set; } = "";

        [Required(ErrorMessage = "Insured Last Name is required")]
        public string InsuredLastName { get; set; } = "";

        [Required(ErrorMessage = "Business Phone is required")]
        [Phone(ErrorMessage = "Enter a valid phone number")]
        public string BusinessPhone { get; set; } = "";

        [Required(ErrorMessage = "Business Email is required")]
        [EmailAddress(ErrorMessage = "Enter a valid email address")]
        public string BusinessEmail { get; set; } = "";

        // Address
        [Required(ErrorMessage = "Street Address is required")]
        public string StreetAddress { get; set; } = "";

        public string? AddressLine2 { get; set; } = null;

        [Required(ErrorMessage = "City is required")]
        public string City { get; set; } = "";

        [Required(ErrorMessage = "ZIP Code is required")]
        public string ZipCode { get; set; } = "";

        // Coverage & timing
        [Required(ErrorMessage = "Effective Date is required")]
        [DataType(DataType.Date)]
        public DateTime? EffectiveDate { get; set; }

        [MinLength(1, ErrorMessage = "Please select at least one coverage type")]
        public List<string> InterestedIn { get; set; } = new();

        // Contact preference
        [Required(ErrorMessage = "Preferred contact method is required")]
        public string PreferredContactMethod { get; set; } = "";

        [Required(ErrorMessage = "Best time to contact is required")]
        public string BestTimeToContact { get; set; } = "";

        // Disclaimer
        [Display(Name = "Acknowledged Disclaimer")]
        public bool AcknowledgedDisclaimer { get; set; } = false;

        // ===================== OPTIONAL (NOT REQUIRED) =====================

        // Business operations (optional extras)
        public string? YearsInBusiness { get; set; }
        public string? YearsOfExperience { get; set; }

        // This should NOT be required (people might not have one)
        public string? BusinessWebsiteOrFacebook { get; set; }

        public string? Comments { get; set; }

        // ===================== ENTITY & GENERAL INFO =====================
        public string? EntityType { get; set; }
        public string? FederalTaxId { get; set; }

        public bool? HasActivePropertyLiabilityPolicy { get; set; }
        public DateTime? PriorCoverageEndDate { get; set; }

        public string? OfficersMembersPartners { get; set; }
        public DateTime? CurrentRenewalDate { get; set; }

        public bool? OwnsOtherBusinesses { get; set; }
        public string? OtherBusinessTypes { get; set; }

        public bool? HasHighPublicProfile { get; set; }
        public bool? IsSocialMediaInfluencer { get; set; }

        // ===================== LIABILITY, PAYROLL & AUTO =====================
        public decimal? LiabilityOccurrenceLimit { get; set; }
        public decimal? MedicalExpenseLimit { get; set; }

        public decimal? PropertyDamageDeductible { get; set; }
        public string? PropertyDamageDeductibleType { get; set; }
        public decimal? BodilyInjuryDeductible { get; set; }

        public int? FullTimeEmployees { get; set; }
        public int? PartTimeEmployees { get; set; }

        public bool? HiredNonOwnedAutoRequested { get; set; }
        public decimal? DeliveryPercentage { get; set; }

        public bool? HasDriverMonitoringProgram { get; set; }
        public bool? DriversHaveThreeYearsExperience { get; set; }

        // ===================== OPTIONAL & PROFESSIONAL COVERAGES =====================
        public decimal? DamageToPremisesLimit { get; set; }

        public bool? DataCompromiseRequested { get; set; }
        public decimal? DataCompromiseLimit { get; set; }
        public bool? HadDataBreachLast12Months { get; set; }

        public decimal? ElectronicDataLimit { get; set; }

        public decimal? EmployeeDishonestyLimit { get; set; }
        public decimal? ForgeryAlterationLimit { get; set; }

        public decimal? ComputerInterruptionLimit { get; set; }
        public decimal? OffPremisesPersonalPropertyLimit { get; set; }

        public bool? TerrorismCoverageRequested { get; set; }

        public bool? MiscProfessionalLiabilityRequested { get; set; }
        public decimal? MiscProfessionalLiabilityLimit { get; set; }
        public DateTime? MiscProfessionalRetroDate { get; set; }
        public bool? MiscProfessionalClaimsLast5Years { get; set; }

        public bool? CyberSuiteRequested { get; set; }
        public decimal? CyberSuiteLimit { get; set; }

        // ===================== HR, LEGAL & EPLI =====================
        public bool? BackgroundChecksPerformed { get; set; }
        public bool? DocumentRetentionPolicy { get; set; }
        public bool? CyberSecurityMeasuresInPlace { get; set; }
        public bool? RecordsStoredSecurely { get; set; }

        public bool? BlanketAdditionalInsuredRequested { get; set; }
        public bool? WaiverOfSubrogationRequested { get; set; }

        public bool? EmployeeBenefitsLiabilityRequested { get; set; }
        public decimal? EmployeeBenefitsLimit { get; set; }
        public DateTime? EmployeeBenefitsRetroDate { get; set; }

        public bool? EPLIRequested { get; set; }
        public decimal? EPLILimit { get; set; }
        public decimal? EPLIDeductible { get; set; }
        public DateTime? EPLIRetroDate { get; set; }

        // ===================== LOSS HISTORY =====================
        public bool? PolicyCancelledLast3Years { get; set; }
        public bool? LossesLast4Years { get; set; }
        public string? LossHistoryDetails { get; set; }

        public bool? PastFraudConvictions { get; set; }
        public bool? PastFinancialIssues { get; set; }
        public bool? PastAbuseClaims { get; set; }

        // ===================== BUILDING INFORMATION =====================
        public bool? BuildingNearFireStation { get; set; }
        public bool? BuildingNearFireHydrant { get; set; }
        public int? YearsInBusinessAtLocation { get; set; }

        public string? Occupancy { get; set; }
        public string? BuildingType { get; set; }
        public bool? SoleOccupant { get; set; }
        public string? BuildingIndustry { get; set; }
        public bool? RestaurantOccupiedPart { get; set; }
        public string? ConstructionType { get; set; }

        public int? YearBuilt { get; set; }
        public int? TotalBuildingSF { get; set; }
        public int? OccupiedSF { get; set; }

        public bool? AutomaticSprinklerSystem { get; set; }
        public string? BurglarAlarm { get; set; }
        public string? FireAlarm { get; set; }

        // ===================== CLASS SPECIFIC QUESTIONS =====================
        public bool? BuildingCoverageNeeded { get; set; }
        public int? BuildingOccupancyPercent { get; set; }
        public bool? StructuralRenovations { get; set; }

        // ===================== BUILDING & PERSONAL PROPERTY COVERAGES =====================
        public decimal? BuildingCoverageLimit { get; set; }
        public string? ValuationType { get; set; }
        public int? InflationGuardPercent { get; set; }

        // ── Attribution (populated by JS before submit, persisted server-side) ──
        public string? SessionId { get; set; }
        public string? VisitorId { get; set; }
        public string? UtmSource { get; set; }
        public string? UtmMedium { get; set; }
        public string? UtmCampaign { get; set; }
        public string? UtmId { get; set; }
        public string? UtmTerm { get; set; }
        public string? UtmContent { get; set; }
        public string? MetaCampaignId { get; set; }
        public string? MetaAdSetId { get; set; }
        public string? MetaAdId { get; set; }
        public string? Fbclid { get; set; }
        public string? ReferrerUrl { get; set; }
        public string? LandingPageUrl { get; set; }
    }
}
