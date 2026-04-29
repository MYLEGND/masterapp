(function () {
    const TOOL_ID = "LegendLivingBalanceSheet";
    const STATUS = ["Exposed", "Partial", "Protected"];
    const WILLS_TRUSTS_PATH = "protection.willsTrusts";
    const ESTATE_PLAN_STATUSES = [
        ["NotSetUp", "Not Set Up"],
        ["BasicWill", "Basic Will"],
        ["FullEstatePlan", "Full Estate Plan"]
    ];
    const ESTATE_RISK_BY_STATUS = Object.freeze({
        NotSetUp: "High",
        BasicWill: "Moderate",
        FullEstatePlan: "Low"
    });
    const FILING_STATUSES = ["Single", "Married Filing Jointly", "Married Filing Separately", "Head of Household", "Business Owner"];
    const COMPOUND_CONTRIBUTION_CADENCES = [
        ["daily", "Daily"],
        ["weekly", "Weekly"],
        ["biweekly", "Biweekly"],
        ["monthly", "Monthly"],
        ["quarterly", "Quarterly"],
        ["yearly", "Yearly"]
    ];
    const COMPOUNDING_CADENCES = [
        ["daily", "Daily (365x)"],
        ["weekly", "Weekly (52x)"],
        ["monthly", "Monthly (12x)"],
        ["quarterly", "Quarterly (4x)"],
        ["semiannual", "Semiannual (2x)"],
        ["yearly", "Yearly (1x)"],
        ["continuous", "Continuous"]
    ];
    const COMPOUND_TIMINGS = [
        ["end", "End of period"],
        ["beginning", "Beginning of period"]
    ];
    const COMPOUND_PERIODS_PER_YEAR = Object.freeze({
        daily: 365,
        weekly: 52,
        biweekly: 26,
        monthly: 12,
        quarterly: 4,
        semiannual: 2,
        yearly: 1
    });

    const ASSET_FIELDS = [
        ["assets.personalProperty", "Personal Property"],
        ["assets.savings", "Savings"],
        ["assets.investments", "Investments"],
        ["assets.retirement", "Retirement"],
        ["assets.realEstate", "Real Estate"],
        ["assets.business", "Business"]
    ];

    const LIABILITY_FIELDS = [
        ["liabilities.shortTerm", "Short Term"],
        ["liabilities.taxes", "Taxes", "From tax profile"],
        ["liabilities.mortgages", "Mortgages"],
        ["liabilities.businessDebt", "Business Debt"]
    ];

    const PROTECTION_FIELDS = [
        ["protection.ifSick", "If You Get Sick"],
        ["protection.ifSued", "If You Are Sued"],
        ["protection.ifDie", "If You Die"],
        [WILLS_TRUSTS_PATH, "Wills & Trusts"]
    ];

    const FINANCIAL_PROTECTION_FIELDS = PROTECTION_FIELDS.filter(([path]) => path !== WILLS_TRUSTS_PATH);

    const CASH_FIELDS = [
        ["cashFlow.earnings", "Earnings", "readonly"],
        ["cashFlow.insuranceCosts", "Insurance Costs", "readonly"],
        ["cashFlow.annualSavings", "Annual Savings", "editable"],
        ["cashFlow.debtsAndTaxCosts", "Debts & Tax Costs", "computed"],
        ["cashFlow.lifestyleRemaining", "What's Left for Lifestyle", "computed"]
    ];
    const CASH_OUTFLOW_PATHS = new Set([
        "cashFlow.insuranceCosts",
        "cashFlow.debtObligations",
        "cashFlow.debtsAndTaxCosts"
    ]);

    const ACTIONS = [
        {
            key: "protect-income",
            label: "Protect Income",
            detail: "Review the protection plan",
            section: ".llbs-protection",
            routeOption: "protectionRoute"
        },
        {
            key: "debt-pressure",
            label: "Eliminate Debt Pressure",
            detail: "Open Debt Clarity",
            toolId: "DebtClarity",
            section: ".llbs-gaps-panel"
        },
        {
            key: "optimize-taxes",
            label: "Optimize Taxes",
            detail: "Review the tax burden",
            section: "#llbsTaxProfile"
        },
        {
            key: "asset-growth",
            label: "Build Asset Growth Plan",
            detail: "Open Wealth Forecast",
            toolId: "WealthForecast",
            section: ".llbs-card-section[data-tone='assets']"
        }
    ];

    const ADVISOR_SCRIPTS = {
        overview: {
            title: "Open the Meeting",
            talk: "Use the balance sheet as the map: protection first, then assets, liabilities, net worth, and cash flow.",
            question: "Before we adjust anything, what part of this picture feels most important to clean up first?",
            objection: "If they say they are not ready, anchor on clarity: the goal today is not a purchase, it is finding the pressure points."
        },
        protection: {
            title: "Protection Conversation",
            talk: "This is where we test whether the plan survives a lawsuit, sickness, estate event, or premature death.",
            question: "If something happened tomorrow, what would this actually look like for your family or business?",
            objection: "If they say they already have coverage, ask when it was last stress-tested against income, debt, and dependents."
        },
        willsTrusts: {
            title: "Wills & Trusts Conversation",
            talk: "This isn’t about coverage — this is about whether your assets are actually controlled and passed the way you intend.",
            question: "Do you currently have anything in place that legally directs where everything goes?",
            objection: "Most people assume things will automatically go to family, but without a plan, the state decides."
        },
        assets: {
            title: "Assets Conversation",
            talk: "Assets only matter if they are working with purpose: liquidity, growth, income, protection, or legacy.",
            question: "Walk me through how these assets are currently working for you.",
            objection: "If they say they are comfortable, ask which assets are strategic and which are simply sitting there."
        },
        liabilities: {
            title: "Liabilities Conversation",
            talk: "Liabilities reveal pressure, drag, and risk transfer opportunities.",
            question: "Which of these feels like the biggest pressure point right now?",
            objection: "If they say the payment is manageable, bring it back to opportunity cost and cash flow control."
        },
        networth: {
            title: "Net Worth Conversation",
            talk: "Net worth is not the finish line. It is the scoreboard that tells us whether structure is helping or hurting.",
            question: "When you see this number, does it match how secure you feel?",
            objection: "If they focus only on the number, redirect to quality: liquidity, protection gaps, taxes, and debt pressure."
        },
        cashflow: {
            title: "Cash Flow Conversation",
            talk: "Cash flow tells us how much freedom the structure is actually creating.",
            question: "After obligations, savings, taxes, and insurance, does this leave the lifestyle margin you want?",
            objection: "If they feel squeezed, separate fixed obligations from choices and identify what can be redesigned."
        },
        tax: {
            title: "Tax Conversation",
            talk: "Taxes are not just a bill. They are a recurring drag that should be planned around intentionally.",
            question: "Do you know what your tax burden is doing to your annual cash flow?",
            objection: "If they say their CPA handles it, position this as coordination between cash flow, assets, and tax planning."
        }
    };

    const defaultState = () => ({
        clientId: null,
        version: 1,
        assets: {
            personalProperty: 0,
            savings: 0,
            investments: 0,
            retirement: 0,
            realEstate: 0,
            business: 0,
            total: 0
        },
        liabilities: {
            shortTerm: 0,
            taxes: 0,
            mortgages: 0,
            businessDebt: 0,
            total: 0
        },
        cashFlow: {
            earnings: 0,
            insuranceCosts: 0,
            annualSavings: 0,
            debtObligations: 0,
            debtsAndTaxCosts: 0,
            lifestyleRemaining: 0
        },
        taxProfile: {
            filingStatus: "Single",
            federalTaxRate: 0,
            stateTaxRate: 0,
            ficaRate: 0,
            useCustomTaxOverride: false,
            manualTaxAmount: 0,
            effectiveTaxRate: 0,
            calculatedTaxAmount: 0
        },
        protection: {
            ifSued: protectionDefault(),
            ifSick: protectionDefault(),
            willsTrusts: estatePlanningDefault(),
            ifDie: protectionDefault()
        },
        summary: {}
        ,
        compoundLab: compoundLabDefault()
    });

    function protectionDefault() {
        return {
            primary: { status: "Exposed", coverageAmount: 0, gapAmount: 0 },
            spouse: { status: "Exposed", coverageAmount: 0, gapAmount: 0 },
            activePerson: "primary"
        };
    }

    function estatePlanningDefault() {
        return { status: "NotSetUp", riskLevel: "High" };
    }

    function compoundLabDefault() {
        return {
            startingBalance: "",
            contributionAmount: "",
            contributionCadence: "",
            contributionTiming: "",
            apr: "",
            years: "",
            compoundingCadence: "",
            annualContributionIncrease: "",
            inflationRate: ""
        };
    }

    function isPlainObject(value) {
        return !!value && typeof value === "object" && !Array.isArray(value);
    }

    function mergeDeep(base, patch) {
        const output = Array.isArray(base) ? [...base] : { ...base };
        if (!isPlainObject(patch)) return output;
        Object.keys(patch).forEach((key) => {
            const current = output[key];
            const incoming = patch[key];
            output[key] = isPlainObject(current) && isPlainObject(incoming)
                ? mergeDeep(current, incoming)
                : incoming;
        });
        return output;
    }

    function getPath(obj, path) {
        return path.split(".").reduce((acc, part) => acc && acc[part], obj);
    }

    function setPath(obj, path, value) {
        const parts = path.split(".");
        const last = parts.pop();
        const target = parts.reduce((acc, part) => {
            acc[part] = acc[part] && typeof acc[part] === "object" ? acc[part] : {};
            return acc[part];
        }, obj);
        target[last] = value;
    }

    function parseNumber(value) {
        if (typeof value === "number") return Number.isFinite(value) ? value : 0;
        const normalized = String(value ?? "").replace(/[$,%\s,]/g, "");
        const parsed = Number.parseFloat(normalized);
        return Number.isFinite(parsed) ? parsed : 0;
    }

    function isBlankValue(value) {
        return value === null || value === undefined || String(value).trim() === "";
    }

    function nonNegative(value) {
        return Math.max(0, parseNumber(value));
    }

    function normalizeRate(value) {
        let rate = parseNumber(value);
        if (rate < 0) rate = 0;
        if (rate > 1) rate = rate / 100;
        return Math.min(1, rate);
    }

    function formatCurrency(value) {
        const amount = Number(value || 0);
        const sign = amount < 0 ? "-" : "";
        return `${sign}$${Math.abs(amount).toLocaleString(undefined, { maximumFractionDigits: 0 })}`;
    }

    function formatPercent(value) {
        return `${(normalizeRate(value) * 100).toLocaleString(undefined, {
            minimumFractionDigits: 0,
            maximumFractionDigits: 2
        })}%`;
    }

    function formatNumberValue(value, maxFractionDigits = 2) {
        return Number(value || 0).toLocaleString(undefined, {
            minimumFractionDigits: 0,
            maximumFractionDigits
        });
    }

    function formatYearsCompact(years) {
        const totalYears = Math.max(0, Number(years || 0));
        if (totalYears < 1) {
            const months = Math.max(1, Math.round(totalYears * 12));
            return `${months} mo`;
        }
        if (Math.abs(totalYears - Math.round(totalYears)) < 1e-9) {
            const rounded = Math.round(totalYears);
            return `${rounded} yr${rounded === 1 ? "" : "s"}`;
        }
        return `${formatNumberValue(totalYears, 1)} yrs`;
    }

    function optionLabel(options, value, fallback = "") {
        const match = options.find(([key]) => key === value);
        return match ? match[1] : fallback;
    }

    function contributionCadenceUnitLabel(cadence) {
        switch (cadence) {
            case "daily": return "Day";
            case "weekly": return "Week";
            case "biweekly": return "2 Weeks";
            case "monthly": return "Month";
            case "quarterly": return "Quarter";
            case "yearly": return "Year";
            default: return "Period";
        }
    }

    function optionalNonNegative(value) {
        return isBlankValue(value) ? "" : nonNegative(value);
    }

    function optionalRate(value) {
        return isBlankValue(value) ? "" : normalizeRate(value);
    }

    function optionalYears(value) {
        return isBlankValue(value) ? "" : Math.min(100, Math.max(0, parseNumber(value)));
    }

    function normalizeOptionValue(value, options, fallback) {
        return options.some(([key]) => key === value) ? value : fallback;
    }

    function compoundPeriodsPerYear(cadence) {
        return COMPOUND_PERIODS_PER_YEAR[cadence] || 12;
    }

    function compoundGrowthFactor(apr, compoundingCadence, years) {
        const safeYears = Math.max(0, Number(years || 0));
        const safeApr = normalizeRate(apr);
        if (safeYears === 0) return 1;
        if (compoundingCadence === "continuous") {
            return Math.exp(safeApr * safeYears);
        }
        const compoundsPerYear = compoundPeriodsPerYear(compoundingCadence);
        return Math.pow(1 + safeApr / compoundsPerYear, compoundsPerYear * safeYears);
    }

    function normalizeCompoundLabState(raw) {
        const defaults = compoundLabDefault();
        const source = isPlainObject(raw) ? raw : {};
        return {
            startingBalance: optionalNonNegative(source.startingBalance ?? defaults.startingBalance),
            contributionAmount: optionalNonNegative(source.contributionAmount ?? defaults.contributionAmount),
            contributionCadence: normalizeOptionValue(source.contributionCadence, COMPOUND_CONTRIBUTION_CADENCES, defaults.contributionCadence),
            contributionTiming: normalizeOptionValue(source.contributionTiming, COMPOUND_TIMINGS, defaults.contributionTiming),
            apr: optionalRate(source.apr ?? defaults.apr),
            years: optionalYears(source.years ?? defaults.years),
            compoundingCadence: normalizeOptionValue(source.compoundingCadence, COMPOUNDING_CADENCES, defaults.compoundingCadence),
            annualContributionIncrease: optionalRate(source.annualContributionIncrease ?? defaults.annualContributionIncrease),
            inflationRate: optionalRate(source.inflationRate ?? defaults.inflationRate)
        };
    }

    function buildCompoundProjectionConfig(rawConfig, overrideYears) {
        const source = normalizeCompoundLabState(rawConfig);
        const hasStartingBalance = !isBlankValue(source.startingBalance) && nonNegative(source.startingBalance) > 0;
        const hasContributionAmount = !isBlankValue(source.contributionAmount) && nonNegative(source.contributionAmount) > 0;
        const hasPrincipalInput = hasStartingBalance || hasContributionAmount;
        if (!hasPrincipalInput) return null;

        const derivedContributionCadence = source.contributionCadence || "monthly";
        const derivedContributionTiming = source.contributionTiming || "end";
        const derivedCompoundingCadence = source.compoundingCadence || source.contributionCadence || "monthly";
        const derivedApr = isBlankValue(source.apr) ? 0 : normalizeRate(source.apr);
        const derivedYears = overrideYears === undefined
            ? (isBlankValue(source.years) ? 0 : Math.min(100, Math.max(0, parseNumber(source.years))))
            : Math.max(0, Number(overrideYears || 0));

        return {
            startingBalance: nonNegative(source.startingBalance),
            contributionAmount: hasContributionAmount ? nonNegative(source.contributionAmount) : 0,
            contributionCadence: derivedContributionCadence,
            contributionTiming: derivedContributionTiming,
            apr: derivedApr,
            years: derivedYears,
            compoundingCadence: derivedCompoundingCadence,
            annualContributionIncrease: isBlankValue(source.annualContributionIncrease) ? 0 : normalizeRate(source.annualContributionIncrease),
            inflationRate: isBlankValue(source.inflationRate) ? 0 : normalizeRate(source.inflationRate),
            assumptions: {
                usedDefaultContributionCadence: !source.contributionCadence,
                usedDefaultContributionTiming: !source.contributionTiming,
                usedDefaultCompoundingCadence: !source.compoundingCadence,
                usedDefaultApr: isBlankValue(source.apr),
                usedDefaultYears: isBlankValue(source.years)
            }
        };
    }

    function simulateCompoundProjection(rawConfig, overrideYears) {
        const config = buildCompoundProjectionConfig(rawConfig, overrideYears);
        if (!config) return null;
        const years = config.years;
        const periodsPerYear = compoundPeriodsPerYear(config.contributionCadence);
        const fullPeriods = Math.floor((years * periodsPerYear) + 1e-9);
        const remainingYears = Math.max(0, years - (fullPeriods / periodsPerYear));
        const periodicFactor = compoundGrowthFactor(config.apr, config.compoundingCadence, 1 / periodsPerYear);

        let balance = config.startingBalance;
        let contributionTotal = 0;
        let currentContribution = config.contributionAmount;

        for (let period = 0; period < fullPeriods; period += 1) {
            if (period > 0 && period % periodsPerYear === 0 && config.annualContributionIncrease > 0) {
                currentContribution *= (1 + config.annualContributionIncrease);
            }

            if (config.contributionTiming === "beginning" && currentContribution > 0) {
                balance += currentContribution;
                contributionTotal += currentContribution;
            }

            balance *= periodicFactor;

            if (config.contributionTiming === "end" && currentContribution > 0) {
                balance += currentContribution;
                contributionTotal += currentContribution;
            }
        }

        if (remainingYears > 0) {
            balance *= compoundGrowthFactor(config.apr, config.compoundingCadence, remainingYears);
        }

        const totalDeposited = config.startingBalance + contributionTotal;
        const interestEarned = Math.max(0, balance - totalDeposited);
        const realValue = config.inflationRate > 0
            ? balance / Math.pow(1 + config.inflationRate, years)
            : balance;

        return {
            config,
            years,
            futureValue: balance,
            contributionTotal,
            totalDeposited,
            interestEarned,
            realValue,
            annualizedContribution: config.contributionAmount * periodsPerYear,
            effectiveAnnualYield: compoundGrowthFactor(config.apr, config.compoundingCadence, 1) - 1
        };
    }

    function compoundMilestoneYears(years) {
        const plannedYears = Math.max(0, Number(years || 0));
        return Array.from(new Set([1, 3, 5, 10, 20, 30, plannedYears]
            .filter(value => value > 0 && value <= 100)
            .map(value => Math.round(value * 100) / 100)))
            .sort((a, b) => a - b)
            .slice(0, 7);
    }

    function inputValueForKind(value, kind) {
        if (kind === "percent") return String(Math.round(normalizeRate(value) * 10000) / 100);
        return String(Number(value || 0));
    }

    function displayForKind(value, kind) {
        return kind === "percent" ? formatPercent(value) : formatCurrency(value);
    }

    function isCashOutflowPath(path) {
        return CASH_OUTFLOW_PATHS.has(path);
    }

    function displayForPath(path, value, kind = "") {
        if (isCashOutflowPath(path)) {
            const amount = nonNegative(value);
            return amount > 0 ? formatCurrency(-amount) : formatCurrency(0);
        }
        return displayForKind(value, kind);
    }

    function normalizeStatus(status) {
        const value = String(status || "").trim();
        return STATUS.find(x => x.toLowerCase() === value.toLowerCase()) || "Exposed";
    }

    function normalizeEstateStatus(status) {
        const value = String(status || "").trim();
        const estateStatus = ESTATE_PLAN_STATUSES.find(([key, label]) =>
            key.toLowerCase() === value.toLowerCase() || label.toLowerCase() === value.toLowerCase()
        );
        if (estateStatus) return estateStatus[0];

        const legacyStatus = normalizeStatus(value);
        if (legacyStatus === "Protected") return "FullEstatePlan";
        if (legacyStatus === "Partial") return "BasicWill";
        return "NotSetUp";
    }

    function riskLevelForEstateStatus(status) {
        return ESTATE_RISK_BY_STATUS[normalizeEstateStatus(status)] || "High";
    }

    function resolvePositionStatus({ netWorth, protectionGap, lifestyleRemaining, debtPressureRatio, exposedCount }) {
        if (netWorth <= 0 || lifestyleRemaining < 0 || protectionGap > 0 || debtPressureRatio >= 0.35 || exposedCount > 0) {
            return {
                status: "Exposed",
                summary: "Gaps are open. Close protection and debt pressure before chasing growth."
            };
        }

        if (netWorth >= 250000 && lifestyleRemaining > 0 && debtPressureRatio <= 0.2 && protectionGap === 0) {
            return {
                status: "Strong",
                summary: "Strong foundation. Focus on tax efficiency and compounding growth."
            };
        }

        return {
            status: "Stable",
            summary: "Workable structure — tighten remaining gaps for full control."
        };
    }

    function resolveDebtPressureBand(value) {
        const ratio = normalizeRate(value);
        if (ratio <= 0.2) return "safe";
        if (ratio <= 0.28) return "healthy";
        if (ratio <= 0.35) return "watch";
        if (ratio <= 0.5) return "high";
        return "critical";
    }

    function calculate(state) {
        const s = mergeDeep(defaultState(), state || {});
        s.version = s.version > 0 ? s.version : 1;

        ASSET_FIELDS.forEach(([path]) => setPath(s, path, nonNegative(getPath(s, path))));
        s.assets.total = ASSET_FIELDS.reduce((sum, [path]) => sum + nonNegative(getPath(s, path)), 0);

        s.cashFlow.earnings = nonNegative(s.cashFlow.earnings);
        s.cashFlow.insuranceCosts = nonNegative(s.cashFlow.insuranceCosts);
        s.cashFlow.annualSavings = nonNegative(s.cashFlow.annualSavings);
        s.cashFlow.debtObligations = nonNegative(s.cashFlow.debtObligations);

        s.taxProfile.filingStatus = FILING_STATUSES.includes(s.taxProfile.filingStatus)
            ? s.taxProfile.filingStatus
            : "Single";
        s.taxProfile.federalTaxRate = normalizeRate(s.taxProfile.federalTaxRate);
        s.taxProfile.stateTaxRate = normalizeRate(s.taxProfile.stateTaxRate);
        s.taxProfile.ficaRate = normalizeRate(s.taxProfile.ficaRate);
        s.taxProfile.manualTaxAmount = nonNegative(s.taxProfile.manualTaxAmount);
        s.taxProfile.effectiveTaxRate = s.taxProfile.useCustomTaxOverride
            ? 0
            : Math.min(1, s.taxProfile.federalTaxRate + s.taxProfile.stateTaxRate + s.taxProfile.ficaRate);
        s.taxProfile.calculatedTaxAmount = s.taxProfile.useCustomTaxOverride
            ? s.taxProfile.manualTaxAmount
            : Math.round(s.cashFlow.earnings * s.taxProfile.effectiveTaxRate);

        s.liabilities.shortTerm = nonNegative(s.liabilities.shortTerm);
        s.liabilities.taxes = nonNegative(s.taxProfile.calculatedTaxAmount);
        s.liabilities.mortgages = nonNegative(s.liabilities.mortgages);
        s.liabilities.businessDebt = nonNegative(s.liabilities.businessDebt);
        s.liabilities.total = s.liabilities.shortTerm + s.liabilities.taxes + s.liabilities.mortgages + s.liabilities.businessDebt;

        s.cashFlow.debtsAndTaxCosts = s.cashFlow.debtObligations + s.liabilities.taxes;
        s.cashFlow.lifestyleRemaining = s.cashFlow.earnings - s.cashFlow.insuranceCosts - s.cashFlow.annualSavings - s.cashFlow.debtsAndTaxCosts;

        FINANCIAL_PROTECTION_FIELDS.forEach(([path]) => {
            const raw = getPath(s, path);
            let item = raw || protectionDefault();

            // Migrate old flat format: { status, coverageAmount, gapAmount }
            if (item.primary === undefined && item.status !== undefined) {
                item = {
                    primary: { status: item.status || "Exposed", coverageAmount: item.coverageAmount || 0, gapAmount: item.gapAmount || 0 },
                    spouse: { status: "Exposed", coverageAmount: 0, gapAmount: 0 },
                    activePerson: "primary"
                };
            }
            if (!item.primary) item.primary = { status: "Exposed", coverageAmount: 0, gapAmount: 0 };
            if (!item.spouse) item.spouse = { status: "Exposed", coverageAmount: 0, gapAmount: 0 };
            if (!item.activePerson) item.activePerson = "primary";

            ["primary", "spouse"].forEach(person => {
                item[person].status = normalizeStatus(item[person].status);
                item[person].coverageAmount = nonNegative(item[person].coverageAmount);
                item[person].gapAmount = nonNegative(item[person].gapAmount);
            });

            setPath(s, path, item);
        });

        const estatePlanning = { ...estatePlanningDefault(), ...(getPath(s, WILLS_TRUSTS_PATH) || {}) };
        estatePlanning.status = normalizeEstateStatus(estatePlanning.status);
        estatePlanning.riskLevel = riskLevelForEstateStatus(estatePlanning.status);
        delete estatePlanning.coverageAmount;
        delete estatePlanning.gapAmount;
        setPath(s, WILLS_TRUSTS_PATH, estatePlanning);

        const protectionPairs = FINANCIAL_PROTECTION_FIELDS.map(([path]) => {
            const item = getPath(s, path);
            return { primary: item.primary || item, spouse: item.spouse || null };
        });
        const protectionCoverageTotal = protectionPairs.reduce((sum, { primary, spouse }) =>
            sum + nonNegative(primary.coverageAmount) + (spouse ? nonNegative(spouse.coverageAmount) : 0), 0);
        const protectionGapTotal = protectionPairs.reduce((sum, { primary, spouse }) =>
            sum + nonNegative(primary.gapAmount) + (spouse ? nonNegative(spouse.gapAmount) : 0), 0);
        const protectedCount = protectionPairs.filter(({ primary }) => primary.status === "Protected").length;
        const partialCount = protectionPairs.filter(({ primary }) => primary.status === "Partial").length;
        const exposedCount = protectionPairs.filter(({ primary }) => primary.status === "Exposed").length;
        const debtPressureRatio = s.cashFlow.earnings > 0
            ? Math.min(1, s.cashFlow.debtObligations / s.cashFlow.earnings)
            : (s.cashFlow.debtObligations > 0 ? 1 : 0);
        // Leakage = all cost outflows (insurance + debt + taxes); avoid double-counting via negative lifestyleRemaining
        const cashFlowLeakage = s.cashFlow.insuranceCosts + s.cashFlow.debtsAndTaxCosts;
        const netWorth = s.assets.total - s.liabilities.total;
        const position = resolvePositionStatus({
            netWorth,
            protectionGap: protectionGapTotal,
            lifestyleRemaining: s.cashFlow.lifestyleRemaining,
            debtPressureRatio,
            exposedCount
        });
        const taxBurdenStatement = s.taxProfile.useCustomTaxOverride
            ? `Your tax burden is set by custom override at ${formatCurrency(s.liabilities.taxes)} annually.`
            : `Your estimated tax burden is ${formatPercent(s.taxProfile.effectiveTaxRate)} (${formatCurrency(s.liabilities.taxes)} annually).`;
        const totalProtectionFields = FINANCIAL_PROTECTION_FIELDS.length;
        const netWorthScore = netWorth <= 0 ? 0 : Math.min(20, Math.floor(netWorth / 25000) * 2);
        const protectionScore = exposedCount === 0 && partialCount === 0 ? 25
            : exposedCount === 0 ? 15
            : Math.round((protectedCount / totalProtectionFields) * 10);
        const debtScore = Math.round(Math.max(0, 1 - debtPressureRatio / 0.35) * 20);
        const lifestyleScore = s.cashFlow.lifestyleRemaining > 0 ? 15 : 0;
        const savingsScore = s.cashFlow.annualSavings > 0 ? 10 : 0;
        const estateScore = estatePlanning.status === "FullEstatePlan" ? 10 : estatePlanning.status === "BasicWill" ? 5 : 0;
        const healthScore = Math.min(100, netWorthScore + protectionScore + debtScore + lifestyleScore + savingsScore + estateScore);
        const sectionCompletion = {
            protection: protectedCount + partialCount > 0 || protectionCoverageTotal > 0,
            assets: s.assets.total > 0,
            liabilities: s.liabilities.shortTerm > 0 || s.liabilities.mortgages > 0 || s.liabilities.businessDebt > 0,
            cash: s.cashFlow.earnings > 0,
            tax: s.taxProfile.federalTaxRate > 0 || s.taxProfile.useCustomTaxOverride
        };
        s.summary = {
            assetsTotal: s.assets.total,
            liabilitiesTotal: s.liabilities.total,
            netWorth,
            taxes: s.liabilities.taxes,
            taxDrag: s.liabilities.taxes,
            debtsAndTaxCosts: s.cashFlow.debtsAndTaxCosts,
            lifestyleRemaining: s.cashFlow.lifestyleRemaining,
            protectionCoverageTotal,
            protectionGapTotal,
            estatePlanningStatus: estatePlanning.status,
            estatePlanningRiskLevel: estatePlanning.riskLevel,
            protectedCount,
            partialCount,
            exposedCount,
            cashFlowLeakage,
            debtPressureRatio,
            positionStatus: position.status,
            positionSummary: position.summary,
            positionStatement: `You are currently operating at a Net Worth of ${formatCurrency(netWorth)}. Based on your current structure, you are ${position.status}.`,
            taxBurdenStatement,
            healthScore,
            sectionCompletion
        };

        return s;
    }

    function editable(path, label, kind = "currency", note = "") {
        const safePath = path.replace(/"/g, "&quot;");
        const aria = `Edit ${label}`.replace(/"/g, "&quot;");
        return `
            <span class="llbs-edit-wrap">
                <button type="button" class="llbs-edit-value" data-llbs-edit data-path="${safePath}" data-kind="${kind}" aria-label="${aria}">$0</button>
                <input hidden class="llbs-edit-input" data-llbs-input data-path="${safePath}" data-kind="${kind}" inputmode="decimal" aria-label="${aria}" />
            </span>
            ${note ? `<small>${note}</small>` : ""}
        `;
    }

    function readonly(path, kind = "currency") {
        return `<span class="llbs-readonly-value" data-llbs-output="${path}" data-llbs-kind="${kind}">$0</span>`;
    }

    function row(label, valueHtml, note = "", total = false) {
        return `
            <div class="llbs-row ${total ? "llbs-total-row" : ""}">
                <div class="llbs-label">
                    <strong>${label}</strong>
                    ${note ? `<small>${note}</small>` : ""}
                </div>
                ${valueHtml}
            </div>
        `;
    }

    function renderProtectionCard(path, title, cardOpts = {}) {
        if (path === WILLS_TRUSTS_PATH) {
            return renderWillsTrustsCard(path, title);
        }

        const primaryLabel = cardOpts.primaryLabel || "You";
        const spouseLabel = cardOpts.spouseLabel || "Spouse";
        const hideSpouseToggle = cardOpts.hideSpouseToggle === true;

        const personFields = (person) => `
            <div data-llbs-person-fields="${person}">
                <select class="llbs-status-select" data-llbs-status="${path}.${person}.status" aria-label="${title} ${person} status">
                    ${STATUS.map(status => `<option value="${status}">${status}</option>`).join("")}
                </select>
                <div class="llbs-two-up">
                    <div class="llbs-label">
                        <small>Coverage</small>
                        ${editable(`${path}.${person}.coverageAmount`, `${title} ${person} coverage`)}
                    </div>
                    <div class="llbs-label">
                        <small>Gap</small>
                        ${editable(`${path}.${person}.gapAmount`, `${title} ${person} gap`)}
                    </div>
                </div>
                <div class="llbs-coverage-bar-wrap">
                    <div class="llbs-coverage-bar" data-llbs-coverage-bar="${path}" data-person="${person}"></div>
                </div>
            </div>
        `;

        const toggle = hideSpouseToggle ? "" : `
            <div class="llbs-person-toggle" role="group" aria-label="Select person">
                <button type="button" class="llbs-person-btn is-active" data-llbs-person-toggle data-card-path="${path}" data-person="primary" aria-pressed="true">${primaryLabel}</button>
                <button type="button" class="llbs-person-btn" data-llbs-person-toggle data-card-path="${path}" data-person="spouse" aria-pressed="false">${spouseLabel}</button>
            </div>
        `;

        return `
            <article class="llbs-protection-card" data-active-person="primary" data-card-path="${path}">
                <div class="llbs-protection-card-title">${title}</div>
                ${toggle}
                ${personFields("primary")}
                ${hideSpouseToggle ? "" : personFields("spouse")}
            </article>
        `;
    }

    function renderEstateStatusOptions() {
        return ESTATE_PLAN_STATUSES.map(([value, label]) => `<option value="${value}">${label}</option>`).join("");
    }

    function renderWillsTrustsCard(path, title) {
        return `
            <article class="llbs-protection-card llbs-estate-card" data-llbs-script-key="willsTrusts">
                <div>
                    <div class="llbs-protection-card-title">${title}</div>
                    <small class="llbs-estate-subtext">Controls how your assets are distributed and protected.</small>
                </div>
                <select class="llbs-status-select llbs-estate-status-select" data-llbs-estate-status="${path}.status" aria-label="${title} status">
                    ${renderEstateStatusOptions()}
                </select>
                <div class="llbs-estate-risk-row">
                    <div class="llbs-label">
                        <small>Risk Level</small>
                        <span class="llbs-risk-pill" data-llbs-estate-risk data-path="${path}.riskLevel" data-risk="High">High</span>
                    </div>
                </div>
            </article>
        `;
    }

    function renderTaxPanel() {
        return `
            <section class="llbs-tax-panel" id="llbsTaxProfile" aria-label="Tax profile" data-llbs-script-key="tax">
                <div class="llbs-tax-summary">
                    <div class="llbs-tax-copy">
                        <h3 class="llbs-section-title">Tax Profile</h3>
                        <span data-llbs-text="summary.taxBurdenStatement">Your estimated tax burden is 0% ($0 annually).</span>
                    </div>
                    <div class="llbs-tax-summary-metrics">
                        <span>
                            <small>Filing</small>
                            <strong data-llbs-text="taxProfile.filingStatus">Single</strong>
                        </span>
                        <span>
                            <small>Effective</small>
                            <strong data-llbs-output="taxProfile.effectiveTaxRate" data-llbs-kind="percent">0%</strong>
                        </span>
                        <span>
                            <small>Taxes</small>
                            <strong data-llbs-output="taxProfile.calculatedTaxAmount">$0</strong>
                        </span>
                        <span>
                            <small>Override</small>
                            <strong data-llbs-tax-override>Off</strong>
                        </span>
                    </div>
                    <button type="button"
                            class="llbs-tax-toggle"
                            data-llbs-tax-toggle
                            aria-controls="llbsTaxProfileBody"
                            aria-expanded="false">Edit Tax Profile</button>
                </div>
                <div class="llbs-tax-body" id="llbsTaxProfileBody" hidden>
                    <div class="llbs-tax-grid">
                        <div class="llbs-tax-field">
                            <label for="llbsFilingStatus">Filing Status</label>
                            <select id="llbsFilingStatus" class="llbs-tax-select" data-llbs-select="taxProfile.filingStatus">
                                ${FILING_STATUSES.map(status => `<option value="${status}">${status}</option>`).join("")}
                            </select>
                        </div>
                        <div class="llbs-tax-field">
                            <label>Federal Rate</label>
                            ${editable("taxProfile.federalTaxRate", "Federal tax rate", "percent")}
                        </div>
                        <div class="llbs-tax-field">
                            <label>State Rate</label>
                            ${editable("taxProfile.stateTaxRate", "State tax rate", "percent")}
                        </div>
                        <div class="llbs-tax-field">
                            <label>FICA Rate</label>
                            ${editable("taxProfile.ficaRate", "FICA tax rate", "percent")}
                        </div>
                        <label class="llbs-toggle">
                            <input type="checkbox" data-llbs-checkbox="taxProfile.useCustomTaxOverride" />
                            <span>Override Taxes</span>
                        </label>
                        <div class="llbs-tax-field llbs-tax-manual">
                            <label>Manual Tax Amount</label>
                            ${editable("taxProfile.manualTaxAmount", "Manual tax amount")}
                        </div>
                        <div class="llbs-tax-field">
                            <label>Effective Rate</label>
                            ${readonly("taxProfile.effectiveTaxRate")}
                        </div>
                        <div class="llbs-tax-field">
                            <label>Calculated Taxes</label>
                            ${readonly("taxProfile.calculatedTaxAmount")}
                        </div>
                    </div>
                </div>
            </section>
        `;
    }

    function renderGapsPanel() {
        return `
            <section class="llbs-section llbs-gaps-panel" aria-label="Legend gaps analysis" data-llbs-script-key="overview">
                <div class="llbs-section-head">
                    <h3 class="llbs-section-title">Legend Gaps Analysis</h3>
                    <span class="llbs-section-note">Where money, risk, and pressure are leaking</span>
                </div>
                <div class="llbs-gap-grid">
                    <article class="llbs-gap-card llbs-gap-card-negative">
                        <span>Protection Gap</span>
                        ${readonly("summary.protectionGapTotal")}
                    </article>
                    <article class="llbs-gap-card llbs-gap-card-negative">
                        <span>Estimated Annual Taxes</span>
                        ${readonly("summary.taxDrag")}
                    </article>
                    <article class="llbs-gap-card llbs-gap-card-negative">
                        <span>Cash Flow Leakage</span>
                        ${readonly("summary.cashFlowLeakage")}
                    </article>
                    <article class="llbs-gap-card">
                        <span>Debt Pressure Ratio</span>
                        ${readonly("summary.debtPressureRatio", "percent")}
                    </article>
                    <article class="llbs-gap-card llbs-health-score-card">
                        <span>Health Score</span>
                        <span class="llbs-score-value" data-llbs-score>0</span>
                        <small>out of 100</small>
                    </article>
                </div>
            </section>
        `;
    }

    function renderActionStrip(options = {}) {
        return `
            <section class="llbs-action-strip" aria-label="Recommended next actions">
                <div class="llbs-action-copy">
                    <div class="llbs-section-title">Based on your current position</div>
                    <p>Choose the next strategy path while the numbers are fresh.</p>
                </div>
                <div class="llbs-action-buttons">
                    ${ACTIONS.map(action => {
                        const route = action.routeOption ? (options[action.routeOption] || "") : "";
                        return `
                            <button type="button"
                                    class="llbs-action-btn"
                                    data-llbs-action="${action.key}"
                                    data-tool-id="${action.toolId || ""}"
                                    data-section="${action.section || ""}"
                                    data-route="${route}">
                                <span>${action.label}</span>
                                <small>${action.detail}</small>
                            </button>
                        `;
                    }).join("")}
                </div>
            </section>
        `;
    }

    function renderAdvisorPanel() {
        return `
            <section class="llbs-advisor-panel" data-llbs-advisor-panel hidden>
                <div class="llbs-section-head">
                    <h3 class="llbs-section-title">Advisor Mode</h3>
                    <span class="llbs-section-note">Live conversation guide</span>
                </div>
                <div class="llbs-advisor-grid">
                    <article>
                        <span>Talking Point</span>
                        <p data-llbs-advisor-talk>${ADVISOR_SCRIPTS.overview.talk}</p>
                    </article>
                    <article>
                        <span>Suggested Question</span>
                        <p data-llbs-advisor-question>${ADVISOR_SCRIPTS.overview.question}</p>
                    </article>
                    <article>
                        <span>Objection Pre-Handle</span>
                        <p data-llbs-advisor-objection>${ADVISOR_SCRIPTS.overview.objection}</p>
                    </article>
                </div>
                <div class="llbs-live-script">
                    <strong data-llbs-advisor-title>${ADVISOR_SCRIPTS.overview.title}</strong>
                    <span>Click Protection, Assets, Liabilities, Net Worth, Cash Flow, or Tax Profile to load the right conversation script.</span>
                </div>
            </section>
        `;
    }

    function renderOptionList(options, placeholder = "") {
        const placeholderHtml = placeholder ? `<option value="">${placeholder}</option>` : "";
        return placeholderHtml + options.map(([value, label]) => `<option value="${value}">${label}</option>`).join("");
    }

    function renderCompoundLabModal() {
        return `
            <div class="llbs-compound-overlay" data-llbs-compound-overlay hidden>
                <div class="llbs-compound-backdrop" data-llbs-compound-close></div>
                <section class="llbs-compound-modal" id="llbsCompoundLabModal" role="dialog" aria-modal="true" aria-labelledby="llbsCompoundLabTitle">
                    <div class="llbs-compound-head">
                        <div>
                            <h3 class="llbs-compound-title" id="llbsCompoundLabTitle">Compound Interest Designer</h3>
                            <p class="llbs-compound-subtitle">Model disciplined saving, flexible contribution cadence, APR, and time so you can clearly show the power of compounding.</p>
                        </div>
                        <div class="llbs-compound-head-actions">
                            <button type="button" class="llbs-clear" data-llbs-compound-reset>Reset Lab</button>
                            <button type="button" class="llbs-compound-close" data-llbs-compound-close aria-label="Close compound interest designer">Close</button>
                        </div>
                    </div>
                    <div class="llbs-compound-grid">
                        <section class="llbs-compound-panel llbs-compound-panel-inputs" aria-label="Compound interest inputs">
                            <div class="llbs-compound-field-grid">
                                <label class="llbs-compound-field">
                                    <span>Starting Balance</span>
                                    <input type="number" min="0" step="0.01" class="llbs-compound-input" data-llbs-compound-field="startingBalance" inputmode="decimal" autocomplete="off" />
                                </label>
                                <label class="llbs-compound-field">
                                    <span data-llbs-compound-contribution-label>Save Each Period</span>
                                    <input type="number" min="0" step="0.01" class="llbs-compound-input" data-llbs-compound-field="contributionAmount" inputmode="decimal" autocomplete="off" />
                                    <small class="llbs-compound-field-note" data-llbs-compound-contribution-note>Choose a savings cadence to annualize contributions.</small>
                                </label>
                                <label class="llbs-compound-field">
                                    <span>Savings Cadence</span>
                                    <select class="llbs-compound-select" data-llbs-compound-field="contributionCadence">
                                        ${renderOptionList(COMPOUND_CONTRIBUTION_CADENCES, "Choose cadence")}
                                    </select>
                                </label>
                                <label class="llbs-compound-field">
                                    <span>Contribution Timing</span>
                                    <select class="llbs-compound-select" data-llbs-compound-field="contributionTiming">
                                        ${renderOptionList(COMPOUND_TIMINGS, "Choose timing")}
                                    </select>
                                </label>
                                <label class="llbs-compound-field">
                                    <span>APR %</span>
                                    <input type="number" min="0" step="0.01" class="llbs-compound-input" data-llbs-compound-field="apr" data-kind="percent" inputmode="decimal" autocomplete="off" />
                                </label>
                                <label class="llbs-compound-field">
                                    <span>Compounding</span>
                                    <select class="llbs-compound-select" data-llbs-compound-field="compoundingCadence">
                                        ${renderOptionList(COMPOUNDING_CADENCES, "Choose compounding")}
                                    </select>
                                </label>
                                <label class="llbs-compound-field">
                                    <span>Years</span>
                                    <input type="number" min="0" step="0.25" class="llbs-compound-input" data-llbs-compound-field="years" inputmode="decimal" autocomplete="off" />
                                </label>
                                <label class="llbs-compound-field">
                                    <span>Annual Step-Up %</span>
                                    <input type="number" min="0" step="0.01" class="llbs-compound-input" data-llbs-compound-field="annualContributionIncrease" data-kind="percent" inputmode="decimal" autocomplete="off" />
                                </label>
                                <label class="llbs-compound-field">
                                    <span>Inflation %</span>
                                    <input type="number" min="0" step="0.01" class="llbs-compound-input" data-llbs-compound-field="inflationRate" data-kind="percent" inputmode="decimal" autocomplete="off" />
                                </label>
                            </div>
                            <div class="llbs-compound-explainer" data-llbs-compound-note></div>
                        </section>
                        <section class="llbs-compound-panel llbs-compound-panel-results" aria-label="Compound interest results">
                            <div class="llbs-compound-summary-grid">
                                <article class="llbs-compound-card llbs-compound-card-primary">
                                    <span>Projected Value</span>
                                    <strong data-llbs-compound-output="futureValue">--</strong>
                                    <small data-llbs-compound-output="projectionNote">Enter inputs to project growth.</small>
                                </article>
                                <article class="llbs-compound-card">
                                    <span>New Contributions</span>
                                    <strong data-llbs-compound-output="contributionTotal">--</strong>
                                    <small data-llbs-compound-output="annualizedContribution">Choose amount and cadence.</small>
                                </article>
                                <article class="llbs-compound-card">
                                    <span>Interest Earned</span>
                                    <strong data-llbs-compound-output="interestEarned">--</strong>
                                    <small data-llbs-compound-output="effectiveAnnualYield">Choose APR and compounding.</small>
                                </article>
                                <article class="llbs-compound-card">
                                    <span>Real Purchasing Power</span>
                                    <strong data-llbs-compound-output="realValue">--</strong>
                                    <small data-llbs-compound-output="realValueNote">Inflation-adjusted when provided.</small>
                                </article>
                            </div>
                            <div class="llbs-compound-insight-strip">
                                <article>
                                    <span>Power Per Habit</span>
                                    <strong data-llbs-compound-output="unitGrowth">--</strong>
                                </article>
                                <article>
                                    <span>Total Deposited</span>
                                    <strong data-llbs-compound-output="totalDeposited">--</strong>
                                </article>
                                <article>
                                    <span>Runway</span>
                                    <strong data-llbs-compound-output="yearsHorizon">--</strong>
                                </article>
                            </div>
                            <div class="llbs-compound-compare-grid" data-llbs-compound-compare></div>
                            <div class="llbs-compound-table-wrap">
                                <div class="llbs-compound-table-head">
                                    <div>
                                        <h4>Growth Checkpoints</h4>
                                        <p>Watch how time, discipline, and yield stack on top of each other.</p>
                                    </div>
                                </div>
                                <table class="llbs-compound-table">
                                    <thead>
                                        <tr>
                                            <th>Horizon</th>
                                            <th>Projected Value</th>
                                            <th>Saved</th>
                                            <th>Interest</th>
                                        </tr>
                                    </thead>
                                    <tbody data-llbs-compound-milestones></tbody>
                                </table>
                            </div>
                        </section>
                    </div>
                </section>
            </div>
        `;
    }

    function renderShell(options = {}) {
        const advisorEnabled = !!options.advisorModeEnabled;
        return `
            <section class="llbs-tool" aria-label="Financial Health Snapshot">
                <div class="llbs-shell">
                    <header class="llbs-hero">
                        <div>
                            <h2 class="llbs-title">Financial Health Snapshot</h2>
                            <p class="llbs-subtitle">One live view for protection, assets, liabilities, net worth, taxes, and lifestyle cash flow.</p>
                        </div>
                        <div class="llbs-status">
                            <span class="llbs-save-state" data-llbs-save-state>Ready</span>
                            <button type="button" class="llbs-compound-btn" data-llbs-compound-open aria-controls="llbsCompoundLabModal" aria-expanded="false">Compound Lab</button>
                            <button type="button" class="llbs-print-btn" data-llbs-print>Print</button>
                            <button type="button" class="llbs-clear" data-llbs-reset>Reset Tool</button>
                        </div>
                    </header>
                    ${advisorEnabled ? `
                        <div class="llbs-advisor-toggle" role="group" aria-label="View mode">
                            <button type="button" class="is-active" data-llbs-view="client">Client View</button>
                            <button type="button" data-llbs-view="advisor">Advisor View</button>
                        </div>
                        ${renderAdvisorPanel()}
                    ` : ""}
                    <div class="llbs-body">
                        <section class="llbs-section llbs-protection" data-llbs-script-key="protection">
                            <div class="llbs-section-head">
                                <h3 class="llbs-section-title">Protection</h3>
                                <span class="llbs-section-note" data-llbs-output="summary.protectionGapTotal">$0</span>
                            </div>
                            <div class="llbs-protection-grid">
                                ${(() => {
                                    const clientFirst = (options.clientFirstName || "").trim();
                                    const spouseFirst = (options.spouseFirstName || "").trim();
                                    const cardOpts = {
                                        primaryLabel: clientFirst || "You",
                                        spouseLabel: spouseFirst || "Spouse",
                                        hideSpouseToggle: options.hasSpouse === false
                                    };
                                    return PROTECTION_FIELDS.map(([path, label]) => renderProtectionCard(path, label, cardOpts)).join("");
                                })()}
                            </div>
                        </section>

                        <div class="llbs-main-grid">
                            <section class="llbs-section llbs-card-section" data-tone="assets" data-llbs-script-key="assets">
                                <div class="llbs-section-head">
                                    <h3 class="llbs-section-title">Assets</h3>
                                    <span class="llbs-section-note">What you own</span>
                                </div>
                                <div class="llbs-rows">
                                    ${ASSET_FIELDS.map(([path, label]) => row(label, editable(path, label))).join("")}
                                    ${row("Total", readonly("assets.total"), "", true)}
                                </div>
                            </section>

                            <div class="llbs-main-stack">
                                <section class="llbs-section llbs-card-section" data-tone="liabilities" data-llbs-script-key="liabilities">
                                    <div class="llbs-section-head">
                                        <h3 class="llbs-section-title">Liabilities</h3>
                                        <span class="llbs-section-note">What you owe</span>
                                    </div>
                                    <div class="llbs-rows">
                                        ${LIABILITY_FIELDS.map(([path, label, note]) => {
                                            const value = path === "liabilities.taxes" ? readonly(path) : editable(path, label);
                                            return row(label, value, note || "");
                                        }).join("")}
                                        ${row("Total", readonly("liabilities.total"), "", true)}
                                    </div>
                                </section>

                                <section class="llbs-net-worth" data-llbs-script-key="networth">
                                    <div class="llbs-net-kicker">Net Worth</div>
                                    <div class="llbs-net-value" data-llbs-output="summary.netWorth">$0</div>
                                </section>
                            </div>
                        </div>

                        <section class="llbs-section llbs-card-section" data-tone="cash" data-llbs-script-key="cashflow">
                            <div class="llbs-section-head">
                                <h3 class="llbs-section-title">Cash Flow</h3>
                                <span class="llbs-section-note">Annual view</span>
                            </div>
                            <div class="llbs-cash-grid">
                                ${CASH_FIELDS.map(([path, label, mode]) => {
                                    const elBadge = (path === "cashFlow.earnings" || path === "cashFlow.insuranceCosts")
                                        ? `<span class="llbs-el-source">· EL</span>` : "";
                                    let valueHtml;
                                    if (mode === "editable") {
                                        valueHtml = editable(path, label);
                                    } else {
                                        valueHtml = readonly(path);
                                    }
                                    return `
                                    <article class="llbs-cash-card ${mode === "computed" ? "is-result" : ""} ${path === "cashFlow.debtsAndTaxCosts" ? "is-cost" : ""}">
                                        <div class="llbs-cash-card-header">
                                            <strong>${label}</strong>
                                            ${elBadge}
                                        </div>
                                        ${valueHtml}
                                        ${path === "cashFlow.lifestyleRemaining" ? `<span class="llbs-lifestyle-note" data-llbs-lifestyle-note></span>` : ""}
                                    </article>`;
                                }).join("")}
                            </div>
                        </section>

                        ${renderTaxPanel()}
                        ${renderGapsPanel()}
                        <div class="llbs-save-error" data-llbs-error hidden></div>
                    </div>
                </div>
                ${renderCompoundLabModal()}
            </section>
        `;
    }

    function refresh(root, state) {
        root.querySelectorAll("[data-llbs-output]").forEach((el) => {
            const path = el.getAttribute("data-llbs-output");
            const value = getPath(state, path);
            const kind = el.getAttribute("data-llbs-kind") || "";
            const isRate = kind === "percent" || (path && (path.toLowerCase().includes("rate") || path.toLowerCase().includes("ratio")));
            const isCashOutflow = isCashOutflowPath(path);
            const numericValue = Number(value || 0);
            el.textContent = isRate ? formatPercent(value) : displayForPath(path, value, kind);
            el.classList.toggle("is-cash-outflow", isCashOutflow);
            el.classList.toggle("is-negative", isCashOutflow ? nonNegative(value) > 0 : numericValue < 0);
            if (path === "summary.netWorth") {
                el.classList.toggle("is-positive", numericValue > 0);
            } else {
                el.classList.remove("is-positive");
            }
            if (path === "summary.debtPressureRatio") {
                el.dataset.pressureBand = resolveDebtPressureBand(value);
            } else {
                delete el.dataset.pressureBand;
            }
        });

        root.querySelectorAll("[data-llbs-text]").forEach((el) => {
            const value = getPath(state, el.getAttribute("data-llbs-text"));
            el.textContent = typeof value === "string" ? value : "";
        });

        const positionEl = root.querySelector("[data-llbs-position]");
        if (positionEl) {
            const status = state.summary.positionStatus || "Exposed";
            positionEl.textContent = status;
            positionEl.dataset.status = status;
        }

        const taxOverrideEl = root.querySelector("[data-llbs-tax-override]");
        if (taxOverrideEl) {
            taxOverrideEl.textContent = state.taxProfile.useCustomTaxOverride ? "On" : "Off";
        }

        root.querySelectorAll("[data-llbs-edit]").forEach((button) => {
            const path = button.getAttribute("data-path");
            const kind = button.getAttribute("data-kind") || "currency";
            const value = getPath(state, path);
            const isCashOutflow = isCashOutflowPath(path);
            button.textContent = displayForPath(path, value, kind);
            button.classList.toggle("is-cash-outflow", isCashOutflow);
            button.classList.toggle("is-negative", isCashOutflow && nonNegative(value) > 0);
        });

        root.querySelectorAll("[data-llbs-input]").forEach((input) => {
            if (document.activeElement === input) return;
            const path = input.getAttribute("data-path");
            const kind = input.getAttribute("data-kind") || "currency";
            input.value = inputValueForKind(getPath(state, path), kind);
        });

        root.querySelectorAll("[data-llbs-status]").forEach((select) => {
            const path = select.getAttribute("data-llbs-status");
            const value = normalizeStatus(getPath(state, path));
            select.value = value;
            select.dataset.status = value;
        });

        root.querySelectorAll("[data-llbs-estate-status]").forEach((select) => {
            const path = select.getAttribute("data-llbs-estate-status");
            const value = normalizeEstateStatus(getPath(state, path));
            select.value = value;
            select.dataset.status = value;
        });

        root.querySelectorAll("[data-llbs-estate-risk]").forEach((badge) => {
            const path = badge.getAttribute("data-path");
            const value = riskLevelForEstateStatus(getPath(state, "protection.willsTrusts.status"));
            badge.textContent = getPath(state, path) || value;
            badge.dataset.risk = badge.textContent;
        });

        root.querySelectorAll("[data-llbs-select]").forEach((select) => {
            const value = getPath(state, select.getAttribute("data-llbs-select"));
            select.value = value || "Single";
        });

        root.querySelectorAll("[data-llbs-checkbox]").forEach((checkbox) => {
            checkbox.checked = !!getPath(state, checkbox.getAttribute("data-llbs-checkbox"));
        });

        root.querySelectorAll("article[data-card-path]").forEach(card => {
            const basePath = card.dataset.cardPath;
            if (!basePath) return;
            const activePerson = getPath(state, `${basePath}.activePerson`) || "primary";
            card.dataset.activePerson = activePerson;
            card.querySelectorAll("[data-llbs-person-toggle]").forEach(btn => {
                const isActive = btn.dataset.person === activePerson;
                btn.classList.toggle("is-active", isActive);
                btn.setAttribute("aria-pressed", isActive ? "true" : "false");
            });
        });

        const manualTaxDisabled = !state.taxProfile.useCustomTaxOverride;
        root.querySelector(".llbs-tax-manual")?.classList.toggle("is-disabled", manualTaxDisabled);
        root.querySelectorAll('[data-path="taxProfile.manualTaxAmount"]').forEach((control) => {
            control.disabled = manualTaxDisabled;
        });

        // Section completion dots
        const completion = state.summary?.sectionCompletion || {};
        [
            [".llbs-protection .llbs-section-head", completion.protection],
            [".llbs-card-section[data-tone='assets'] .llbs-section-head", completion.assets],
            [".llbs-card-section[data-tone='liabilities'] .llbs-section-head", completion.liabilities],
            [".llbs-card-section[data-tone='cash'] .llbs-section-head", completion.cash],
            ["#llbsTaxProfile .llbs-tax-summary", completion.tax]
        ].forEach(([selector, complete]) => {
            const el = root.querySelector(selector);
            if (el) el.classList.toggle("is-complete", !!complete);
        });

        // Lifestyle contextual note
        const lifestyleNoteEl = root.querySelector("[data-llbs-lifestyle-note]");
        if (lifestyleNoteEl) {
            const lr = state.cashFlow.lifestyleRemaining;
            lifestyleNoteEl.textContent = lr < 0 ? "Obligations exceed income" : "";
            lifestyleNoteEl.dataset.tone = lr < 0 ? "bad" : "";
        }

        // Coverage % bars
        root.querySelectorAll("[data-llbs-coverage-bar]").forEach(bar => {
            const path = bar.getAttribute("data-llbs-coverage-bar");
            const person = bar.getAttribute("data-person") || "primary";
            const item = getPath(state, path);
            if (!item) return;
            const personData = item[person] || item.primary || item;
            const coverage = nonNegative(personData.coverageAmount);
            const gap = nonNegative(personData.gapAmount);
            const total = coverage + gap;
            const pct = total > 0 ? Math.round((coverage / total) * 100) : 0;
            bar.style.width = `${pct}%`;
            bar.dataset.status = personData.status || "Exposed";
        });

        // Health score badge
        const scoreEl = root.querySelector("[data-llbs-score]");
        if (scoreEl) {
            const score = state.summary?.healthScore ?? 0;
            scoreEl.textContent = score;
            scoreEl.dataset.grade = score >= 75 ? "strong" : score >= 45 ? "stable" : "exposed";
        }
    }

    async function render(options) {
        const host = options?.host;
        const persistence = options?.persistence || window.LegendFinancePersistence;
        if (!host) return;

        if (typeof host.__llbsCleanup === "function") {
            try { host.__llbsCleanup(); } catch (_) { }
        }
        host.__llbsCleanup = null;

        host.innerHTML = renderShell(options);
        const root = host.querySelector(".llbs-tool");
        const mainGridEl = root.querySelector(".llbs-main-grid");
        const mainStackEl = root.querySelector(".llbs-main-stack");
        const assetsSectionEl = root.querySelector('.llbs-card-section[data-tone="assets"]');
        const liabilitiesSectionEl = root.querySelector('.llbs-card-section[data-tone="liabilities"]');
        const netWorthSectionEl = root.querySelector('.llbs-net-worth');
        const saveStateEl = root.querySelector("[data-llbs-save-state]");
        const errorEl = root.querySelector("[data-llbs-error]");
        const LINKED_STATE_PATHS = new Set([
            "cashFlow.annualSavings"
        ]);
        const linkedStateLocks = new Set();
        const windowCleanupFns = [];
        const bindWindow = (eventName, handler) => {
            window.addEventListener(eventName, handler);
            windowCleanupFns.push(() => window.removeEventListener(eventName, handler));
        };
        host.__llbsCleanup = () => {
            windowCleanupFns.forEach(dispose => {
                try { dispose(); } catch (_) { }
            });
            windowCleanupFns.length = 0;
            document.body.classList.remove("llbs-modal-open");
        };
        let loadedState = {};
        try {
            loadedState = await (persistence?.loadState?.(TOOL_ID) || {});
        } catch (_) {
            loadedState = {};
        }

        if (Array.isArray(loadedState?._linkedStateLocks)) {
            loadedState._linkedStateLocks.forEach((path) => {
                if (LINKED_STATE_PATHS.has(path)) linkedStateLocks.add(path);
            });
        }

        function getExpenseLensIncome(source) {
            const hasSplitIncome =
                String(source?.primaryIncome ?? "").trim() !== ""
                || String(source?.spouseIncome ?? "").trim() !== "";
            if (hasSplitIncome) {
                return parseNumber(source?.primaryIncome ?? 0) + parseNumber(source?.spouseIncome ?? 0);
            }
            return parseNumber(source?.income ?? 0);
        }

        const shouldSeedDefault = !loadedState || Object.keys(loadedState).length === 0;
        let state = calculate(mergeDeep(defaultState(), loadedState));
        state.compoundLab = normalizeCompoundLabState(state.compoundLab);
        if (options?.clientProfileId) {
            state.clientId = options.clientProfileId;
        }

        // Seed insurance costs + debt obligations from Expense Lens persisted state
        try {
            const elState = await (persistence?.loadState?.("ExpenseLens") || {});
            const FREQ_MULT = { monthly: 1, weekly: 4.33, biweekly: 2.17 };
            const categories = (elState || {}).categories || [];
            const insMonthly = categories
                .filter(c => (c.name || "").toLowerCase().includes("insurance"))
                .reduce((sum, c) => sum + parseNumber(c.amount || 0) * (FREQ_MULT[c.frequency] || 1), 0);
            const debtMonthly = Math.max(0, parseNumber((elState || {}).monthlyExpenseTotal ?? 0) - insMonthly);
            const elIncome = getExpenseLensIncome(elState || {});
            const nextInsAnnual = Math.round(Math.max(0, insMonthly) * 12);
            const nextDebtAnnual = Math.round(Math.max(0, debtMonthly) * 12);
            const nextEarningsAnnual = Math.round(Math.max(0, elIncome) * 12);
            let seeded = false;
            if (nextInsAnnual !== nonNegative(getPath(state, "cashFlow.insuranceCosts"))) {
                setPath(state, "cashFlow.insuranceCosts", nextInsAnnual);
                seeded = true;
            }
            if (nextDebtAnnual !== nonNegative(getPath(state, "cashFlow.debtObligations"))) {
                setPath(state, "cashFlow.debtObligations", nextDebtAnnual);
                seeded = true;
            }
            if (nextEarningsAnnual !== nonNegative(getPath(state, "cashFlow.earnings"))) {
                setPath(state, "cashFlow.earnings", nextEarningsAnnual);
                seeded = true;
            }
            if (seeded) state = calculate(state);
        } catch (_) {}
        const sessionStartNetWorth = state.summary.netWorth;
        let saveTimer = null;
        let savedLabelTimer = null;
        let focusPulseTimer = null;
        const compoundOverlay = root.querySelector("[data-llbs-compound-overlay]");
        const compoundTrigger = root.querySelector("[data-llbs-compound-open]");
        const compoundFieldEls = Array.from(root.querySelectorAll("[data-llbs-compound-field]"));
        const compoundOutputEls = new Map(Array.from(root.querySelectorAll("[data-llbs-compound-output]")).map((el) => [el.getAttribute("data-llbs-compound-output"), el]));
        const compoundContributionLabelEl = root.querySelector("[data-llbs-compound-contribution-label]");
        const compoundContributionNoteEl = root.querySelector("[data-llbs-compound-contribution-note]");
        const compoundNoteEl = root.querySelector("[data-llbs-compound-note]");
        const compoundCompareEl = root.querySelector("[data-llbs-compound-compare]");
        const compoundMilestonesEl = root.querySelector("[data-llbs-compound-milestones]");

        // Direct listeners on compound fields guarantee updates even when delegation misfires
        compoundFieldEls.forEach(f => {
            const evtName = f.tagName === "SELECT" ? "change" : "input";
            f.addEventListener(evtName, () => {
                updateCompoundLabField(f);
                if (f.tagName === "SELECT") syncCompoundLabForm();
            });
        });

        function getCompoundLabState() {
            state.compoundLab = normalizeCompoundLabState(state.compoundLab);
            return state.compoundLab;
        }

        function compoundFieldInputValue(field, labState) {
            if (isBlankValue(labState[field])) return "";
            if (field === "apr" || field === "annualContributionIncrease" || field === "inflationRate") {
                return inputValueForKind(labState[field], "percent");
            }
            return inputValueForKind(labState[field], "number");
        }

        function syncCompoundLabForm() {
            const labState = getCompoundLabState();
            compoundFieldEls.forEach((field) => {
                const key = field.getAttribute("data-llbs-compound-field");
                if (!key || !(key in labState)) return;
                if (field.tagName === "SELECT") {
                    field.value = labState[key];
                    return;
                }
                if (document.activeElement === field) return;
                field.value = compoundFieldInputValue(key, labState);
            });
        }

        function resetCompoundLab() {
            state.compoundLab = normalizeCompoundLabState(compoundLabDefault());
        }

        function renderCompoundComparisons(projection, labState) {
            const cadenceLabel = optionLabel(COMPOUND_CONTRIBUTION_CADENCES, labState.contributionCadence, "period").toLowerCase();
            const scenarios = [
                {
                    label: "+10% Saved",
                    detail: `${formatCurrency(labState.contributionAmount * 1.1)} each ${cadenceLabel}`,
                    projection: simulateCompoundProjection({ ...labState, contributionAmount: labState.contributionAmount * 1.1 })
                },
                {
                    label: "+1% APR",
                    detail: `${formatPercent(Math.min(1, labState.apr + 0.01))} nominal rate`,
                    projection: simulateCompoundProjection({ ...labState, apr: Math.min(1, labState.apr + 0.01) })
                },
                {
                    label: "+5 More Years",
                    detail: `${formatYearsCompact(labState.years + 5)} horizon`,
                    projection: simulateCompoundProjection({ ...labState, years: labState.years + 5 })
                }
            ];

            return scenarios.map((scenario) => {
                const uplift = scenario.projection.futureValue - projection.futureValue;
                return `
                    <article class="llbs-compound-compare-card">
                        <span>${scenario.label}</span>
                        <strong>${formatCurrency(scenario.projection.futureValue)}</strong>
                        <small>${scenario.detail}</small>
                        <em>${uplift >= 0 ? "+" : "-"}${formatCurrency(Math.abs(uplift))} vs base</em>
                    </article>
                `;
            }).join("");
        }

        function refreshCompoundLab(force = false) {
            if (!compoundOverlay) return;
            if (compoundOverlay.hidden && !force) return;
            const labState = getCompoundLabState();
            const projection = simulateCompoundProjection(labState);
            const projectionConfig = projection?.config || null;
            const cadenceSelected = !!labState.contributionCadence;
            const hasContributionAmount = !isBlankValue(labState.contributionAmount);
            const activeCadence = cadenceSelected
                ? labState.contributionCadence
                : (projectionConfig?.contributionAmount > 0 ? projectionConfig.contributionCadence : "");
            const cadenceLabel = optionLabel(COMPOUND_CONTRIBUTION_CADENCES, activeCadence, "period");
            const cadenceUnitLabel = activeCadence ? contributionCadenceUnitLabel(activeCadence) : "Period";
            const compoundingLabel = optionLabel(COMPOUNDING_CADENCES, projectionConfig?.compoundingCadence || labState.compoundingCadence, "Monthly");
            const annualizedContribution = cadenceSelected && hasContributionAmount
                ? nonNegative(labState.contributionAmount) * compoundPeriodsPerYear(labState.contributionCadence)
                : null;
            const effectiveAnnualYield = projection ? projection.effectiveAnnualYield : null;
            const unitProjection = projection ? simulateCompoundProjection({
                ...labState,
                startingBalance: 0,
                contributionAmount: 1,
                annualContributionIncrease: "",
                inflationRate: ""
            }) : null;

            if (compoundContributionLabelEl) {
                compoundContributionLabelEl.textContent = `Save Each ${cadenceUnitLabel}`;
            }

            if (compoundContributionNoteEl) {
                if (!cadenceSelected) {
                    compoundContributionNoteEl.textContent = "Choose a savings cadence to annualize contributions.";
                } else if (isBlankValue(labState.contributionAmount)) {
                    compoundContributionNoteEl.textContent = `Enter an amount to see the ${cadenceUnitLabel.toLowerCase()} pace.`;
                } else if (labState.contributionCadence === "yearly") {
                    compoundContributionNoteEl.textContent = `${formatCurrency(labState.contributionAmount)} annual contribution`;
                } else {
                    compoundContributionNoteEl.textContent = `${formatCurrency(labState.contributionAmount)} per ${cadenceUnitLabel.toLowerCase()} = ${formatCurrency(annualizedContribution)} in year 1`;
                }
            }

            if (!projection) {
                if (compoundNoteEl) {
                    compoundNoteEl.innerHTML = `
                        <strong>Enter Inputs</strong>
                        <span>Choose savings cadence, contribution timing, compounding, APR, and years. Then enter a starting balance and/or savings amount to see the projection update live.</span>
                    `;
                }

                const emptyOutputs = {
                    futureValue: "--",
                    projectionNote: "Enter inputs to project growth.",
                    contributionTotal: "--",
                    annualizedContribution: annualizedContribution === null
                        ? (cadenceSelected ? "Enter an amount to see year 1 pace." : "Choose amount and cadence.")
                        : `Year 1 pace: ${formatCurrency(annualizedContribution)} per year`,
                    interestEarned: "--",
                    effectiveAnnualYield: effectiveAnnualYield === null
                        ? "Choose APR and compounding."
                        : `${formatPercent(effectiveAnnualYield)} effective annual yield`,
                    realValue: "--",
                    realValueNote: isBlankValue(labState.inflationRate)
                        ? "Inflation-adjusted when provided."
                        : `Inflation set to ${formatPercent(labState.inflationRate)}.`,
                    unitGrowth: "--",
                    totalDeposited: "--",
                    yearsHorizon: isBlankValue(labState.years) ? "--" : formatYearsCompact(labState.years)
                };

                Object.entries(emptyOutputs).forEach(([key, value]) => {
                    const el = compoundOutputEls.get(key);
                    if (el) el.textContent = value;
                });

                if (compoundCompareEl) {
                    compoundCompareEl.innerHTML = "";
                }

                if (compoundMilestonesEl) {
                    compoundMilestonesEl.innerHTML = `
                        <tr>
                            <td colspan="4">Enter a starting balance or savings amount, plus cadence, timing, APR, compounding, and years to see checkpoints.</td>
                        </tr>
                    `;
                }
                return;
            }

            const assumptionNotes = [];
            if (projectionConfig.assumptions.usedDefaultApr) {
                assumptionNotes.push("APR is blank, so growth is currently using 0%.");
            }
            if (projectionConfig.assumptions.usedDefaultYears) {
                assumptionNotes.push("Years is blank, so the current horizon is 0 years.");
            }
            if (projectionConfig.contributionAmount > 0 && projectionConfig.assumptions.usedDefaultContributionTiming) {
                assumptionNotes.push("Contribution timing is blank, so deposits are currently treated as end-of-period.");
            }
            if (projectionConfig.assumptions.usedDefaultCompoundingCadence) {
                assumptionNotes.push(`Compounding is blank, so ${compoundingLabel.toLowerCase()} compounding is being used for now.`);
            }

            if (compoundNoteEl) {
                compoundNoteEl.innerHTML = `
                    <strong>Current Math Basis</strong>
                    <span>${projectionConfig.contributionAmount > 0 ? `${formatCurrency(projectionConfig.contributionAmount)} per ${cadenceUnitLabel.toLowerCase()} means ${formatCurrency(projection.annualizedContribution)} contributed in year 1 at ${formatNumberValue(compoundPeriodsPerYear(projectionConfig.contributionCadence), 0)} deposits per year.` : "No recurring savings amount is entered yet, so the projection is currently showing starting-balance growth only."}</span>
                    <span>${formatCurrency(nonNegative(labState.startingBalance))} starts compounding at ${formatPercent(labState.apr)} APR with ${compoundingLabel.toLowerCase()} compounding for ${projectionConfig.assumptions.usedDefaultYears ? "0 yrs" : formatYearsCompact(projectionConfig.years)}. Deposits are applied at the ${labState.contributionTiming === "beginning" ? "beginning" : "end"} of each ${cadenceLabel.toLowerCase()} period${!isBlankValue(labState.annualContributionIncrease) && normalizeRate(labState.annualContributionIncrease) > 0 ? `, and contributions step up ${formatPercent(labState.annualContributionIncrease)} each year` : ""}.</span>
                    ${assumptionNotes.length > 0 ? `<span>${assumptionNotes.join(" ")}</span>` : ""}
                    <span>${projectionConfig.contributionAmount > 0 ? `Reality check: ${formatCurrency(1)} saved ${cadenceLabel.toLowerCase()} grows to ${formatCurrency(unitProjection.futureValue)} over ${projectionConfig.assumptions.usedDefaultYears ? "0 yrs" : formatYearsCompact(projectionConfig.years)} at the current settings.` : `Current projection reflects ${formatCurrency(projection.futureValue)} from the values entered so far.`}</span>
                `;
            }

            const outputs = {
                futureValue: formatCurrency(projection.futureValue),
                projectionNote: projectionConfig.assumptions.usedDefaultApr || projectionConfig.assumptions.usedDefaultYears
                    ? "Live value using the inputs entered so far."
                    : "Nominal future value from current inputs.",
                contributionTotal: formatCurrency(projection.contributionTotal),
                annualizedContribution: !isBlankValue(labState.annualContributionIncrease) && normalizeRate(labState.annualContributionIncrease) > 0
                    ? `Year 1 pace: ${formatCurrency(projection.annualizedContribution)} per year`
                    : cadenceSelected
                        ? `${formatCurrency(projection.annualizedContribution)} per year`
                        : (hasContributionAmount ? "Choose cadence to annualize savings." : "No recurring savings entered yet."),
                interestEarned: formatCurrency(projection.interestEarned),
                effectiveAnnualYield: effectiveAnnualYield === null
                    ? "APR blank, so current growth is 0%."
                    : `${formatPercent(projection.effectiveAnnualYield)} effective annual yield`,
                realValue: formatCurrency(projection.realValue),
                realValueNote: isBlankValue(labState.inflationRate) || normalizeRate(labState.inflationRate) === 0
                    ? "No inflation adjustment applied."
                    : `Inflation-adjusted at ${formatPercent(labState.inflationRate)}.`,
                unitGrowth: projectionConfig.contributionAmount > 0
                    ? `${formatCurrency(unitProjection.futureValue)} from each ${formatCurrency(1)} ${cadenceLabel.toLowerCase()} save`
                    : "--",
                totalDeposited: formatCurrency(projection.totalDeposited),
                yearsHorizon: projectionConfig.assumptions.usedDefaultYears ? "--" : formatYearsCompact(projectionConfig.years)
            };

            Object.entries(outputs).forEach(([key, value]) => {
                const el = compoundOutputEls.get(key);
                if (el) el.textContent = value;
            });

            if (compoundCompareEl) {
                compoundCompareEl.innerHTML = projectionConfig.assumptions.usedDefaultYears
                    ? ""
                    : renderCompoundComparisons(projection, labState);
            }

            if (compoundMilestonesEl) {
                if (projectionConfig.assumptions.usedDefaultYears) {
                    compoundMilestonesEl.innerHTML = `
                        <tr>
                            <td colspan="4">Enter years to see growth checkpoints.</td>
                        </tr>
                    `;
                    return;
                }
                compoundMilestonesEl.innerHTML = compoundMilestoneYears(projectionConfig.years).map((years) => {
                    const point = simulateCompoundProjection(labState, years);
                    return `
                        <tr>
                            <td>${formatYearsCompact(years)}</td>
                            <td>${formatCurrency(point.futureValue)}</td>
                            <td>${formatCurrency(point.totalDeposited)}</td>
                            <td>${formatCurrency(point.interestEarned)}</td>
                        </tr>
                    `;
                }).join("");
            }
        }

        function setCompoundLabOpen(isOpen) {
            if (!compoundOverlay) return;
            compoundOverlay.hidden = !isOpen;
            compoundTrigger?.setAttribute("aria-expanded", isOpen ? "true" : "false");
            document.body.classList.toggle("llbs-modal-open", !!isOpen);
            if (isOpen) {
                syncCompoundLabForm();
                refreshCompoundLab(true);
                window.requestAnimationFrame(() => {
                    root.querySelector('[data-llbs-compound-field="startingBalance"]')?.focus();
                });
                // Read DOM → state after browser autofill window (autofill doesn't fire input events)
                window.setTimeout(() => {
                    compoundFieldEls.forEach(f => { if (f.tagName !== "SELECT") updateCompoundLabField(f); });
                    refreshCompoundLab(true);
                }, 150);
            } else {
                compoundTrigger?.focus();
            }
        }

        function updateCompoundLabField(field) {
            const key = field.getAttribute("data-llbs-compound-field");
            if (!key) return;
            const labState = getCompoundLabState();
            const rawValue = field.value;

            if (key === "contributionCadence" || key === "compoundingCadence" || key === "contributionTiming") {
                labState[key] = rawValue;
            } else if (isBlankValue(rawValue)) {
                labState[key] = "";
            } else if (key === "apr" || key === "annualContributionIncrease" || key === "inflationRate") {
                labState[key] = normalizeRate(parseNumber(rawValue) / 100);
            } else if (key === "years") {
                labState[key] = Math.min(100, Math.max(0, parseNumber(rawValue)));
            } else {
                labState[key] = nonNegative(rawValue);
            }

            state.compoundLab = normalizeCompoundLabState(labState);
            refreshCompoundLab();
            scheduleSave();
        }

        function refreshAndDelta() {
            refresh(root, state);
            refreshCompoundLab();
            syncNetWorthColumnHeight();
            const deltaEl = root.querySelector("[data-llbs-net-delta]");
            if (deltaEl) {
                const delta = (state.summary?.netWorth ?? 0) - sessionStartNetWorth;
                deltaEl.hidden = delta === 0;
                if (delta !== 0) {
                    deltaEl.textContent = `${delta > 0 ? "+" : ""}${formatCurrency(delta)} vs. session start`;
                    deltaEl.dataset.tone = delta > 0 ? "up" : "down";
                }
            }
            window.dispatchEvent(new CustomEvent("LegendLivingBalanceSheet:updated", {
                detail: {
                    assetsTotal: state.summary?.assetsTotal ?? 0,
                    liabilitiesTotal: state.summary?.liabilitiesTotal ?? 0,
                    netWorth: state.summary?.netWorth ?? 0
                }
            }));
        }

        function syncNetWorthColumnHeight() {
            if (!mainGridEl || !mainStackEl || !assetsSectionEl || !liabilitiesSectionEl || !netWorthSectionEl) return;

            netWorthSectionEl.style.height = "";
            netWorthSectionEl.style.minHeight = "";

            if (window.matchMedia('(max-width: 1080px)').matches) return;

            const assetsHeight = assetsSectionEl.getBoundingClientRect().height;
            const liabilitiesHeight = liabilitiesSectionEl.getBoundingClientRect().height;
            const stackStyles = window.getComputedStyle(mainStackEl);
            const stackGap = parseFloat(stackStyles.rowGap || stackStyles.gap || '0') || 0;
            const targetHeight = Math.floor(assetsHeight - liabilitiesHeight - stackGap);

            if (targetHeight > 48) {
                netWorthSectionEl.style.height = `${targetHeight}px`;
                netWorthSectionEl.style.minHeight = `${targetHeight}px`;
            }
        }

        let netWorthHeightFrame = 0;
        function scheduleNetWorthColumnHeightSync() {
            window.cancelAnimationFrame(netWorthHeightFrame);
            netWorthHeightFrame = window.requestAnimationFrame(() => {
                syncNetWorthColumnHeight();
            });
        }

        function setStatus(text) {
            if (saveStateEl) saveStateEl.textContent = text;
        }

        function showError(message) {
            if (!errorEl) return;
            errorEl.hidden = !message;
            errorEl.textContent = message || "";
        }

        function persistNow() {
            try {
                state = calculate(state);
                if (linkedStateLocks.size > 0) state._linkedStateLocks = Array.from(linkedStateLocks);
                else delete state._linkedStateLocks;
                persistence?.saveState?.(TOOL_ID, state, { immediate: true });
                setStatus("Saving...");
                window.clearTimeout(savedLabelTimer);
                savedLabelTimer = window.setTimeout(() => setStatus("Saved"), 650);
                showError("");
            } catch (err) {
                setStatus("Needs attention");
                showError("Unable to save this update yet. Your values remain visible on this page.");
            }
        }

        function scheduleSave() {
            window.clearTimeout(saveTimer);
            saveTimer = window.setTimeout(persistNow, 450);
        }

        function updateValue(path, value, kind) {
            if (LINKED_STATE_PATHS.has(path)) linkedStateLocks.add(path);
            const normalized = kind === "percent" ? normalizeRate(value / 100) : parseNumber(value);
            setPath(state, path, normalized);
            state = calculate(state);
            refreshAndDelta();
            scheduleSave();
            if (path === "cashFlow.annualSavings") {
                window.dispatchEvent(new CustomEvent("LegendLivingBalanceSheet:savingsUpdated", {
                    detail: { annualSavings: state.cashFlow.annualSavings }
                }));
            }
        }

        function beginEdit(button) {
            const input = button.parentElement?.querySelector("[data-llbs-input]");
            if (!input) return;
            button.hidden = true;
            input.hidden = false;
            input.focus();
            input.select();
        }

        function commitInput(input) {
            const button = input.parentElement?.querySelector("[data-llbs-edit]");
            const path = input.getAttribute("data-path");
            const kind = input.getAttribute("data-kind") || "currency";
            updateValue(path, parseNumber(input.value), kind);
            input.hidden = true;
            if (button) button.hidden = false;
        }

        function focusSection(selector) {
            const section = selector ? root.querySelector(selector) : null;
            if (!section) return false;
            window.clearTimeout(focusPulseTimer);
            root.querySelectorAll(".llbs-focus-pulse").forEach(el => el.classList.remove("llbs-focus-pulse"));
            section.classList.add("llbs-focus-pulse");
            section.scrollIntoView({ behavior: "smooth", block: "center" });
            focusPulseTimer = window.setTimeout(() => section.classList.remove("llbs-focus-pulse"), 1800);
            return true;
        }

        function setAdvisorView(mode) {
            const isAdvisor = mode === "advisor";
            root.dataset.advisorMode = isAdvisor ? "advisor" : "client";
            root.querySelector("[data-llbs-advisor-panel]")?.toggleAttribute("hidden", !isAdvisor);
            root.querySelectorAll("[data-llbs-view]").forEach((button) => {
                const active = button.getAttribute("data-llbs-view") === (isAdvisor ? "advisor" : "client");
                button.classList.toggle("is-active", active);
                button.setAttribute("aria-pressed", active ? "true" : "false");
            });
        }

        function setAdvisorScript(key) {
            const script = ADVISOR_SCRIPTS[key] || ADVISOR_SCRIPTS.overview;
            const titleEl = root.querySelector("[data-llbs-advisor-title]");
            const talkEl = root.querySelector("[data-llbs-advisor-talk]");
            const questionEl = root.querySelector("[data-llbs-advisor-question]");
            const objectionEl = root.querySelector("[data-llbs-advisor-objection]");
            if (titleEl) titleEl.textContent = script.title;
            if (talkEl) talkEl.textContent = script.talk;
            if (questionEl) questionEl.textContent = script.question;
            if (objectionEl) objectionEl.textContent = script.objection;
        }

        function runAction(button) {
            persistNow();

            const route = button.dataset.route || "";
            if (route) {
                window.location.href = route;
                return;
            }

            const toolId = button.dataset.toolId || "";
            const dropdown = document.getElementById("budgetDropdown");
            if (toolId && dropdown && Array.from(dropdown.options).some(option => option.value === toolId)) {
                dropdown.value = toolId;
                dropdown.dispatchEvent(new Event("change", { bubbles: true }));
                return;
            }

            if (button.dataset.section === "#llbsTaxProfile") {
                setTaxProfileExpanded(true);
            }

            focusSection(button.dataset.section || "");
        }

        function setTaxProfileExpanded(expanded) {
            const body = root.querySelector("#llbsTaxProfileBody");
            const toggle = root.querySelector("[data-llbs-tax-toggle]");
            if (!body || !toggle) return;
            body.hidden = !expanded;
            toggle.setAttribute("aria-expanded", expanded ? "true" : "false");
            toggle.textContent = expanded ? "Hide Tax Profile" : "Edit Tax Profile";
        }

        if (options?.advisorModeEnabled) {
            setAdvisorView("client");
        }

        root.addEventListener("click", (event) => {
            const editButton = event.target.closest("[data-llbs-edit]");
            if (editButton) {
                beginEdit(editButton);
                return;
            }

            const personToggle = event.target.closest("[data-llbs-person-toggle]");
            if (personToggle) {
                const cardPath = personToggle.dataset.cardPath;
                const person = personToggle.dataset.person;
                setPath(state, `${cardPath}.activePerson`, person);
                const card = personToggle.closest("article[data-active-person]");
                if (card) {
                    card.dataset.activePerson = person;
                    card.querySelectorAll("[data-llbs-person-toggle]").forEach(btn => {
                        const isActive = btn.dataset.person === person;
                        btn.classList.toggle("is-active", isActive);
                        btn.setAttribute("aria-pressed", isActive ? "true" : "false");
                    });
                }
                scheduleSave();
                return;
            }

            const actionButton = event.target.closest("[data-llbs-action]");
            if (actionButton) {
                runAction(actionButton);
                return;
            }

            const viewButton = event.target.closest("[data-llbs-view]");
            if (viewButton) {
                setAdvisorView(viewButton.getAttribute("data-llbs-view"));
                return;
            }

            if (event.target.closest("[data-llbs-compound-open]")) {
                setCompoundLabOpen(true);
                return;
            }

            if (event.target.closest("[data-llbs-compound-close]")) {
                setCompoundLabOpen(false);
                return;
            }

            if (event.target.closest("[data-llbs-compound-reset]")) {
                resetCompoundLab();
                syncCompoundLabForm();
                refreshCompoundLab();
                scheduleSave();
                return;
            }

            const taxToggle = event.target.closest("[data-llbs-tax-toggle]");
            if (taxToggle) {
                const body = root.querySelector("#llbsTaxProfileBody");
                setTaxProfileExpanded(!!body?.hidden);
                return;
            }

            if (event.target.closest("[data-llbs-print]")) {
                const printWin = window.open("", "_blank");
                const cssLinks = Array.from(document.querySelectorAll('link[rel="stylesheet"]'))
                    .map(l => `<link rel="stylesheet" href="${l.href}">`)
                    .join("\n");
                printWin.document.write(
                    `<!DOCTYPE html><html><head><meta charset="UTF-8">${cssLinks}</head>` +
                    `<body style="margin:0;padding:0;background:#060b16">${root.outerHTML}</body></html>`
                );
                printWin.document.close();
                printWin.addEventListener("load", () => {
                    printWin.print();
                    printWin.close();
                });
                return;
            }

            if (event.target.closest("[data-llbs-reset]")) {
                if (!window.confirm("Reset the Financial Health Snapshot? All entered values will be cleared.")) return;
                linkedStateLocks.clear();
                state = calculate(defaultState());
                delete state._linkedStateLocks;
                refreshAndDelta();
                persistNow();
                return;
            }

            const scriptSource = event.target.closest("[data-llbs-script-key]");
            const clickedInteractive = event.target.closest("button,input,select,textarea,a,label");
            if (!clickedInteractive && scriptSource && root.dataset.advisorMode === "advisor") {
                setAdvisorScript(scriptSource.getAttribute("data-llbs-script-key"));
            }
        });

        root.addEventListener("input", (event) => {
            const compoundField = event.target.closest("[data-llbs-compound-field]");
            if (compoundField) {
                if (event.target.tagName === "SELECT") return;
                updateCompoundLabField(compoundField);
                return;
            }

            const input = event.target.closest("[data-llbs-input]");
            if (!input) return;
            const path = input.getAttribute("data-path");
            if (LINKED_STATE_PATHS.has(path)) linkedStateLocks.add(path);
            const kind = input.getAttribute("data-kind") || "currency";
            const normalized = kind === "percent"
                ? normalizeRate(parseNumber(input.value) / 100)
                : parseNumber(input.value);
            setPath(state, path, normalized);
            state = calculate(state);
            refreshAndDelta();
            scheduleSave();
        });

        root.addEventListener("blur", (event) => {
            const compoundField = event.target.closest("[data-llbs-compound-field]");
            if (compoundField) {
                syncCompoundLabForm();
            }

            const input = event.target.closest("[data-llbs-input]");
            if (input) commitInput(input);
        }, true);

        root.addEventListener("keydown", (event) => {
            if (event.key === "Escape" && compoundOverlay && !compoundOverlay.hidden) {
                event.preventDefault();
                setCompoundLabOpen(false);
                return;
            }

            const input = event.target.closest("[data-llbs-input]");
            if (!input) return;
            if (event.key === "Enter") {
                event.preventDefault();
                commitInput(input);
            }
            if (event.key === "Escape") {
                event.preventDefault();
                input.hidden = true;
                const button = input.parentElement?.querySelector("[data-llbs-edit]");
                if (button) button.hidden = false;
                refreshAndDelta();
            }
        });

        root.addEventListener("change", (event) => {
            const compoundField = event.target.closest("[data-llbs-compound-field]");
            if (compoundField) {
                if (event.target.tagName !== "SELECT") return;
                updateCompoundLabField(compoundField);
                syncCompoundLabForm();
                return;
            }

            const estateStatusSelect = event.target.closest("[data-llbs-estate-status]");
            if (estateStatusSelect) {
                const path = estateStatusSelect.getAttribute("data-llbs-estate-status");
                const status = normalizeEstateStatus(estateStatusSelect.value);
                setPath(state, path, status);
                setPath(state, path.replace(/\.status$/, ".riskLevel"), riskLevelForEstateStatus(status));
                state = calculate(state);
                refreshAndDelta();
                scheduleSave();
                return;
            }

            const statusSelect = event.target.closest("[data-llbs-status]");
            if (statusSelect) {
                setPath(state, statusSelect.getAttribute("data-llbs-status"), normalizeStatus(statusSelect.value));
                state = calculate(state);
                refreshAndDelta();
                scheduleSave();
                return;
            }

            const select = event.target.closest("[data-llbs-select]");
            if (select) {
                setPath(state, select.getAttribute("data-llbs-select"), select.value);
                state = calculate(state);
                refreshAndDelta();
                scheduleSave();
                return;
            }

            const checkbox = event.target.closest("[data-llbs-checkbox]");
            if (checkbox) {
                setPath(state, checkbox.getAttribute("data-llbs-checkbox"), checkbox.checked);
                state = calculate(state);
                refreshAndDelta();
                scheduleSave();
            }
        });

        refreshAndDelta();
        if (shouldSeedDefault) persistNow();
        else setStatus("Loaded");

        scheduleNetWorthColumnHeightSync();
        bindWindow("resize", scheduleNetWorthColumnHeightSync);

        bindWindow("ExpenseLens:updated", (event) => {
            const detail = event.detail || {};
            const expenses = detail.expenses || [];
            const insMonthly = expenses
                .filter(e => (e.name || "").toLowerCase().includes("insurance"))
                .reduce((sum, e) => sum + parseNumber(e.amount || 0), 0);
            const insAnnual = Math.round(insMonthly * 12);
            const debtAnnual = Math.round(Math.max(0, parseNumber(detail.monthlyExpenseTotal ?? 0) - insMonthly) * 12);
            const earningsAnnual = Math.round(Math.max(0, getExpenseLensIncome(detail)) * 12);
            const prevIns = nonNegative(getPath(state, "cashFlow.insuranceCosts"));
            const prevDebt = nonNegative(getPath(state, "cashFlow.debtObligations"));
            const prevEarnings = nonNegative(getPath(state, "cashFlow.earnings"));
            const nextIns = insAnnual;
            const nextDebt = debtAnnual;
            const nextEarnings = earningsAnnual;
            if (nextIns === prevIns && nextDebt === prevDebt && nextEarnings === prevEarnings) return;
            setPath(state, "cashFlow.insuranceCosts", nextIns);
            setPath(state, "cashFlow.debtObligations", nextDebt);
            setPath(state, "cashFlow.earnings", nextEarnings);
            state = calculate(state);
            refreshAndDelta();
            scheduleSave();
        });

        bindWindow("SavingsAccelerator:updated", (event) => {
            const detail = event.detail || {};
            const savings = parseNumber(detail.annualSavings ?? 0);
            const prevSavings = nonNegative(getPath(state, "cashFlow.annualSavings"));
            const nextSavings = linkedStateLocks.has("cashFlow.annualSavings") ? prevSavings : savings;
            if (nextSavings === prevSavings) return;
            setPath(state, "cashFlow.annualSavings", nextSavings);
            state = calculate(state);
            refreshAndDelta();
            scheduleSave();
        });
    }

    window.LegendLivingBalanceSheetTool = {
        toolId: TOOL_ID,
        render,
        calculate
    };
})();
