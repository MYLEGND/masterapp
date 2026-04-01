using System.ComponentModel.DataAnnotations;

namespace Protect_Website.Models
{
    public class RiskAssessmentModel
    {
        // STEP TRACKER (keeps user on same step if server validation fails)
        public int CurrentStep { get; set; } = 1;

        // ---------------- PERSONAL INFO ----------------
        [Required(ErrorMessage = "First Name is required.")]
        public string FirstName { get; set; } = "";

        [Required(ErrorMessage = "Last Name is required.")]
        public string LastName { get; set; } = "";

        [Required(ErrorMessage = "Email is required.")]
        [EmailAddress(ErrorMessage = "Enter a valid email address.")]
        public string Email { get; set; } = "";

        public string? PhoneNumber { get; set; }
        public string? State { get; set; }
        public string? Occupation { get; set; }
        public int? Age { get; set; }
        public string? MaritalStatus { get; set; }
        public int? HouseholdSize { get; set; }

        // ---------------- INCOME & WORK ----------------
        public decimal? AnnualIncome { get; set; }
        public int? RetirementAgeTarget { get; set; }
        public int? WorkingYearsLeft { get; set; }
        public string? SelfEmployed { get; set; }
        public string? EmployerBenefits { get; set; }

        // ---------------- CASH FLOW ----------------
        public decimal? MonthlyIncome { get; set; }
        public decimal? OtherIncome { get; set; }
        public decimal? MonthlyDebt { get; set; }
        public decimal? MortgagePayment { get; set; }
        public decimal? MonthlyExpenses { get; set; }
        public decimal? Taxes { get; set; }

        public decimal? EmergencySavings { get; set; }
        public decimal? CheckingAccount { get; set; }
        public decimal? SavingsAccount { get; set; }

        public decimal? RothIRA { get; set; }
        public decimal? TraditionalIRA { get; set; }
        public decimal? _401k { get; set; }
        public decimal? BrokerageAccount { get; set; }
        public decimal? HSA { get; set; }
        public decimal? OtherPropertyAssets { get; set; }

        public decimal? BusinessOwnershipValue { get; set; }
        public decimal? RealEstateValue { get; set; }
        public decimal? RentalPropertyValue { get; set; }

        public decimal? VehicleValue { get; set; }
        public decimal? CollectiblesValue { get; set; }

        public decimal? MortgageBalance { get; set; }
        public decimal? StudentLoans { get; set; }
        public decimal? OtherLiabilities { get; set; }

        // Net Monthly Cash Flow
        public decimal? NetCashFlow
        {
            get
            {
                decimal income = (MonthlyIncome ?? 0) + (OtherIncome ?? 0);
                decimal outflows = (MonthlyExpenses ?? 0) + (MonthlyDebt ?? 0) + (MortgagePayment ?? 0) + (Taxes ?? 0);
                return income - outflows;
            }
        }

        // ---------------- ESTATE ----------------
        public string? HasWill { get; set; }
        public string? HasTrust { get; set; }
        public string? HasPOA { get; set; }
        public string? HasHealthDirective { get; set; }

        // ---------------- LIFE ----------------
        [Required(ErrorMessage = "Please select Yes or No for Life Insurance.")]
        public string HasLifeInsurance { get; set; } = "";

        public decimal? LifeCoverageIndividual { get; set; }
        public decimal? LifeCoverageGroup { get; set; }
        public string? PrimaryBeneficiaries { get; set; }
        public string? SecondaryBeneficiaries { get; set; }

        // ---------------- DISABILITY ----------------
        [Required(ErrorMessage = "Please select Yes or No for Disability Insurance.")]
        public string HasDI { get; set; } = "";

        public decimal? DIBenefitMonthly { get; set; }
        public int? DIWaitingPeriod { get; set; }
        public string? DIBenefitPeriod { get; set; }

        // ---------------- HEALTH ----------------
        public string? HealthCoverageType { get; set; }
        public decimal? HealthDeductible { get; set; }
        public decimal? HealthOutOfPocketMax { get; set; }

        // ---------------- PROPERTY & LIABILITY ----------------
        public string? HasHomeInsurance { get; set; }
        public decimal? HomeCoverageLimit { get; set; }
        public string? HasAutoInsurance { get; set; }
        public decimal? AutoCoverageLimit { get; set; }
        public string? HasGeneralLiability { get; set; }
        public decimal? GeneralLiabilityLimit { get; set; }
        public string? HasProfessionalLiability { get; set; }
        public decimal? ProfessionalLiabilityLimit { get; set; }

                        [Display(Name = "Acknowledged Disclaimer")]
        [Required(ErrorMessage = "You must acknowledge the disclaimer.")]
        public bool AcknowledgedDisclaimer { get; set; } = false;
    }
}
