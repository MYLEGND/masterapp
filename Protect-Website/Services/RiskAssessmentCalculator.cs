using System;
using Protect_Website.Models;

namespace Protect_Website.Services
{
    public static class RiskAssessmentCalculator
    {
        public static RiskAssessmentResult Calculate(RiskAssessmentModel model)
        {
            // ---------- helpers ----------
            static decimal D(decimal? v) => v ?? 0m;
            static decimal Clamp(decimal v, decimal min, decimal max)
                => v < min ? min : (v > max ? max : v);

            static decimal SafeDiv(decimal num, decimal den)
                => den == 0 ? 0 : num / den;

            static decimal ToMonthlyFromAnnual(decimal annual) => annual / 12m;

            static decimal ScoreFromRatio(decimal ratio, decimal goodAtOrAbove, decimal badAtOrBelow)
            {
                // ratio >= good => 100
                // ratio <= bad  => 0
                if (ratio >= goodAtOrAbove) return 100m;
                if (ratio <= badAtOrBelow) return 0m;

                // linear interpolation between bad and good
                var t = (ratio - badAtOrBelow) / (goodAtOrAbove - badAtOrBelow);
                return Clamp(t * 100m, 0m, 100m);
            }

            static decimal ScoreFromLowerIsBetter(decimal value, decimal goodAtOrBelow, decimal badAtOrAbove)
            {
                // value <= good => 100
                // value >= bad  => 0
                if (value <= goodAtOrBelow) return 100m;
                if (value >= badAtOrAbove) return 0m;

                // invert linear
                var t = 1m - ((value - goodAtOrBelow) / (badAtOrAbove - goodAtOrBelow));
                return Clamp(t * 100m, 0m, 100m);
            }

            // ---------- base numbers ----------
            // Monthly inflows
            decimal monthlyIncome = D(model.MonthlyIncome);
            decimal otherIncome = D(model.OtherIncome);
            decimal totalMonthlyIncome = monthlyIncome + otherIncome;

            // Monthly outflows
            decimal monthlyExpenses = D(model.MonthlyExpenses);
            decimal monthlyDebt = D(model.MonthlyDebt);
            decimal mortgagePayment = D(model.MortgagePayment);
            decimal taxes = D(model.Taxes);

            decimal totalMonthlyOutflows = monthlyExpenses + monthlyDebt + mortgagePayment + taxes;

            // ✅ Correct Net Cash Flow
            decimal netCashFlow = totalMonthlyIncome - totalMonthlyOutflows;

            // Annual income normalization (used for coverage ratios / affordability)
            // If user didn’t enter AnnualIncome, derive from monthly income.
            decimal annualIncome = D(model.AnnualIncome);
            if (annualIncome <= 0m && totalMonthlyIncome > 0m)
                annualIncome = totalMonthlyIncome * 12m;

            decimal monthlyIncomeFromAnnual = annualIncome > 0m ? ToMonthlyFromAnnual(annualIncome) : totalMonthlyIncome;

            // Assets
            decimal liquidAssets = D(model.CheckingAccount) + D(model.SavingsAccount) + D(model.EmergencySavings);
            decimal investAssets =
                D(model.RothIRA) + D(model.TraditionalIRA) + D(model._401k) + D(model.BrokerageAccount) + D(model.HSA);

            decimal propertyAssets =
                D(model.BusinessOwnershipValue) + D(model.RealEstateValue) + D(model.RentalPropertyValue) + D(model.OtherPropertyAssets);

            decimal otherAssets = D(model.VehicleValue) + D(model.CollectiblesValue);

            decimal totalAssets = liquidAssets + investAssets + propertyAssets + otherAssets;

            // Liabilities (balances, not monthly payments)
            decimal totalLiabilities = D(model.MortgageBalance) + D(model.StudentLoans) + D(model.OtherLiabilities);

            // ---------- key metrics ----------
            // Cash flow metrics
            decimal savingsRate = SafeDiv(netCashFlow, totalMonthlyIncome);         // net / income
            decimal debtToIncomeMonthly = SafeDiv(monthlyDebt + mortgagePayment, totalMonthlyIncome); // payment DTI
            decimal essentialBurnRate = monthlyExpenses + monthlyDebt + mortgagePayment + taxes;
            decimal emergencyFundMonths = SafeDiv(liquidAssets, essentialBurnRate);

            // Coverage adequacy targets (simple, practical baseline)
            // Life: 10x annual income as “good” baseline for breadwinner (you can tune later).
            decimal lifeTarget = annualIncome * 10m;

            decimal lifeCoverageTotal = D(model.LifeCoverageIndividual) + D(model.LifeCoverageGroup);
            decimal lifeCoverageRatio = SafeDiv(lifeCoverageTotal, lifeTarget);

            // Disability: target ~60% of gross monthly income replacement
            decimal diTargetMonthly = monthlyIncomeFromAnnual * 0.60m;
            decimal diMonthlyBenefit = D(model.DIBenefitMonthly);
            decimal diCoverageRatio = SafeDiv(diMonthlyBenefit, diTargetMonthly);

            // Estate completion
            int estateDocs = 0;
            if (string.Equals(model.HasWill, "Yes", StringComparison.OrdinalIgnoreCase)) estateDocs++;
            if (string.Equals(model.HasTrust, "Yes", StringComparison.OrdinalIgnoreCase)) estateDocs++;
            if (string.Equals(model.HasPOA, "Yes", StringComparison.OrdinalIgnoreCase)) estateDocs++;
            if (string.Equals(model.HasHealthDirective, "Yes", StringComparison.OrdinalIgnoreCase)) estateDocs++;
            decimal estateCompletionRatio = estateDocs / 4m;

            // Property/protection completion (basic yes/no coverage check)
            int protectionItems = 0;
            int protectionYes = 0;

            void AddProt(string? yesNo)
            {
                protectionItems++;
                if (string.Equals(yesNo, "Yes", StringComparison.OrdinalIgnoreCase)) protectionYes++;
            }

            AddProt(model.HasHomeInsurance);
            AddProt(model.HasAutoInsurance);
            AddProt(model.HasGeneralLiability);
            AddProt(model.HasProfessionalLiability);

            decimal protectionCompletionRatio = protectionItems == 0 ? 0m : (protectionYes / (decimal)protectionItems);

            // ---------- scoring ----------
            // Cash Flow Score:
            // - positive cash flow is good
            // - 10%+ savings rate is excellent, -10% is bad
            decimal cashFlowScore = ScoreFromRatio(savingsRate, goodAtOrAbove: 0.10m, badAtOrBelow: -0.10m);

            // Emergency Fund Score:
            // - 6 months+ is great
            // - 0 months is bad
            decimal emergencyScore = ScoreFromRatio(emergencyFundMonths, goodAtOrAbove: 6m, badAtOrBelow: 0m);

            // Debt Score:
            // - payment DTI <= 25% is strong
            // - >= 50% is weak
            decimal debtScore = ScoreFromLowerIsBetter(debtToIncomeMonthly, goodAtOrBelow: 0.25m, badAtOrAbove: 0.50m);

            // Estate Score:
            // - 4/4 docs -> 100, 0/4 -> 0
            decimal estateScore = Clamp(estateCompletionRatio * 100m, 0m, 100m);

            // Life Score:
            // If user says “No life insurance”, push score low unless high assets.
            bool hasLife = string.Equals(model.HasLifeInsurance, "Yes", StringComparison.OrdinalIgnoreCase);
            decimal lifeScore;
            if (!hasLife)
            {
                // if no life, but assets are very high relative to income, don’t nuke them
                var assetsToIncome = SafeDiv(totalAssets, annualIncome);
                lifeScore = ScoreFromRatio(assetsToIncome, goodAtOrAbove: 12m, badAtOrBelow: 1m) * 0.55m; // capped lower
            }
            else
            {
                lifeScore = ScoreFromRatio(lifeCoverageRatio, goodAtOrAbove: 1.0m, badAtOrBelow: 0.15m);
            }

            // Disability Score:
            bool hasDI = string.Equals(model.HasDI, "Yes", StringComparison.OrdinalIgnoreCase);
            decimal disabilityScore;
            if (!hasDI)
            {
                // no DI generally = high risk if income is present
                disabilityScore = totalMonthlyIncome > 0 ? 10m : 55m;
            }
            else
            {
                disabilityScore = ScoreFromRatio(diCoverageRatio, goodAtOrAbove: 1.0m, badAtOrBelow: 0.15m);
            }

            // Health Score:
            // You didn’t capture health plan quality deeply. Use basic “has info” proxy for now.
            // Later you can expand: deductible vs income, OOP max vs liquid assets, plan type, etc.
            decimal healthScore = 60m;
            if (!string.IsNullOrWhiteSpace(model.HealthCoverageType)) healthScore += 15m;
            if (D(model.HealthDeductible) > 0m) healthScore += 10m;
            if (D(model.HealthOutOfPocketMax) > 0m) healthScore += 15m;
            healthScore = Clamp(healthScore, 0m, 100m);

            // Property Score:
            // Based on completion + whether limits are present when "Yes"
            decimal propertyScore = Clamp(protectionCompletionRatio * 100m, 0m, 100m);

            // Protection Score:
            // Blend: protection completion + debtScore + emergencyScore (real-world resilience)
            decimal protectionScore =
                (propertyScore * 0.40m) +
                (debtScore * 0.30m) +
                (emergencyScore * 0.30m);

            // Overall Score (weighted)
            decimal overallScore =
                (lifeScore * 0.18m) +
                (disabilityScore * 0.18m) +
                (healthScore * 0.12m) +
                (propertyScore * 0.12m) +
                (cashFlowScore * 0.18m) +
                (estateScore * 0.12m) +
                (protectionScore * 0.10m);

            overallScore = Clamp(overallScore, 0m, 100m);

            // ---------- feedback (simple, direct) ----------
            string feedback = BuildFeedback(
                netCashFlow, savingsRate, emergencyFundMonths, debtToIncomeMonthly,
                hasLife, lifeCoverageRatio, hasDI, diCoverageRatio,
                estateDocs, protectionYes, protectionItems
            );

            // ---------- return result ----------
            return new RiskAssessmentResult
            {
                // Copy inputs
                FirstName = model.FirstName ?? "",
                LastName = model.LastName ?? "",
                Email = model.Email ?? "",
                PhoneNumber = model.PhoneNumber,
                State = model.State,
                Occupation = model.Occupation,
                Age = model.Age,
                MaritalStatus = model.MaritalStatus,
                HouseholdSize = model.HouseholdSize,

                AnnualIncome = model.AnnualIncome,
                RetirementAgeTarget = model.RetirementAgeTarget,
                WorkingYearsLeft = model.WorkingYearsLeft,
                SelfEmployed = model.SelfEmployed,
                EmployerBenefits = model.EmployerBenefits,

                MonthlyIncome = model.MonthlyIncome,
                OtherIncome = model.OtherIncome,
                MonthlyDebt = model.MonthlyDebt,
                MortgagePayment = model.MortgagePayment,
                MonthlyExpenses = model.MonthlyExpenses,
                Taxes = model.Taxes,

                EmergencySavings = model.EmergencySavings,
                CheckingAccount = model.CheckingAccount,
                SavingsAccount = model.SavingsAccount,

                RothIRA = model.RothIRA,
                TraditionalIRA = model.TraditionalIRA,
                _401k = model._401k,
                BrokerageAccount = model.BrokerageAccount,
                HSA = model.HSA,

                BusinessOwnershipValue = model.BusinessOwnershipValue,
                RealEstateValue = model.RealEstateValue,
                RentalPropertyValue = model.RentalPropertyValue,
                OtherPropertyAssets = model.OtherPropertyAssets,

                VehicleValue = model.VehicleValue,
                CollectiblesValue = model.CollectiblesValue,

                MortgageBalance = model.MortgageBalance,
                StudentLoans = model.StudentLoans,
                OtherLiabilities = model.OtherLiabilities,

                HasWill = model.HasWill,
                HasTrust = model.HasTrust,
                HasPOA = model.HasPOA,
                HasHealthDirective = model.HasHealthDirective,

                HasLifeInsurance = model.HasLifeInsurance,
                LifeCoverageIndividual = model.LifeCoverageIndividual,
                LifeCoverageGroup = model.LifeCoverageGroup,
                PrimaryBeneficiaries = model.PrimaryBeneficiaries,
                SecondaryBeneficiaries = model.SecondaryBeneficiaries,

                HasDI = model.HasDI,
                DIBenefitMonthly = model.DIBenefitMonthly,
                DIWaitingPeriod = model.DIWaitingPeriod,
                DIBenefitPeriod = model.DIBenefitPeriod,

                HealthCoverageType = model.HealthCoverageType,
                HealthDeductible = model.HealthDeductible,
                HealthOutOfPocketMax = model.HealthOutOfPocketMax,

                HasHomeInsurance = model.HasHomeInsurance,
                HomeCoverageLimit = model.HomeCoverageLimit,
                HasAutoInsurance = model.HasAutoInsurance,
                AutoCoverageLimit = model.AutoCoverageLimit,
                HasGeneralLiability = model.HasGeneralLiability,
                GeneralLiabilityLimit = model.GeneralLiabilityLimit,
                HasProfessionalLiability = model.HasProfessionalLiability,
                ProfessionalLiabilityLimit = model.ProfessionalLiabilityLimit,

                // Scores
                LifeScore = lifeScore,
                DisabilityScore = disabilityScore,
                HealthScore = healthScore,
                PropertyScore = propertyScore,
                CashFlowScore = cashFlowScore,
                EstateScore = estateScore,
                ProtectionScore = protectionScore,
                OverallScore = overallScore,

                FeedbackText = feedback,

                // Calculated
                NetCashFlow = netCashFlow
            };
        }

        private static string BuildFeedback(
            decimal netCashFlow,
            decimal savingsRate,
            decimal emergencyMonths,
            decimal dtiMonthly,
            bool hasLife,
            decimal lifeCoverageRatio,
            bool hasDI,
            decimal diCoverageRatio,
            int estateDocs,
            int protectionYes,
            int protectionItems
        )
        {
            // keep it tight, practical, coach-style
            var sb = new System.Text.StringBuilder();

            // Cash flow
            if (netCashFlow < 0)
                sb.Append("Cash flow is negative — we need to tighten expenses/debt or raise income to stop financial bleed. ");
            else if (savingsRate < 0.05m)
                sb.Append("Cash flow is positive but thin — we should build a stronger monthly margin. ");
            else
                sb.Append("Cash flow is trending strong — keep building margin and automate saving. ");

            // Emergency fund
            if (emergencyMonths < 1m)
                sb.Append("Emergency fund is critically low (under 1 month). ");
            else if (emergencyMonths < 3m)
                sb.Append("Emergency fund is below target (aim for 3–6 months). ");
            else
                sb.Append("Emergency fund looks solid. ");

            // DTI
            if (dtiMonthly >= 0.50m)
                sb.Append("Debt payments are heavy relative to income — we need a payoff strategy. ");
            else if (dtiMonthly >= 0.35m)
                sb.Append("Debt ratio is moderate — we can optimize it. ");
            else
                sb.Append("Debt ratio is under control. ");

            // Life coverage
            if (!hasLife)
                sb.Append("No life coverage selected — if anyone depends on your income, that’s a major exposure. ");
            else if (lifeCoverageRatio < 0.5m)
                sb.Append("Life coverage may be under the typical baseline — we should confirm your true need. ");
            else
                sb.Append("Life coverage appears within a reasonable baseline range. ");

            // DI coverage
            if (!hasDI)
                sb.Append("No disability coverage selected — income is your #1 asset and needs protection. ");
            else if (diCoverageRatio < 0.5m)
                sb.Append("Disability benefit may be under target — we should tighten income protection. ");
            else
                sb.Append("Disability coverage appears reasonably aligned. ");

            // Estate docs
            if (estateDocs <= 1)
                sb.Append("Estate planning is thin — we should get core documents in place. ");
            else if (estateDocs <= 3)
                sb.Append("Estate planning is partially built — we can complete the set. ");
            else
                sb.Append("Estate planning is strong. ");

            // Protection completion
            var protectionRatio = protectionItems == 0 ? 0m : (protectionYes / (decimal)protectionItems);
            if (protectionRatio < 0.50m)
                sb.Append("Property/liability protection looks incomplete — we should close gaps fast.");
            else
                sb.Append("Property/liability protection looks reasonably structured.");

            return sb.ToString().Trim();
        }
    }
}
