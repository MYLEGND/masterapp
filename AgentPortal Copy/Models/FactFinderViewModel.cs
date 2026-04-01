// Models/FactFinderViewModel.cs
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace AgentPortal.Models
{
    /// <summary>
    /// ONE model that supports Senior + Middle + Young Razor forms.
    /// Binding works because your input names use:
    ///   Senior.*  Middle.*  Young.*
    /// </summary>
    public class FactFinderViewModel
    {
        // Used to decide which form was submitted (ex: "Senior", "Middle", "Young")
        public string? FormType { get; set; }

        public SeniorFactFinder Senior { get; set; } = new SeniorFactFinder();
        public MiddleFactFinder Middle { get; set; } = new MiddleFactFinder();
        public YoungFactFinder Young { get; set; } = new YoungFactFinder();
    }

    // =====================================================================
    // MIDDLE-AGED  (UNCHANGED from your version)
    // =====================================================================
public class MiddleFactFinder
    {
        public MiddleApplicant Applicant { get; set; } = new();
        public MiddleEmployerBenefits ApplicantBenefits { get; set; } = new();
        public MiddleSpouse Spouse { get; set; } = new();

        public MiddleDirection Direction { get; set; } = new();
        public List<MiddleDependent> Dependents { get; set; } = new();

        public string? FamilySupportOutsideHousehold { get; set; }
        public MiddleEmergencyContact EmergencyContact { get; set; } = new();

        public string? SourceOfVisit { get; set; }
        public string? Agents { get; set; }
        public DateTime? DateOfAppointment { get; set; }

        public MiddleHealth Health { get; set; } = new();
        public MiddleLife Life { get; set; } = new();
        public MiddleDI DI { get; set; } = new();
        public MiddleLiability Liability { get; set; } = new();

        public MiddleCashflow Cashflow { get; set; } = new();
        public MiddleDebt Debt { get; set; } = new();

        public MiddleAssets Assets { get; set; } = new();
        public List<MiddleRealEstateItem> RealEstate { get; set; } = new();
        public MiddleBusiness Business { get; set; } = new();
        public MiddleEquityComp EquityComp { get; set; } = new();

        public MiddleTax Tax { get; set; } = new();
        public MiddleCollege College { get; set; } = new();
        public MiddleLegacy Legacy { get; set; } = new();

// Force checkbox to be checked (true) to pass validation
[Range(typeof(bool), "true", "true", ErrorMessage = "You must acknowledge the disclaimer to proceed.")]
public bool AcknowledgedDisclaimer { get; set; }
    }

    public class MiddleApplicant
    {
        [Required(ErrorMessage = "Applicant Name is required.")]
        public string? Name { get; set; }

        [Required(ErrorMessage = "Date of Birth is required.")]
        public DateTime? DOB { get; set; }

        public string? Email { get; set; }
        public string? Phone { get; set; }

        public string? Occupation { get; set; }
        public string? Employer { get; set; }
        public int? YearsInRole { get; set; }

        public string? IncomeType { get; set; }
        public decimal? AnnualIncome { get; set; }
        public string? IncomeTrend { get; set; }
        public string? IncomeThreats { get; set; }
    }

    public class MiddleSpouse
    {
        public string? Name { get; set; }
        public DateTime? DOB { get; set; }
        public string? Occupation { get; set; }
        public string? Employer { get; set; }
        public decimal? AnnualIncome { get; set; }
    }

    public class MiddleDirection
    {
        public string? WinningDefinition { get; set; }
        public string? BiggestFearIfNoChange { get; set; }
        public string? ChangeOneThing { get; set; }
    }

    public class MiddleDependent
    {
        public string? Name { get; set; }
        public int? Age { get; set; }
        public string? Relationship { get; set; }
        public string? SpecialNeeds { get; set; }
    }

    public class MiddleEmergencyContact
    {
        public string? Name { get; set; }
        public string? Phone { get; set; }
        public string? Email { get; set; }
    }

    public class MiddleEmployerBenefits
    {
        public bool Health { get; set; }
        public bool Dental { get; set; }
        public bool Vision { get; set; }
        public bool HSA { get; set; }
        public bool FSA { get; set; }
        public bool GroupLife { get; set; }
        public bool GroupDI { get; set; }
        public bool RetirementPlan { get; set; }

        public string? RetMatch { get; set; }
        public string? Vesting { get; set; }
        public string? Other { get; set; }
    }

    public class MiddleHealth
    {
        public string? CurrentCoverageSummary { get; set; }
        public string? Carrier { get; set; }
        public decimal? Premium { get; set; }
        public string? Deductible { get; set; }
        public string? OOPMax { get; set; }
        public string? HSABalance { get; set; }

        public string? LastReviewNotes { get; set; }
        public string? MedicalBillsSurprises { get; set; }

        public MiddleHealthAdditional Additional { get; set; } = new();

        public string? HealthLastThreeYears { get; set; }
        public string? CurrentMeds { get; set; }
        public string? MajorDiagnoses { get; set; }
        public string? SurgeriesHosp { get; set; }
        public string? FamilyHistory { get; set; }
        public string? HealthEventImpact { get; set; }
    }

    public class MiddleHealthAdditional
    {
        public bool CriticalIllness { get; set; }
        public bool Accident { get; set; }
        public bool HospitalIndemnity { get; set; }
        public bool ShortTermDI { get; set; }
        public bool LongTermDI { get; set; }
        public string? Other { get; set; }
    }

    public class MiddleLife
    {
        public string? PrimaryPurpose { get; set; }

        public List<MiddleLifePolicy> Applicant { get; set; } = new();
        public List<MiddleLifePolicy> Spouse { get; set; } = new();

        public string? LastReview { get; set; }
        public string? WhatBreaksFirst { get; set; }
    }

    public class MiddleLifePolicy
    {
        public string? Type { get; set; }
        public string? Company { get; set; }
        public decimal? Face { get; set; }
        public decimal? Premium { get; set; }
        public string? Duration { get; set; }
        public string? Beneficiary { get; set; }
        public decimal? CashValue { get; set; }
        public string? Riders { get; set; }
    }

    public class MiddleDI
    {
        public string? IfCannotWorkImpact { get; set; }
        public string? GroupDI { get; set; }
        public string? IndividualDI { get; set; }

        public string? BenefitAmount { get; set; }
        public string? Elimination { get; set; }
        public string? BenefitPeriod { get; set; }

        public string? EmergencyFundMonths { get; set; }
    }

    public class MiddleLiability
    {
        public string? Umbrella { get; set; }
        public string? Exposures { get; set; }
        public string? ClaimsHistory { get; set; }
    }

    public class MiddleCashflow
    {
        public string? BiggestFrustration { get; set; }

        public decimal? NetIncome { get; set; }
        public decimal? CoreExpenses { get; set; }
        public decimal? Savings { get; set; }

        public string? Leaks { get; set; }
        public string? BudgetSystem { get; set; }

        public decimal? EmergencyFundAmount { get; set; }
        public string? EmergencyFundMonths { get; set; }

        public string? Unexpected10kPlan { get; set; }
    }

    public class MiddleDebt
    {
        public decimal? HomeValue { get; set; }
        public decimal? MortgageBalance { get; set; }
        public string? MortgageRate { get; set; }
        public decimal? MortgagePayment { get; set; }
        public int? MortgageYearsRemaining { get; set; }

        public List<MiddleOtherDebt> Other { get; set; } = new();

        public string? FeelsLike { get; set; }
        public string? ChangeOneThing { get; set; }
    }

    public class MiddleOtherDebt
    {
        public string? Type { get; set; }
        public decimal? Balance { get; set; }
        public string? Rate { get; set; }
        public decimal? Payment { get; set; }
        public string? Notes { get; set; }
    }

    public class MiddleAssets
    {
        public List<MiddleRetirementAccount> Retirement { get; set; } = new();
        public List<MiddleBrokerageAccount> Brokerage { get; set; } = new();

        public string? OrganizationFeel { get; set; }
        public string? NetWorthEstimate { get; set; }
    }

    public class MiddleRetirementAccount
    {
        public string? Type { get; set; }
        public string? Institution { get; set; }
        public decimal? Value { get; set; }
        public decimal? MonthlyContribution { get; set; }
    }

    public class MiddleBrokerageAccount
    {
        public string? Institution { get; set; }
        public decimal? Value { get; set; }
        public string? Notes { get; set; }
    }

    public class MiddleRealEstateItem
    {
        public string? Type { get; set; }
        public string? Location { get; set; }
        public decimal? Value { get; set; }
        public decimal? Mortgage { get; set; }
        public string? CashFlow { get; set; }
        public string? Notes { get; set; }
    }

    public class MiddleBusiness
    {
        public string? Ownership { get; set; }
        public decimal? Value { get; set; }
        public string? Continuity { get; set; }
        public string? BuySellKeyPersonSuccession { get; set; }
    }

    public class MiddleEquityComp
    {
        public string? HasEquityComp { get; set; }
        public string? ValueNotes { get; set; }
        public string? Strategy { get; set; }
    }

    public class MiddleTax
    {
        public string? FeelsTooHighWhy { get; set; }
        public string? Preparer { get; set; }
        public string? ProactivePlanningLast12Mo { get; set; }
        public string? OptimizeMost { get; set; }
    }

    public class MiddleCollege
    {
        public string? PlanToFund { get; set; }
        public string? CurrentAccounts { get; set; }
        public string? PlanIfHigher { get; set; }
    }

    public class MiddleLegacy
    {
        public string? HasWill { get; set; }
        public string? HasTrust { get; set; }
        public string? HasPOAFinancial { get; set; }
        public string? HasHealthDirective { get; set; }
        public string? LastUpdated { get; set; }
        public string? FamilyWouldFindEverything { get; set; }
    }

    // =====================================================================
    // YOUNGER (matches Views/FactFinder/Younger.cshtml exactly)
    // =====================================================================
    public class YoungFactFinder
    {
        public YoungPerson Person { get; set; } = new YoungPerson();
        public YoungDirection Direction { get; set; } = new YoungDirection();
        public YoungOwnership Ownership { get; set; } = new YoungOwnership();

        public YoungWork Work { get; set; } = new YoungWork();
        public YoungBenefits Benefits { get; set; } = new YoungBenefits();

        public YoungCashflow Cashflow { get; set; } = new YoungCashflow();
        public YoungBanking Banking { get; set; } = new YoungBanking();
        public YoungEmergency Emergency { get; set; } = new YoungEmergency();
        public YoungHabits Habits { get; set; } = new YoungHabits();

        // Repeater: name="Young.Debt[i].*"
        public List<YoungDebtItem> Debt { get; set; } = new List<YoungDebtItem>();

        // Narrative fields: asp-for="Young.DebtNarrative.*"
        public YoungDebtNarrative DebtNarrative { get; set; } = new YoungDebtNarrative();

        public YoungCredit Credit { get; set; } = new YoungCredit();
        public YoungHousing Housing { get; set; } = new YoungHousing();

        // asp-for="Young.LifeTransitions"
        public string? LifeTransitions { get; set; }

        public YoungLifestyle Lifestyle { get; set; } = new YoungLifestyle();
        public YoungHealth Health { get; set; } = new YoungHealth();
        public YoungProtection Protection { get; set; } = new YoungProtection();

        public YoungSavings Savings { get; set; } = new YoungSavings();
        public YoungInvesting Investing { get; set; } = new YoungInvesting();

        // IMPORTANT: bool + [Required] is not enough (false is "valid").
        // This forces it to be checked (true) to pass server-side validation.
        [Range(typeof(bool), "true", "true", ErrorMessage = "Disclaimer acknowledgment is required.")]
        public bool AcknowledgedDisclaimer { get; set; }
    }

    public class YoungPerson
    {
        [Required(ErrorMessage = "Full Name is required.")]
        public string? Name { get; set; }

        [Required(ErrorMessage = "Date of Birth is required.")]
        public DateTime? DOB { get; set; }

        public string? Location { get; set; }
        public string? RelationshipStatus { get; set; }

        public string? HasDependents { get; set; }
        public int? DependentsCount { get; set; }
    }

    public class YoungDirection
    {
        public string? ThreeToFiveYears { get; set; }
        public string? TenYears { get; set; }
        public string? CurrentStress { get; set; }
        public string? FearIfNoChange { get; set; }
        public string? OneThingFirst { get; set; }
    }

    public class YoungOwnership
    {
        public string? StabilityMeaning { get; set; }
        public string? WhatsStoppedYou { get; set; }
        public string? WorthIt { get; set; }

        public string? CommitmentScale { get; set; }
        public string? First30Days { get; set; }
    }

    public class YoungWork
    {
        public string? Role { get; set; }
        public string? Employer { get; set; }
        public string? Tenure { get; set; }

        public string? IncomeType { get; set; }
        public decimal? AnnualIncome { get; set; }
        public string? IncomeStability { get; set; }

        public string? ObstacleToIncreaseIncome { get; set; }
        public string? PathExploration { get; set; }
    }

    public class YoungBenefits
    {
        public bool Health { get; set; }
        public bool Dental { get; set; }
        public bool Vision { get; set; }
        public bool RetirementPlan { get; set; }
        public bool HSA { get; set; }
        public bool GroupLife { get; set; }
        public bool GroupDI { get; set; }

        public string? UnderstandBenefits { get; set; }
    }

    public class YoungCashflow
    {
        public string? SystemLevel { get; set; }
        public string? OffTrack { get; set; }
        public string? SystemDesiredOutcome { get; set; }
        public string? SpendingWeakness { get; set; }

        public decimal? NetIncome { get; set; }
        public decimal? FixedBills { get; set; }
        public decimal? VariableSpending { get; set; }

        public decimal? MonthlySavings { get; set; }
        public decimal? DebtPayments { get; set; }
        public string? EndOfMonth { get; set; }
    }

    public class YoungBanking
    {
        public string? KnowWhereMoneyGoes { get; set; }
        public int? NumAccounts { get; set; }
    }

    public class YoungEmergency
    {
        public decimal? Amount { get; set; }
        public string? Months { get; set; }

        public string? PlanIfIncomeStops { get; set; }
        public string? UsesCreditToSurvive { get; set; }
        public string? Unexpected1k { get; set; }
    }

    public class YoungHabits
    {
        public string? NeedButNotLocked { get; set; }
    }

    public class YoungDebtItem
    {
        public string? Type { get; set; }
        public decimal? Balance { get; set; }
        public string? Rate { get; set; }
        public decimal? Payment { get; set; }
        public string? Notes { get; set; }
    }

    public class YoungDebtNarrative
    {
        public string? PayoffPlan { get; set; }
        public string? MostStressful { get; set; }
    }

    public class YoungCredit
    {
        public string? KnowsScoreRange { get; set; }
        public string? LatePayments { get; set; }
        public string? NextMajorPurchase { get; set; }
    }

    public class YoungHousing
    {
        public string? PlanBuyHome { get; set; }
        public string? TimelineAndRange { get; set; }
    }

    public class YoungLifestyle
    {
        public string? ProtectFromStress { get; set; }
        public string? Influences { get; set; }
    }

    public class YoungHealth
    {
        public string? Coverage { get; set; }
        public string? Deductible { get; set; }
        public string? OOPMax { get; set; }

        public string? MedicalBillsSetback { get; set; }
        public string? HowPayBillsIfHealthEvent { get; set; }
    }

    public class YoungProtection
    {
        public string? HasLife { get; set; }
        public string? LifeAmount { get; set; }
        public string? WhoImpactedIfDie { get; set; }

        public string? HasDI { get; set; }
        public string? IfCannotWork3Mo { get; set; }

        public string? BeliefAboutInsurance { get; set; }
        public string? ProtectFirst { get; set; }
    }

    public class YoungSavings
    {
        public string? SavesConsistently { get; set; }
        public string? Where { get; set; }
        public decimal? TotalSaved { get; set; }
    }

    public class YoungInvesting
    {
        public string? CurrentInvesting { get; set; }
        public string? Mistakes { get; set; }
        public string? FeelBehindOrOnTrack { get; set; }
    }

        // =====================================================================
    // SENIOR (FULL — matches Senior.cshtml EXACTLY)
    // IMPORTANT: repeaters in the cshtml use the "Index" binder pattern,
    // so DO NOT prepopulate fixed rows here (it creates extra blank rows).
    // =====================================================================
    public class SeniorFactFinder
    {
        public SeniorApplicant Applicant { get; set; } = new SeniorApplicant();
        public SeniorSpouse Spouse { get; set; } = new SeniorSpouse();

        // Repeater: Senior.Children.Index + Senior.Children[KEY].*
        public List<SeniorChild> Children { get; set; } = new List<SeniorChild>();

        public string? AgesOfGrandchildren { get; set; }

        public SeniorEmergencyContact EmergencyContact { get; set; } = new SeniorEmergencyContact();

        public string? SourceOfVisit { get; set; }
        public string? Agents { get; set; }
        public DateTime? DateOfAppointment { get; set; }

        public SeniorMedical Medical { get; set; } = new SeniorMedical();
        public SeniorExtendedCare ExtendedCare { get; set; } = new SeniorExtendedCare();
        public SeniorLifeInsurance LifeInsurance { get; set; } = new SeniorLifeInsurance();
        public SeniorRetirement Retirement { get; set; } = new SeniorRetirement();

        [Required(ErrorMessage = "Disclaimer acknowledgment is required.")]
        public bool AcknowledgedDisclaimer { get; set; }
    }

    public class SeniorApplicant
    {
        // ✅ MUST match cshtml: Senior.Applicant.ApplicantName
        [Required(ErrorMessage = "Applicant Name is required.")]
        public string? ApplicantName { get; set; }

        public DateTime? DateOfBirth { get; set; }
        public string? Occupation { get; set; }

        public bool IsRetired { get; set; }
        public int? RetiredYear { get; set; }

        public bool Benefit_Pension { get; set; }
        public bool Benefit_HealthPlan { get; set; }
        public bool Benefit_Other { get; set; }
        public string? BenefitOtherText { get; set; }

        public string? Email { get; set; }
        public string? Phone { get; set; }

        public string? Address { get; set; }
        public string? City { get; set; }
        public string? State { get; set; }
        public string? Zip { get; set; }
    }

    public class SeniorSpouse
    {
        public string? SpouseName { get; set; }
        public DateTime? DateOfBirth { get; set; }
        public string? Occupation { get; set; }

        public bool IsRetired { get; set; }
        public int? RetiredYear { get; set; }

        public bool Benefit_Pension { get; set; }
        public bool Benefit_HealthPlan { get; set; }
        public bool Benefit_Other { get; set; }
        public string? BenefitOtherText { get; set; }

        public string? Email { get; set; }
        public string? Phone { get; set; }
    }

    public class SeniorChild
    {
        public string? ChildName { get; set; }
        public int? Age { get; set; }
        public string? City { get; set; }
    }

    public class SeniorEmergencyContact
    {
        public string? Name { get; set; }
        public string? Phone { get; set; }
        public string? Email { get; set; }
    }

    // -------------------------
    // Senior: Medical
    // -------------------------
    public class SeniorMedical
    {
        // Q1
        public string? ProtectYourselfPlan { get; set; }

        // Inventory tables
        public SeniorMedicalCoverage ApplicantCoverage { get; set; } = new SeniorMedicalCoverage();
        public SeniorMedicalCoverage SpouseCoverage { get; set; } = new SeniorMedicalCoverage();

        // Q2–Q6
        public string? HealthLastThreeYears { get; set; }
        public string? CurrentMedications { get; set; }
        public bool FamilyHistoryCancerStrokeHeart { get; set; }
        public string? FamilyHistoryImpact { get; set; }
        public string? ChangeAboutPresentCoverage { get; set; }
        public string? StrategyCoverOutsidePlansCoverage { get; set; }
        public bool WouldLikeLearnAvoidOutOfPocket { get; set; }

        // Parents blocks (rendered with @for i < Count)
        public List<SeniorParentInfo> ApplicantParents { get; set; } = new List<SeniorParentInfo>
        {
            new SeniorParentInfo { Label = "Applicant Father" },
            new SeniorParentInfo { Label = "Applicant Mother" }
        };

        public List<SeniorParentInfo> SpouseParents { get; set; } = new List<SeniorParentInfo>
        {
            new SeniorParentInfo { Label = "Spouse Father" },
            new SeniorParentInfo { Label = "Spouse Mother" }
        };
    }

    public class SeniorMedicalCoverage
    {
        // Coverage checkboxes
        public bool None { get; set; }
        public bool OriginalMedicare { get; set; }
        public bool Medicaid { get; set; }
        public bool Group { get; set; }
        public bool MedSupp { get; set; }
        public bool MA { get; set; }
        public bool HIP_CI { get; set; }

        // "Other" text field in your cshtml (not a checkbox)
        public string? Other { get; set; }

        public string? CompanyName { get; set; }
        public string? Plan { get; set; }
        public decimal? Premium { get; set; }

        // Select has blank option => bool?
        public bool? DrugCoverage { get; set; }
        public string? ProviderPCP { get; set; }

        // Additional benefits
        public bool Add_Dental { get; set; }
        public bool Add_Vision { get; set; }
        public bool Add_CriticalIllness { get; set; }
        public bool Add_Other { get; set; }
        public string? AddOtherText { get; set; }
    }

    public class SeniorParentInfo
    {
        public string? Label { get; set; }
        public int? Age { get; set; }
        public string? CauseOfDeathOrAgeAtDeath { get; set; }
    }

    // -------------------------
    // Senior: Extended Care
    // -------------------------
    public class SeniorExtendedCare
    {
        public bool HasExtendedCareCoverage { get; set; }

        // If coverage exists (policy details block)
        public SeniorExtendedCarePolicy CurrentPolicy { get; set; } = new SeniorExtendedCarePolicy();

        // Q8–Q13
        public bool LookedIntoIt { get; set; }
        public string? WhyNotImportant { get; set; }
        public string? WhatPreventedMovingForward { get; set; }

        public string? KnowSomeoneNeededCare { get; set; }
        public string? FinanciallyImpactedStory { get; set; }

        public string? BiggestConcernChoice { get; set; }
        public string? BiggestConcernWhy { get; set; }

        public string? ChildrenInvolvementView { get; set; }
        public string? FamilyConversationsAgingInPlace { get; set; }
    }

    public class SeniorExtendedCarePolicy
    {
        public string? BenefitsCovered { get; set; }
        public string? BenefitPeriod { get; set; }
        public string? EliminationPeriod { get; set; }
        public decimal? Premium { get; set; }
        public string? Company { get; set; }
        public decimal? BenefitAmount { get; set; }
        public string? InflationProtection { get; set; }
    }

    // -------------------------
    // Senior: Life Insurance
    // -------------------------
    public class SeniorLifeInsurance
    {
        // Q14
        public string? PrimaryPurposeOfCurrentLifeInsurance { get; set; }

        // Repeaters: Senior.LifeInsurance.*Policies.Index + [KEY].*
        public List<SeniorLifePolicy> ApplicantPolicies { get; set; } = new List<SeniorLifePolicy>();
        public List<SeniorLifePolicy> SpousePolicies { get; set; } = new List<SeniorLifePolicy>();

        // Q15–Q18
        public string? WhyChoseType { get; set; }
        public string? HowChoseBenefitAmount { get; set; }

        public string? WhenLastReviewed { get; set; }
        public bool HasWillOrTrust { get; set; }

        public bool AwareHowSocialSecurityWorksWhenOneSpousePasses { get; set; }
        public string? PlanningToCoverFutureSSReduction { get; set; }

        public bool PlanningToLeaveIRAtoFamily { get; set; }
    }

    public class SeniorLifePolicy
    {
        public string? Label { get; set; }
        public decimal? FaceAmount { get; set; }
        public string? Company { get; set; }
        public decimal? Premium { get; set; }
        public string? Type { get; set; }
        public string? PrimaryBeneficiary { get; set; }
        public decimal? CashValue { get; set; }
        public decimal? SurrenderValue { get; set; }
    }

    // -------------------------
    // Senior: Retirement + Assets
    // -------------------------
    public class SeniorRetirement
    {
        // Income tables (monthly)
        public SeniorIncomeGroup ApplicantIncome { get; set; } = new SeniorIncomeGroup();
        public SeniorIncomeGroup SpouseIncome { get; set; } = new SeniorIncomeGroup();

        // Social Security / Pension details
        public bool ReceiveSocialSecurity { get; set; }
        public decimal? SocialSecurityMonthlyAmount { get; set; }

        public decimal? CompanyPensionMonthlyAmount { get; set; }
        public bool PensionHasSurvivorBenefitsForSpouse { get; set; }

        // Priorities/questions (Step 4)
        public string? OutlivingMoneyConcerns { get; set; }

        public bool StillPayingIncomeTax { get; set; }
        public string? PriorityIncreaseIncomeOrLowerTaxesOrBoth { get; set; }

        public string? MonthlyExpensesNotes { get; set; }
        public string? WhatChangeInFinancialPlan { get; set; }

        public string? GoalsForThisMoney { get; set; }
        public string? RiskComfortLevel { get; set; }
        public string? BiggestConcern_GrowthIncomeSafety { get; set; }

        public string? FeelAboutRecentPerformance { get; set; }
        public string? FeelAboutServiceReceived { get; set; }

        public string? UpdatedOnSecureActImpact { get; set; }
        public string? StoryBehindAssetsInheritance { get; set; }

        public string? OutcomesAndFollowUpNotes { get; set; }

        // Assets (numbers)
        public SeniorNonLiquidAssets NonLiquidAssets { get; set; } = new SeniorNonLiquidAssets();
        public SeniorLiquidAssets LiquidAssets { get; set; } = new SeniorLiquidAssets();

        public bool AwareHowRMDsWork { get; set; }

        // Repeaters wired in JS: seniorCDsRepeater / seniorAnnuitiesRepeater / seniorOtherAssetsRepeater
        // (Use Index binder pattern in cshtml)
        public List<SeniorCDHolding> CDs { get; set; } = new List<SeniorCDHolding>();
        public List<SeniorAnnuityIraHolding> AnnuitiesIRAs { get; set; } = new List<SeniorAnnuityIraHolding>();
        public List<SeniorOtherAsset> OtherAssets { get; set; } = new List<SeniorOtherAsset>();

        // Kept from your model (commonly used in senior intake)
        public Senior401kGroup K401 { get; set; } = new Senior401kGroup();
    }

    public class SeniorIncomeGroup
    {
        public decimal? SS { get; set; }
        public decimal? Pension { get; set; }
        public decimal? Employment { get; set; }
        public decimal? RealEstate { get; set; }

        public decimal? Investment { get; set; }
        public decimal? RMD { get; set; }
        public decimal? Other { get; set; }
        public decimal? Total { get; set; }
    }

    public class SeniorNonLiquidAssets
    {
        public decimal? NonQualifiedAnnuities { get; set; }
        public decimal? LifeInsuranceCashValue { get; set; }
        public decimal? QualifiedIRAsAndAnnuities { get; set; }
        public decimal? OtherInvestments_CDs { get; set; }
        public decimal? RealEstateExcludingPrimaryResidence { get; set; }
        public decimal? ValuePrimaryResidence { get; set; }
    }

    public class SeniorLiquidAssets
    {
        public decimal? Checking { get; set; }
        public decimal? Savings { get; set; }
        public decimal? MoneyMarkets { get; set; }
        public decimal? MutualFunds { get; set; }
        public decimal? StocksBondsOrOther { get; set; }
    }

    public class SeniorCDHolding
    {
        public string? BankName { get; set; }
        public decimal? Value { get; set; }
        public decimal? InterestRate { get; set; }
        public DateTime? MaturityDate { get; set; }
        public string? Penalty { get; set; }
    }

    public class SeniorAnnuityIraHolding
    {
        public string? Company { get; set; }
        public string? Type { get; set; }
        public decimal? Value { get; set; }
        public decimal? InterestRate { get; set; }
        public DateTime? ContractDate { get; set; }
        public DateTime? PenaltyExpirationDate { get; set; }
    }

    public class Senior401kGroup
    {
        public string? ApplicantCompany { get; set; }
        public decimal? ApplicantValue { get; set; }

        public string? SpouseCompany { get; set; }
        public decimal? SpouseValue { get; set; }
    }

    public class SeniorOtherAsset
    {
        public string? Type { get; set; }
        public decimal? Value { get; set; }
        public string? AdditionalInformation { get; set; }
    }
}