namespace Protect_Website.Models
{
    public class RiskAssessmentResult
    {
        // ---------------- PERSONAL INFO ----------------
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
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

        // ---------------- CASH FLOW INPUTS ----------------
        public decimal? MonthlyIncome { get; set; }
        public decimal? OtherIncome { get; set; }
        public decimal? MonthlyDebt { get; set; }
        public decimal? MortgagePayment { get; set; }
        public decimal? MonthlyExpenses { get; set; }
        public decimal? Taxes { get; set; }

        // Liquid Assets
        public decimal? EmergencySavings { get; set; }
        public decimal? CheckingAccount { get; set; }
        public decimal? SavingsAccount { get; set; }

        // Investment Accounts
        public decimal? RothIRA { get; set; }
        public decimal? TraditionalIRA { get; set; }
        public decimal? _401k { get; set; }
        public decimal? BrokerageAccount { get; set; }
        public decimal? HSA { get; set; }

        // Business & Property Interests
        public decimal? BusinessOwnershipValue { get; set; }
        public decimal? RealEstateValue { get; set; }
        public decimal? RentalPropertyValue { get; set; }
        public decimal? OtherPropertyAssets { get; set; }

        // Other Assets
        public decimal? VehicleValue { get; set; }
        public decimal? CollectiblesValue { get; set; }

        // Liabilities beyond monthly debt
        public decimal? MortgageBalance { get; set; }
        public decimal? StudentLoans { get; set; }
        public decimal? OtherLiabilities { get; set; }

        // ---------------- ESTATE ----------------
        public string? HasWill { get; set; }
        public string? HasTrust { get; set; }
        public string? HasPOA { get; set; }
        public string? HasHealthDirective { get; set; }

        // ---------------- LIFE ----------------
        public string? HasLifeInsurance { get; set; }
        public decimal? LifeCoverageIndividual { get; set; }
        public decimal? LifeCoverageGroup { get; set; }
        public string? PrimaryBeneficiaries { get; set; }
        public string? SecondaryBeneficiaries { get; set; }

        // ---------------- DISABILITY ----------------
        public string? HasDI { get; set; }
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

        // ---------------- SCORES ----------------
        public decimal LifeScore { get; set; }
        public decimal DisabilityScore { get; set; }
        public decimal HealthScore { get; set; }
        public decimal PropertyScore { get; set; }
        public decimal CashFlowScore { get; set; }
        public decimal EstateScore { get; set; }
        public decimal ProtectionScore { get; set; }
        public decimal OverallScore { get; set; }

        // ---------------- FEEDBACK ----------------
        public string FeedbackText { get; set; } = string.Empty;

        // ---------------- CALCULATOR OUTPUTS (MATCHES YOUR FULL CALCULATOR) ----------------

        // Totals
        public decimal TotalMonthlyIncome { get; set; }
        public decimal TotalMonthlyOutflows { get; set; }

        // Cash flow
        public decimal NetCashFlow { get; set; }           // totalMonthlyIncome - totalMonthlyOutflows
        public decimal SavingsRate { get; set; }           // netCashFlow / totalMonthlyIncome (0.10 = 10%)
        public decimal DebtToIncomeMonthly { get; set; }   // (monthlyDebt + mortgagePayment) / totalMonthlyIncome
        public decimal EmergencyFundMonths { get; set; }   // liquidAssets / essentialBurnRate

        // Coverage targets + ratios
        public decimal AnnualIncomeDerived { get; set; }   // annualIncome used by calculator (derived if needed)
        public decimal LifeTarget { get; set; }            // annualIncome * 10
        public decimal LifeCoverageTotal { get; set; }     // individual + group
        public decimal LifeCoverageRatio { get; set; }     // lifeCoverageTotal / lifeTarget

        public decimal DITargetMonthly { get; set; }       // annualIncome/12 * 0.60
        public decimal DICoverageRatio { get; set; }       // diMonthlyBenefit / diTargetMonthly

        // Completion counts
        public int EstateDocsCompleted { get; set; }       // 0-4
        public int ProtectionYesCount { get; set; }        // 0-4
        public int ProtectionItemsCount { get; set; }      // usually 4
        public decimal ProtectionCompletionRatio { get; set; } // yes/items
    }
}
