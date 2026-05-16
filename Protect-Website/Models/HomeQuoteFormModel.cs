using System;
using System.ComponentModel.DataAnnotations;

namespace Protect_Website.Models
{
    public class HomeQuoteFormModel
    {
        // ===================== SECTION 1: APPLICANT INFO =====================

        [Required]
        public string? FirstName { get; set; }

        [Required]
        public string? LastName { get; set; }

        public string? Nickname { get; set; }

        [Required]
        public string? AddressState { get; set; }   // state abbrev dropdown

        [Required]
        public string? PostalCode { get; set; }

        [Required]
        public string? Gender { get; set; }

        [Required]
        [DataType(DataType.Date)]
        public DateTime? DOB { get; set; }

        [Required]
        public string? MaritalStatus { get; set; }

        [Required]
        public string? DriversLicenseNumber { get; set; }

        [Required]
        public string? DLStatus { get; set; }

        [Required]
        public string? DLState { get; set; }

        public string? Education { get; set; }

        [Required]
        public string? Industry { get; set; }


        // ===================== SECTION 2: ADDRESS & CONTACT =====================

        // Primary Address
        [Required]
        public string? PrimaryAddress { get; set; }

        public string? PrimaryUnit { get; set; }

        public string? PrimaryAddressLine2 { get; set; }

        [Required]
        public string? PrimaryCity { get; set; }

        [Required]
        public string? PrimaryState { get; set; }

        [Required]
        public string? PrimaryCountry { get; set; }

        [Required]
        public string? PrimaryPostalCode { get; set; }

        [Required]
        public string? PrimaryYearsAtAddress { get; set; }  // you used input text, not numeric

        // Previous Address (conditionally required in JS)
        public string? PreviousAddress { get; set; }
        public string? PreviousUnit { get; set; }
        public string? PreviousAddressLine2 { get; set; }
        public string? PreviousCity { get; set; }
        public string? PreviousState { get; set; }
        public string? PreviousCountry { get; set; }
        public string? PreviousPostalCode { get; set; }
        public string? PreviousYearsAtAddress { get; set; }

        // Contact Info
        [Required]
        public string? PhoneType { get; set; }

        [Required]
        public string? PhoneNumber { get; set; }

        [Required]
        public string? EmailType { get; set; }

        [Required]
        [EmailAddress]
        public string? EmailAddress { get; set; }

        [Required]
        public string? PreferredContactMethod { get; set; }

        [Required]
        public string? BestTimeToContact { get; set; }


        // ===================== SECTION 3: POLICY INFORMATION =====================

        [Required]
        public string? PolicyFormType { get; set; }

        [Required]
        public string? PriorCarrier { get; set; }

        [Required]
        [DataType(DataType.Date)]
        public DateTime? CurrentPolicyExpirationDate { get; set; }

        public string? PriorPolicyPremium { get; set; }

        [Required]
        public string? YearsWithPriorCarrier { get; set; }

        [Required]
        public string? MonthsWithPriorCarrier { get; set; }

        [Required]
        public string? YearsContinuousCoverage { get; set; }

        [Required]
        public string? MonthsContinuousCoverage { get; set; }

        [Required]
        public string? CreditCheckAuthorized { get; set; }

        [Required]
        public string? QuoteAsPackage { get; set; }

        [Required]
        [DataType(DataType.Date)]
        public DateTime? NewPolicyEffectiveDate { get; set; }

        // Underwriting Information
        [Required]
        public string? CancelledDeclinedNonRenewedLast5Years { get; set; }

        [Required]
        public string? HomeUnderConstruction { get; set; }

        [Required]
        public string? BusinessOrDaycareOnPremises { get; set; }

        public string? NumberOfEmployees { get; set; }

        [Required]
        public string? SwimmingPoolOnPremises { get; set; }

        [Required]
        public string? DogsOnPremises { get; set; }

        // Additional Carrier Questions
        [Required]
        public string? Paperless { get; set; }

        [Required]
        public string? NumberOfAnimalsOnPremises { get; set; }

        [Required]
        public string? LapseInCoveragePast12Months { get; set; }

        public string? AdditionalCarrierQuestions { get; set; }

        [Required]
        public string? AutoYearsWithPriorCarrierOrAgent { get; set; }


        // ===================== SECTION 4: DWELLING INFORMATION =====================

        [Required]
        public string? DwellingUsage { get; set; }

        [Required]
        public string? OccupancyType { get; set; }

        [Required]
        public string? DwellingType { get; set; }

        [Required]
        public string? NumberOfOccupants { get; set; }

        [Required]
        public string? NumberOfStories { get; set; }

        [Required]
        public string? SquareFootage { get; set; }

        [Required]
        public string? YearBuilt { get; set; }

        [Required]
        public string? ConstructionStyle { get; set; }

        [Required]
        public string? RoofTypeMainMaterial { get; set; }

        [Required]
        public string? FoundationType { get; set; }

        [Required]
        public string? RoofDesign { get; set; }

        [Required]
        public string? ExteriorWalls { get; set; }

        [Required]
        public string? FullBaths { get; set; }

        public string? HalfBaths { get; set; }
        public string? WoodBurningStoves { get; set; }

        public string? BurglarAlarm { get; set; }
        public string? FireDetection { get; set; }
        public string? SprinklerSystem { get; set; }
        public string? SmokeDetector { get; set; }

        public string? PurchasePrice { get; set; }

        [Required]
        [DataType(DataType.Date)]
        public DateTime? PurchaseDate { get; set; }

        [Required]
        public string? DistanceFromFireStationMiles { get; set; }

        [Required]
        public string? FeetFromHydrant { get; set; }

        [Required]
        public string? HeatingUpdate { get; set; }
        public string? HeatingYearUpdated { get; set; }

        [Required]
        public string? ElectricalUpdate { get; set; }
        public string? ElectricalYearUpdated { get; set; }

        [Required]
        public string? PlumbingUpdate { get; set; }
        public string? PlumbingYearUpdated { get; set; }

        [Required]
        public string? RoofingUpdate { get; set; }
        public string? RoofingYearUpdated { get; set; }


        // ===================== SECTION 5: GENERAL COVERAGES =====================

        [Required]
        public string? DwellingCoverage { get; set; }

        [Required]
        public string? EstReplacementCost { get; set; }

        public string? PersonalProperty { get; set; }
        public string? LossOfUse { get; set; }

        [Required]
        public string? PersonalLiability { get; set; }

        [Required]
        public string? MedicalPayments { get; set; }

        [Required]
        public string? AllPerilsDeductible { get; set; }

        public string? TheftDeductible { get; set; }
        public string? WindDeductible { get; set; }

        public string? FirstMortgagee { get; set; }
        public string? SecondMortgagee { get; set; }
        public string? ThirdMortgagee { get; set; }
        public string? Cosigner { get; set; }
        public string? EquityLineOfCredit { get; set; }
        public string? NumberOfOtherInterests { get; set; }


        // ===================== SECTION 6: ENDORSEMENTS + EARTHQUAKE + DISCLAIMER =====================

        public string? BuildingAdditionsOrAlterations { get; set; }
        public string? IncreasedReplacementCostDwellingPercentage { get; set; }
        public string? LossAssessment { get; set; }
        public string? OrdinanceOrLaw { get; set; }
        public string? IncreasedCoverageOnCreditCard { get; set; }
        public string? IncreasedLimitJewelryWatchesFurs { get; set; }
        public string? WaterBackup { get; set; }
        public string? IncreasedMoldPropertyDamage { get; set; }
        public string? PersonalInjury { get; set; }
        public string? SpecialPersonalProperty { get; set; }
        public string? SinkholeCollapse { get; set; }

        public string? EarthquakeZone { get; set; }
        public string? EarthquakeDeductible { get; set; }
        public string? PercentVeneer { get; set; }

        public bool AcknowledgedDisclaimer { get; set; }

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
