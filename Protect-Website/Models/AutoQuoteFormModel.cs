using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Protect_Website.Models
{
    public class AutoQuoteFormModel
    {
        // ===================== SECTION 0: APPLICANT INFO =====================
        [Required] public string FirstName { get; set; } = "";
        [Required] public string LastName { get; set; } = "";

        // Address State + Postal Code (from your Applicant Info layout)
        [Required] public string AddressState { get; set; } = "";  // dropdown state
        [Required] public string PostalCode { get; set; } = "";

        // Not required
        public string? Nickname { get; set; } = "";

        [Required] public string Gender { get; set; } = ""; // Female/Male/Not Specified
        [Required, DataType(DataType.Date)] public DateTime? DOB { get; set; }

        [Required] public string MaritalStatus { get; set; } = "";

        [Required] public string DriversLicenseNumber { get; set; } = "";
        [Required] public string DLStatus { get; set; } = "";
        [Required] public string DLState { get; set; } = ""; // dropdown state

        // Not required
        public string? Education { get; set; } = "";

        [Required] public string Industry { get; set; } = "";

        // ===================== SECTION 1: ADDRESS =====================
        // Primary Address (required per your layout)
        [Required] public string PrimaryAddress { get; set; } = "";
        public string? PrimaryUnit { get; set; } = "";
        public string? PrimaryAddressLine2 { get; set; } = "";
        [Required] public string PrimaryCity { get; set; } = "";
        [Required] public string PrimaryState { get; set; } = "";
        [Required] public string PrimaryCountry { get; set; } = "";     // dropdown, default US in UI
        [Required] public string PrimaryPostalCode { get; set; } = "";
        [Required] public string PrimaryYearsAtAddress { get; set; } = "";

        // Previous Address (ONLY when PrimaryYearsAtAddress < 3 — so these are NOT required in the model)
        public string? PreviousAddress { get; set; } = "";
        public string? PreviousUnit { get; set; } = "";
        public string? PreviousAddressLine2 { get; set; } = "";
        public string? PreviousCity { get; set; } = "";
        public string? PreviousState { get; set; } = "";
        public string? PreviousCountry { get; set; } = "";
        public string? PreviousPostalCode { get; set; } = "";
        public string? PreviousYearsAtAddress { get; set; } = "";

        // ===================== SECTION 1B: CONTACT INFO =====================
        [Required] public string PhoneType { get; set; } = "";          // Home/Work/Mobile/Fax
        [Required] public string PhoneNumber { get; set; } = "";        // actual number input

        [Required] public string EmailType { get; set; } = "";          // Primary/Secondary
        [Required] public string EmailAddress { get; set; } = "";       // actual email input

        [Required] public string PreferredContactMethod { get; set; } = ""; // Phone/Email/Text
        [Required] public string BestTimeToContact { get; set; } = "";      // Morning/Afternoon/Evening

        // ===================== SECTION 1: POLICY INFORMATION =====================
        [Required] public string PriorCarrier { get; set; } = "";
        [Required, DataType(DataType.Date)] public DateTime? PriorPolicyExpirationDate { get; set; }

        [Required] public string PriorLiabilityLimits { get; set; } = "";
        [Required] public string PriorPolicyTerm { get; set; } = "";

        // Not required
        public string? PriorPolicyPremium { get; set; } = "";

        [Required] public string YearsWithPriorCarrier { get; set; } = "";
        [Required] public string MonthsWithPriorCarrier { get; set; } = "";

        [Required] public string YearsContinuousCoverage { get; set; } = "";
        [Required] public string MonthsContinuousCoverage { get; set; } = "";

        [Required] public string CreditCheckAuthorized { get; set; } = ""; // Yes/No
        [Required] public string NewPolicyTerm { get; set; } = "";        // 6/12
        [Required] public string PackagePolicy { get; set; } = "";        // Yes/No

        [Required, DataType(DataType.Date)] public DateTime? NewPolicyEffectiveDate { get; set; }

        [Required] public string AdditionalCarrierQuestions { get; set; } = "";
        [Required] public string Paperless { get; set; } = ""; // Yes/No
        [Required] public string MultiPolicyDiscount { get; set; } = "";

        // ===================== SECTION 2: DRIVERS =====================
        [MinLength(1, ErrorMessage = "At least one driver is required.")]
        public List<Driver> Drivers { get; set; } = new();

        // ===================== SECTION 3: VEHICLES =====================
        [MinLength(1, ErrorMessage = "At least one vehicle is required.")]
        public List<Vehicle> Vehicles { get; set; } = new();

        // ===================== SECTION 4: INCIDENTS =====================
        public List<Accident> Accidents { get; set; } = new();
        public List<Violation> Violations { get; set; } = new();
        public List<CompLoss> CompLosses { get; set; } = new();

        // ===================== SECTION 5: GENERAL COVERAGE =====================
        [Required] public string BodilyInjury { get; set; } = "";
        [Required] public string UninsuredMotorist { get; set; } = "";
        [Required] public string UnderinsuredMotorist { get; set; } = "";
        [Required] public string MedicalPayments { get; set; } = "";
        [Required] public string ResidenceType { get; set; } = "";

        // ===================== DISCLAIMER / AUTH =====================
        [Range(typeof(bool), "true", "true", ErrorMessage = "You must acknowledge the authorization to submit.")]
        public bool AcknowledgedDisclaimer { get; set; }
    }

    public class Driver
    {
        [Required] public string FirstName { get; set; } = "";
        [Required] public string LastName { get; set; } = "";
        [Required, DataType(DataType.Date)] public DateTime? DOB { get; set; }

        [Required] public string Gender { get; set; } = "";
        [Required] public string MaritalStatus { get; set; } = "";

        [Required] public string OccupationIndustry { get; set; } = "";
        [Required] public string OccupationTitle { get; set; } = "";

        [Required] public string DLStatus { get; set; } = "";
        [Required] public string AgeLicensed { get; set; } = "";

        [Required] public string DLNumber { get; set; } = "";
        [Required] public string DLState { get; set; } = "";

        // not required (HTML has no required)
        [DataType(DataType.Date)] public DateTime? DefensiveDriverCourseDate { get; set; }

        [Required] public string LicenseSuspendedLast5Years { get; set; } = ""; // Yes/No

        // not required (HTML has no required)
        public string? DriverEducation { get; set; } = "";
        public string? MatureDriver { get; set; } = "";
        public string? GoodDriver { get; set; } = "";

        // Carrier questions (required in HTML)
        [Required] public string TelematicsDiscount { get; set; } = ""; // Yes/No
        [Required] public string MilitaryService { get; set; } = "";    // Yes/No
    }

    public class Vehicle
    {
        [Required] public string VIN { get; set; } = "";
        [Required] public string Year { get; set; } = "";
        [Required] public string Make { get; set; } = "";
        [Required] public string Model { get; set; } = "";
        [Required, DataType(DataType.Date)] public DateTime? PurchaseDate { get; set; }

        // not required (HTML has no required)
        public string? PassiveRestraints { get; set; } = "";
        public string? AntiTheft { get; set; } = "";

        // not required (HTML has no required)
        public string? AntiLockBrakes { get; set; } = "";
        public string? DaytimeRunningLights { get; set; } = "";

        // not required (HTML has no required)
        public string? CostNewValue { get; set; } = "";

        [Required] public string Use { get; set; } = "";
        [Required] public string AnnualMiles { get; set; } = "";
        [Required] public string Performance { get; set; } = "";

        // not required (HTML has no required)
        public string? ModificationValue { get; set; } = "";

        // not required (HTML has no required)
        public string? WasNew { get; set; } = "";

        [Required] public string OwnershipType { get; set; } = "";

        // not required (HTML has no required)
        public string? Carpool { get; set; } = "";
        public string? Telematics { get; set; } = "";
        public string? TNC { get; set; } = "";

        // Vehicle assignment required by HTML required attr
        [Range(0, int.MaxValue, ErrorMessage = "Vehicle assignment is required.")]
        public int? AssignedDriverIndex { get; set; }

        // ===== SECTION 5 PER-VEHICLE COVERAGES (required in HTML) =====
        [Required] public string Comprehensive { get; set; } = "";
        [Required] public string Collision { get; set; } = "";
        [Required] public string Towing { get; set; } = "";
        [Required] public string Rental { get; set; } = "";

        // not required (HTML has no required)
        public string? LoanLease { get; set; } = "";

        // not required (HTML has no required)
        public string? Liability { get; set; } = "";

        // Carrier questions (required in HTML)
        [Required] public string SpecialEquipment { get; set; } = "";
        [Required] public string BrandedTitle { get; set; } = ""; // Yes/No

        // not required (HTML has no required)
        public string? CustomEquipment { get; set; } = "";
    }

    public class Accident
    {
        // not required (HTML has no required)
        public DateTime? Date { get; set; }

        // not required (HTML has no required)
        public int? DriverIndex { get; set; }

        // not required (HTML has no required)
        public string? Description { get; set; } = "";

        // not required (HTML has no required)
        public string? PropertyDamageAmount { get; set; } = "";
        public string? BodilyInjuryAmount { get; set; } = "";
        public string? CollisionAmount { get; set; } = "";
        public string? MedicalPaymentAmount { get; set; } = "";

        // not required (HTML has no required)
        public int? VehicleIndex { get; set; }

        // not required (HTML has no required)
        public string? VehicleInvolvedText { get; set; } = "";
    }

    public class Violation
    {
        // not required (HTML has no required)
        public DateTime? Date { get; set; }

        // not required (HTML has no required)
        public int? DriverIndex { get; set; }

        // not required (HTML has no required)
        public string? Description { get; set; } = "";
    }

    public class CompLoss
    {
        // not required (HTML has no required)
        public DateTime? Date { get; set; }

        // not required (HTML has no required)
        public int? DriverIndex { get; set; }

        // not required (HTML has no required)
        public string? LossDescription { get; set; } = "";
    }
}
