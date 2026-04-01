(function () {
  const root = document.querySelector(".amp-shell");
  if (!root) return;

  const $ = (sel, scope = document) => scope.querySelector(sel);
  const $$ = (sel, scope = document) => Array.from(scope.querySelectorAll(sel));

  const views = {
    strategy: $("#ampStrategyView", root),
    guide: $("#ampGuideView", root),
    input: $("#ampInputView", root),
    results: $("#ampResultsView", root)
  };

  const form = $("#ampForm", root);
  const resultsHost = $("#ampResults", root);
  const strategyInput = $("#ampSelectedStrategy", root);
  const strategyCards = $$("[data-strategy]", root);
  const strategySections = $$(".strategy-section", root);
  const strategyFieldEls = $$("[data-strategy-field]", root).reduce((acc, el) => {
    acc[el.getAttribute("data-strategy-field")] = el;
    return acc;
  }, {});

  const ampContinueToInputs = $("#ampContinueToInputs", root);
  const ampOpenGuide = $("#ampOpenGuide", root);
  const ampGuideBack = $("#ampGuideBack", root);
  const ampGuideToInputs = $("#ampGuideToInputs", root);
  const ampBackToStrategies = $("#ampBackToStrategies", root);
  const ampCalc = $("#ampCalc", root);
  const ampReset = $("#ampReset", root);
  const ampBackToEdit = $("#ampBackToEdit", root);
  const ampRecalc = $("#ampRecalc", root);
  const ampResetFromResults = $("#ampResetFromResults", root);
  const ampSensitivityProxy = $("#ampSensitivityProxy", root);
  const ampSensitivitySelect = $("#ampSensitivitySelect", root);
  const ampSelectedStrategyDisplay = $("#ampSelectedStrategyDisplay", root);
  const ampSelectedStrategySub = $("#ampSelectedStrategySub", root);
  const ampClientSearch = $("#ampClientSearch", root);
  const ampClientSelection = $("#ampClientSelection", root);
  const ampClientSearchResults = $("#ampClientSearchResults", root);
  const ampClearClientSelection = $("#ampClearClientSelection", root);
  const ampClientUserIdHidden = $("#ampClientUserId", root);
  const ampClientProfileIdHidden = $("#ampClientProfileId", root);

  const guideEls = {
    title: $("#ampGuideTitle", root),
    tagline: $("#ampGuideTagline", root),
    overview: $("#ampGuideOverview", root),
    fit: $("#ampGuideFit", root),
    poorFit: $("#ampGuidePoorFit", root),
    inputs: $("#ampGuideInputs", root),
    inputsGuide: $("#ampGuideInputsGuide", root),
    fields: $("#ampGuideFields", root),
    math: $("#ampGuideMath", root),
    outputs: $("#ampGuideOutputs", root),
    results: $("#ampGuideResults", root),
    risks: $("#ampGuideRisks", root),
    guardrails: $("#ampGuideGuardrails", root),
    questions: $("#ampGuideQuestions", root),
    next: $("#ampGuideNext", root),
    talking: $("#ampGuideTalking", root)
  };

  const strategyMeta = {
    DefinedBenefit: {
      title: "Defined Benefit",
      subtitle: "Large deductible retirement funding",
      tagline: "Actuarial-style funding for older, high-income owners who want large deductible contributions.",
      what: "A qualified plan illustration that estimates deductible retirement funding based on retirement horizon and target income.",
      bestFor: "High-income business owners, especially age 45+, who want to accelerate tax-deferred retirement savings.",
      why: "Shows how a larger deductible contribution can increase projected retirement value while highlighting employee-cost tradeoffs.",
      tradeoff: "Requires plan administration, actuarial review, contribution discipline, and employee-cost monitoring.",
      noFit: "Poor fit for cash-constrained owners, very young owners, or cases with unstable payroll/employee coverage.",
      notes: "Educational illustration only. Final contribution limits and plan design require CPA, ERISA counsel, TPA, and actuarial review.",
      overview: "Defined benefit plans are typically used when a business owner wants a larger deductible contribution than a standard defined contribution design can provide.",
      fit: [
        "Older owner with strong, durable income",
        "Wants larger deductions than a 401(k)-only design",
        "Comfortable with administration and annual commitment"
      ],
      poorFit: [
        "Volatile income or uncertain cash flow",
        "Young owner with long horizon and limited tax pressure",
        "Workforce profile makes employee costs too high"
      ],
      inputs: [
        "Owner age, retirement age, and owner comp",
        "Eligible employee count and average employee comp",
        "Target contribution or target benefit",
        "Tax assumptions and growth assumptions"
      ],
      inputsGuide: [
        "Client Snapshot establishes age and retirement horizon",
        "Business Snapshot drives employee impact",
        "Defined Benefit Inputs tune target contribution/benefit",
        "Projection Assumptions drive future value and income"
      ],
      fields: [
        "Use DB target contribution if you already have a target from an actuary or case design",
        "Use target benefit when you want the tool to estimate a required contribution",
        "Employee cost factor is the rough cost pressure from covering eligible employees"
      ],
      math: [
        "Estimates an annual contribution needed to reach a modeled retirement corpus",
        "Applies growth assumptions and a modest volatility buffer",
        "Calculates current-year tax savings from deductible contributions"
      ],
      outputs: [
        "Estimated deductible contribution",
        "Estimated tax savings and after-tax annual cost",
        "Projected retirement value, income, and legacy value"
      ],
      results: [
        "Lead with the estimated deduction and tax savings",
        "Then explain net annual cost after tax savings and employee impact",
        "Use retirement value/income as the long-term payoff story"
      ],
      risks: [
        "Contribution flexibility is limited compared to nonqualified concepts",
        "Employee cost can rise with age mix and payroll mix",
        "Illustration is not an actuarial certification"
      ],
      guardrails: [
        "Do not present as a final contribution limit",
        "Do not imply discriminatory plan design is acceptable",
        "Always pair with professional review language"
      ],
      questions: [
        "How much could I potentially deduct?",
        "What does it cost me after tax savings?",
        "What happens if business income drops?"
      ],
      next: [
        "Validate payroll and workforce census",
        "Confirm owner retirement horizon and benefit objective",
        "Escalate to TPA/actuary for formal design"
      ],
      talking: [
        "This shows the planning range, not a final actuarial number.",
        "The value is the larger deduction and accelerated retirement funding.",
        "The decision comes down to cash flow comfort and employee cost tolerance."
      ]
    },
    CashBalance: {
      title: "Cash Balance",
      subtitle: "Hybrid DB with pay and interest credits",
      tagline: "A qualified hybrid plan illustration for owners who want structured deductible funding with clearer participant-style framing.",
      what: "A cash balance-style illustration that models desired total contributions with pay and interest credit assumptions.",
      bestFor: "Owners who want large qualified-plan funding but prefer a participant-account style explanation.",
      why: "Useful when you want a qualified-plan story with predictable credits and clearer participant communication.",
      tradeoff: "Still requires plan administration and employee-cost review.",
      noFit: "Weak fit when the business wants maximum flexibility with minimal ongoing administration.",
      notes: "Cash balance plans are still defined benefit arrangements from a compliance/design perspective.",
      overview: "Cash balance plans credit a modeled pay credit and interest credit while still operating under defined-benefit plan rules.",
      fit: [
        "Owner wants higher deductible contributions",
        "A cleaner participant-account narrative helps the case",
        "Workforce economics can support employee costs"
      ],
      poorFit: [
        "Highly unstable business income",
        "Very small contribution opportunity relative to admin burden",
        "Employee cost factor makes design unattractive"
      ],
      inputs: [
        "Desired total contribution",
        "Pay credit and interest credit assumptions",
        "Admin cost and employee profile",
        "Tax and projection assumptions"
      ],
      inputsGuide: [
        "Business Snapshot drives feasibility and employee cost pressure",
        "Cash Balance Inputs control contribution design assumptions",
        "Projection inputs drive modeled retirement value and income"
      ],
      fields: [
        "Desired total contribution is the main planning lever",
        "Pay credit and interest credit help frame the design economics",
        "Admin cost is a real friction point worth showing"
      ],
      math: [
        "Uses desired contribution or owner comp fallback",
        "Combines deductible contribution estimates with tax savings",
        "Projects future value using shared projection assumptions"
      ],
      outputs: [
        "Estimated deductible contribution and tax savings",
        "Net annual cost after tax and employee impact",
        "Projected retirement value and income"
      ],
      results: [
        "Explain this as a qualified-plan funding illustration",
        "Highlight employee cost and admin friction early",
        "Use the comparison table to show the delta from the current path"
      ],
      risks: [
        "Contribution targets still need formal design validation",
        "Employee credits can materially affect net economics",
        "Illustration does not replace TPA/actuarial work"
      ],
      guardrails: [
        "Keep the words illustrative and hypothetical visible",
        "Avoid presenting pay-credit assumptions as guaranteed outcomes",
        "Do not skip employee-cost discussion"
      ],
      questions: [
        "How is this different from a defined benefit plan?",
        "What does the owner really get to contribute?",
        "How expensive is this for the rest of the staff?"
      ],
      next: [
        "Collect census data",
        "Model alternative pay-credit assumptions",
        "Coordinate with TPA/actuary"
      ],
      talking: [
        "This keeps the qualified-plan deduction story front and center.",
        "The real planning question is whether the employee economics still work.",
        "This is a planning screen, not the final plan document."
      ]
    },
    ComboDb401k: {
      title: "Combo DB + 401(k)",
      subtitle: "Layered DB with 401(k) and profit sharing",
      tagline: "Models a stacked qualified-plan design when one plan alone is not enough.",
      what: "Combines defined benefit style funding with defined contribution layers like deferrals, safe harbor, and profit sharing.",
      bestFor: "Owners who already understand qualified plans and want to maximize combined contribution opportunity.",
      why: "Useful for showing how multiple qualified-plan layers can increase savings opportunity.",
      tradeoff: "More moving parts, more testing/design considerations, and more administration.",
      noFit: "Poor fit if the client wants simple administration or has minimal appetite for plan complexity.",
      notes: "Combo designs need careful testing and plan coordination; this tool is only a planning illustration.",
      overview: "The combo strategy layers DB-style funding with 401(k)/profit-sharing elements to show a broader contribution opportunity.",
      fit: [
        "Owner already maxes or nearly maxes core qualified-plan savings",
        "Business can absorb more design/admin complexity",
        "Retirement accumulation is a major planning priority"
      ],
      poorFit: [
        "Simple plan design is the priority",
        "Testing risk or employee cost makes layering unattractive",
        "Owner cash flow does not support larger total funding"
      ],
      inputs: [
        "DB target contribution assumptions",
        "Employee deferral, employer %, profit sharing, safe harbor",
        "Employee-cost factor and testing buffer"
      ],
      inputsGuide: [
        "Use DB section for pension-style funding",
        "Use Combo section for DC-layer assumptions",
        "Review Business Snapshot carefully because employee mix matters"
      ],
      fields: [
        "Target total contribution is the anchor for the combo story",
        "Testing buffer is a planning cushion, not a compliance approval",
        "Catch-up only applies when age-eligible"
      ],
      math: [
        "Uses combo target total or owner comp fallback",
        "Aggregates cost, tax savings, and projections into one view",
        "Shows proposed vs current path side by side"
      ],
      outputs: [
        "Estimated combined deductible contribution",
        "Estimated tax savings and after-tax cost",
        "Projected retirement value and income"
      ],
      results: [
        "Explain why layering is being modeled instead of a single plan",
        "Use the comparison rows to show the incremental planning benefit",
        "Keep the employee-cost/testing conversation explicit"
      ],
      risks: [
        "Higher admin complexity and coordination burden",
        "Testing outcomes can differ from simplified modeling",
        "Employee economics can erode the headline deduction"
      ],
      guardrails: [
        "Do not treat the target total as automatically achievable",
        "Do not hide testing assumptions",
        "Keep compliance review language on every presentation"
      ],
      questions: [
        "Why stack plans instead of just using one?",
        "How much more complexity does this add?",
        "What is the real after-tax cost?"
      ],
      next: [
        "Validate current plan design",
        "Review census/testing with TPA",
        "Compare combo vs single-plan alternatives"
      ],
      talking: [
        "The reason to layer is to expand planning flexibility and deductible capacity.",
        "The reason not to layer is complexity if the economics are not strong enough.",
        "This is a strategy conversation starter, not a final design memo."
      ]
    },
    ExecutiveBonus162: {
      title: "Executive Bonus / 162",
      subtitle: "Executive benefit using life chassis",
      tagline: "Illustrates taxable bonus funding as a simpler executive-benefit concept.",
      what: "Models an employer bonus that is deductible to the business and taxable to the executive, often paired with a life chassis.",
      bestFor: "Selective executive benefit conversations where qualified-plan or broad-based funding is not the target.",
      why: "Simple to explain and useful for showing current bonus cost versus projected policy-driven value.",
      tradeoff: "Taxable to the executive and usually less deductible leverage than qualified plans.",
      noFit: "Poor fit when the client expects current tax-free executive funding or broad employee coverage.",
      notes: "Actual policy design, underwriting, and tax treatment require carrier and tax review.",
      overview: "A 162 bonus is often used when the employer wants a simpler executive-benefit path without a qualified-plan framework.",
      fit: [
        "Selective executive retention/reward planning",
        "Business wants deductible compensation treatment",
        "Life chassis is appropriate to the case"
      ],
      poorFit: [
        "Client expects non-taxable executive access",
        "Underwriting or policy chassis is not practical",
        "Broad employee parity is required"
      ],
      inputs: [
        "Annual bonus amount",
        "Funding years",
        "Policy growth assumption",
        "Death benefit multiple"
      ],
      inputsGuide: [
        "Use Executive Bonus Inputs as the core design levers",
        "Projection and tax assumptions still matter for long-term value framing"
      ],
      fields: [
        "Annual bonus funding is the main driver",
        "Years funded determines the bonus/premium horizon",
        "Death benefit multiple frames legacy leverage"
      ],
      math: [
        "Uses annual bonus as the modeled funding amount",
        "Shows illustrative employer tax savings and employee tax drag",
        "Projects policy value using the policy growth assumption (illustrative)"
      ],
      outputs: [
        "Illustrative annual bonus funding",
        "Illustrative employer tax savings",
        "Illustrative employee tax drag",
        "Projected policy value and policy-based access (illustrative)"
      ],
      results: [
        "Lead with simplicity versus qualified-plan complexity",
        "Explain the tradeoff: deductible to employer but taxable compensation to the executive",
        "Use policy value (not death benefit) as the legacy/access anchor"
      ],
      risks: [
        "Depends on policy assumptions and underwriting",
        "Executive tax treatment must be clearly explained",
        "Not a replacement for detailed carrier illustration"
      ],
      guardrails: [
        "Do not imply guaranteed policy performance",
        "Do not understate executive tax impact",
        "Frame this as concept-level planning only"
      ],
      questions: [
        "Is this taxable to the executive?",
        "Why would we use this instead of a qualified plan?",
        "How much long-term value could this create?"
      ],
      next: [
        "Validate executive selection and objectives",
        "Run carrier-specific illustration if appropriate",
        "Coordinate with tax/legal review"
      ],
      talking: [
        "This is often the simpler executive-benefit conversation.",
        "The client needs to understand the tax tradeoff clearly.",
        "If the case advances, carrier-specific modeling is the next step."
      ]
    },
    DeferredComp: {
      title: "Deferred Compensation",
      subtitle: "Nonqualified deferral with employer credit risk",
      tagline: "Illustrates nonqualified deferral economics for executive planning conversations.",
      what: "Models a nonqualified deferral stream and later distribution horizon with no current employer deduction.",
      bestFor: "Executive compensation planning where broad-based qualified-plan design is not the goal.",
      why: "Useful when the business wants to promise future value without immediate qualified-plan style funding.",
      tradeoff: "No current deduction and executive benefit is subject to employer credit risk.",
      noFit: "Poor fit when the client needs current deduction or minimal credit-risk exposure.",
      notes: "Deferred compensation is heavily document- and compliance-dependent; legal/tax review is mandatory.",
      overview: "This concept models deferral now and distributions later, but it is not a qualified plan and has different tax and risk characteristics.",
      fit: [
        "Executive-level retention planning",
        "Employer comfortable with future-benefit obligation",
        "Client understands current-vs-future tax timing tradeoff"
      ],
      poorFit: [
        "Need for current employer deduction",
        "Low tolerance for employer credit risk",
        "Expectation of full portability or guaranteed access"
      ],
      inputs: [
        "Deferral amount and years",
        "Distribution start age and payout years",
        "Current and future tax rates",
        "Growth assumption"
      ],
      inputsGuide: [
        "Deferred Comp Inputs drive the core economics",
        "Client Snapshot and Projection assumptions drive time horizon"
      ],
      fields: [
        "Deferral amount is the key contribution lever",
        "Distribution years help frame retirement income",
        "Current/future tax rates show the timing tradeoff"
      ],
      math: [
        "No current deduction is modeled",
        "Projects deferred accumulation over the deferral horizon",
        "Estimates later income based on the modeled distribution period"
      ],
      outputs: [
        "No-current-deduction treatment",
        "Projected retirement value and income",
        "Legacy-style remaining value estimate"
      ],
      results: [
        "Be direct that this is not a qualified-plan deduction play",
        "Use the income projection to frame the executive benefit",
        "Call out credit risk and documentation requirements early"
      ],
      risks: [
        "Employer credit risk",
        "409A and document sensitivity",
        "No current deduction can weaken the employer story"
      ],
      guardrails: [
        "Never present as secured or guaranteed unless it truly is",
        "Do not blur the line with qualified plans",
        "Require legal/tax review before advancing"
      ],
      questions: [
        "Why would I defer if there is no current deduction?",
        "What happens if the employer has trouble later?",
        "How much income could this create?"
      ],
      next: [
        "Review executive-retention objectives",
        "Confirm tax/legal appetite",
        "Move to document-level review if the concept fits"
      ],
      talking: [
        "This is more about executive compensation timing than immediate tax savings.",
        "The tradeoff is clear: future value potential versus current deduction and credit risk.",
        "Document and compliance review are not optional here."
      ]
    },
    SplitDollar: {
      title: "Split-Dollar",
      subtitle: "Shared premium and collateral concept",
      tagline: "Illustrates a compliance-sensitive shared funding idea for highly specialized cases.",
      what: "Models annual premium, growth, exit timing, and death benefit leverage in a split-dollar style concept.",
      bestFor: "Narrow cases with sophisticated advisors where split-dollar is already under consideration.",
      why: "Useful as a conversation framework when the client wants to understand the high-level economics before legal design work.",
      tradeoff: "High compliance sensitivity, legal complexity, and very case-specific rules.",
      noFit: "Poor fit for straightforward planning cases or anyone expecting a simple off-the-shelf design.",
      notes: "This concept should never be advanced without legal/tax review and carrier-specific structure guidance.",
      overview: "Split-dollar is a specialized arrangement, so the tool is intentionally high-level and disclosure-heavy.",
      fit: [
        "Sophisticated owner with strong advisory bench",
        "Life chassis and collateral concept are already being explored",
        "Client understands this is a custom legal structure"
      ],
      poorFit: [
        "Needs a simple recommendation",
        "No appetite for legal/tax coordination",
        "Low tolerance for complexity or ambiguity"
      ],
      inputs: [
        "Annual premium and funding years",
        "Exit year",
        "Growth rate",
        "Death benefit"
      ],
      inputsGuide: [
        "Split-Dollar Inputs are the core design assumptions",
        "Projection assumptions shape long-term modeled value"
      ],
      fields: [
        "Annual premium is the primary funding lever",
        "Exit year is critical because it changes the long-term economics",
        "Death benefit is often part of the client-facing value story"
      ],
      math: [
        "Models premium stream and projected growth",
        "Shows no current deduction under the default illustration",
        "Projects retirement and legacy outputs for concept framing"
      ],
      outputs: [
        "Modeled premium commitment",
        "Projected value and legacy leverage",
        "No-current-deduction framing"
      ],
      results: [
        "Keep the conversation conceptual and disclosure-heavy",
        "Do not let the client mistake this for final design economics",
        "Use the results only to decide whether deeper review is worth it"
      ],
      risks: [
        "Complex legal/tax structure",
        "Case-specific rules and documentation risk",
        "Misuse if shown as a simple retail recommendation"
      ],
      guardrails: [
        "Always say legal/tax review required",
        "Do not present the modeled values as guaranteed",
        "Only use with sophisticated, advisor-supported cases"
      ],
      questions: [
        "Why is this more complex than other strategies?",
        "What is the real legal work involved?",
        "Is there current deductibility?"
      ],
      next: [
        "Confirm the advisory team is appropriate",
        "Validate whether the case deserves advanced legal review",
        "Escalate only if sophistication and economics both support it"
      ],
      talking: [
        "This is the most caution-heavy concept in the tool.",
        "Its value is helping decide whether deeper review is justified.",
        "If the case is not clearly advanced, stop here."
      ]
    },
    TaxDiversification: {
      title: "Tax Diversification",
      subtitle: "Balance qualified, taxable, and tax-free buckets",
      tagline: "Shows how a bucket-balance approach can change future income flexibility even without a current deduction.",
      what: "Illustrates the long-term impact of balancing qualified, taxable, and tax-free assets rather than focusing only on current deduction.",
      bestFor: "Clients nearing retirement or already well-funded who need distribution flexibility, not just more deduction.",
      why: "Useful when the client has concentrated savings in one tax bucket and needs better retirement-income flexibility.",
      tradeoff: "Usually no current deduction, so the story is about future tax control and distribution planning.",
      noFit: "Poor fit when the primary objective is immediate employer deduction.",
      notes: "This is a planning framework, not a substitute for full tax-distribution analysis.",
      overview: "Tax diversification focuses on future distribution flexibility by balancing qualified, taxable, and tax-free assets.",
      fit: [
        "Client is overconcentrated in qualified assets",
        "Future distribution tax exposure is a concern",
        "Flexibility matters more than current deduction"
      ],
      poorFit: [
        "Client wants only a current-year deduction strategy",
        "Asset-bucket data is incomplete",
        "No appetite for distribution planning"
      ],
      inputs: [
        "Current qualified, taxable, and tax-free assets",
        "Annual savings",
        "Growth, inflation, and future tax assumptions"
      ],
      inputsGuide: [
        "Client Snapshot and Tax Diversification inputs are the main drivers",
        "Projection assumptions determine future-value framing"
      ],
      fields: [
        "Current asset buckets should be directional but realistic",
        "Annual savings supports the accumulation story",
        "Future tax rate matters because the strategy is distribution-focused"
      ],
      math: [
        "Uses annual savings rather than deductible contribution",
        "Models future asset growth and retirement income",
        "Frames the strategy around future tax flexibility"
      ],
      outputs: [
        "Projected retirement value and income",
        "Estimated legacy value",
        "No-current-deduction positioning"
      ],
      results: [
        "Lead with bucket-balance and retirement flexibility",
        "Do not try to sell it as a deduction story",
        "Use current-vs-proposed comparison to show long-term difference"
      ],
      risks: [
        "No immediate tax-savings headline",
        "Requires client education around future tax control",
        "Simplified model does not replace full tax-distribution planning"
      ],
      guardrails: [
        "Keep current-deduction expectations realistic",
        "Present as distribution planning, not a tax loophole",
        "Use professional review language for implementation"
      ],
      questions: [
        "Why does tax diversification matter if I already save a lot?",
        "How does this affect retirement income?",
        "What does it do for legacy planning?"
      ],
      next: [
        "Validate current asset bucket estimates",
        "Review retirement-income goals",
        "Coordinate with tax planning for implementation"
      ],
      talking: [
        "This is about future flexibility, not immediate deduction.",
        "A better tax bucket mix can change how retirement income feels.",
        "The payoff is optionality later."
      ]
    }
  };

  const strategyKeys = Object.keys(strategyMeta);
  const placeholderMarkup = '<div class="placeholder">Enter inputs and click Calculate to see results.</div>';
  const calcUrl = form?.dataset.calcUrl || "/Workstation/AdvancedMarkets/Calculate";
  const initialStrategy = normalizeStrategy(strategyInput?.value);
  const initialSensitivity = ampSensitivitySelect?.dataset.current || ampSensitivitySelect?.value || "Base";
  let clientSearchTimer = 0;
  let clientSearchSeq = 0;
  let clientInputsRequestSeq = 0;
  let advancedMarketsSaveTimer = 0;
  let selectedBusinessClient = null;
  let selectedBusinessClientInputs = null;
  let appliedPrefillClientId = "";

  function hasInputs(payload){
    if (!payload || !payload.inputs) return false;
    return Object.keys(payload.inputs).length > 0;
  }

  function normalizeStrategy(value) {
    return strategyKeys.includes(value) ? value : strategyKeys[0];
  }

  function escapeHtml(value) {
    return (value ?? "")
      .toString()
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  function fillList(el, items) {
    if (!el) return;
    el.innerHTML = "";
    (items || []).forEach((item) => {
      const li = document.createElement("li");
      li.textContent = item;
      el.appendChild(li);
    });
  }

  async function fetchJson(url) {
    const response = await fetch(url, {
      credentials: "include"
    });

    if (!response.ok) {
      const text = await response.text().catch(() => "");
      throw new Error(text || `Request failed: ${response.status}`);
    }

    return await response.json();
  }

  function getNestedValue(source, path) {
    return path.split(".").reduce((acc, key) => {
      if (acc == null || typeof acc !== "object") return undefined;
      // prefer exact match, otherwise fall back to case-insensitive match (API returns camelCase, form names are PascalCase)
      if (Object.prototype.hasOwnProperty.call(acc, key)) return acc[key];
      const insensitive = Object.keys(acc).find((k) => k.toLowerCase() === key.toLowerCase());
      return insensitive ? acc[insensitive] : undefined;
    }, source);
  }

  function setNested(obj, path, value) {
    const parts = path.split(".");
    let curr = obj;
    parts.forEach((part, idx) => {
      if (idx === parts.length - 1) {
        curr[part] = value;
      } else {
        curr[part] = curr[part] || {};
        curr = curr[part];
      }
    });
  }

  function serializeFormToVm(formEl) {
    const data = {};
    const fd = new FormData(formEl);
    for (const [key, raw] of fd.entries()) {
      if (!key) continue;
      const input = formEl.querySelector(`[name="${CSS.escape(key)}"]`);
      let val = raw;
      if (input?.type === "checkbox") {
        val = input.checked;
      } else if (input?.type === "number" || input?.classList.contains("money-input") || input?.classList.contains("pct-input")) {
        const n = Number(String(raw).replace(/,/g, ""));
        val = Number.isFinite(n) ? n : raw;
      }
      setNested(data, key, val);
    }
    if (!data.Strategy) data.Strategy = {};
    data.Strategy.Selected = strategyInput?.value || data.Strategy.Selected;
    return data;
  }

  async function saveAdvancedMarketsState(options = {}) {
    if (!form) return;
    const { immediate = false } = options;
    window.clearTimeout(advancedMarketsSaveTimer);
    const runner = async () => {
      if (!selectedBusinessClient?.clientUserId) return;
      const clientProfileId =
        selectedBusinessClient.clientProfileId ||
        ampClientProfileIdHidden?.value ||
        "";
      if (!clientProfileId) {
        console.warn("Advanced Markets sync skipped: missing clientProfileId");
        return;
      }
      const inputs = serializeFormToVm(form);
      if (!inputs || !Object.keys(inputs).length) return; // guard: do not write empty payloads
      const payload = {
        clientUserId: selectedBusinessClient.clientUserId,
        clientProfileId,
        inputs
      };
      try {
        const token = $('input[name="__RequestVerificationToken"]', form)?.value || "";
        await fetch("/Clients/SaveAdvancedMarketsInputs", {
          method: "POST",
          headers: {
            "Content-Type": "application/json",
            "RequestVerificationToken": token
          },
          credentials: "include",
          body: JSON.stringify(payload)
        });
      } catch (error) {
        console.warn("Advanced Markets sync to Quick View failed", error);
      }
    };
    if (immediate) {
      runner();
    } else {
      advancedMarketsSaveTimer = window.setTimeout(runner, 800);
    }
  }

  function syncNamedFieldMirrors(source, scope) {
    if (!source?.name || !scope) return;

    const selector = `[name="${source.name}"]`;
    $$(selector, scope).forEach((control) => {
      if (control === source) return;

      if (control.type === "checkbox") {
        control.checked = !!source.checked;
        return;
      }

      control.value = source.value;
    });
  }

  function setNested(obj, path, value) {
    const parts = path.split(".");
    let curr = obj;
    parts.forEach((part, idx) => {
      if (idx === parts.length - 1) {
        curr[part] = value;
      } else {
        curr[part] = curr[part] || {};
        curr = curr[part];
      }
    });
  }

  function serializeFormToVm(formEl) {
    const data = {};
    const fd = new FormData(formEl);
    for (const [key, raw] of fd.entries()) {
      if (!key) continue;
      const input = formEl.querySelector(`[name="${CSS.escape(key)}"]`);
      let val = raw;
      if (input?.type === "checkbox") {
        val = input.checked;
      } else if (input?.type === "number" || input?.classList.contains("money-input") || input?.classList.contains("pct-input")) {
        const n = Number(String(raw).replace(/,/g, ""));
        val = Number.isFinite(n) ? n : raw;
      }
      setNested(data, key, val);
    }
    if (!data.Strategy) data.Strategy = {};
    data.Strategy.Selected = strategyInput?.value || data.Strategy.Selected;
    return data;
  }

  function setClientSelectionMarkup(html) {
    if (ampClientSelection) ampClientSelection.innerHTML = html;
  }

  function renderSelectedBusinessClient() {
    if (!selectedBusinessClient) {
      setClientSelectionMarkup('<span class="amp-client-selection-label">No business client selected.</span>');
      if (ampClientUserIdHidden) ampClientUserIdHidden.value = "";
      if (ampClientProfileIdHidden) ampClientProfileIdHidden.value = "";
      return;
    }

    if (ampClientUserIdHidden) ampClientUserIdHidden.value = selectedBusinessClient.clientUserId || "";
    if (ampClientProfileIdHidden) ampClientProfileIdHidden.value = selectedBusinessClient.clientProfileId || "";

    const savedLabel = selectedBusinessClient.hasSavedInputs
      ? "Saved inputs ready for prefill."
      : "No saved inputs yet. Default client snapshot will prefill where available.";
    const contactBits = [selectedBusinessClient.email, selectedBusinessClient.phone].filter(Boolean).join(" • ");

    setClientSelectionMarkup(`
      <span class="amp-client-selection-label">${escapeHtml(selectedBusinessClient.displayName || "Business Client")}</span>
      <div class="amp-client-selection-meta">${escapeHtml(contactBits || "Selected business client")}</div>
      <div class="amp-client-selection-meta">${escapeHtml(savedLabel)}</div>
    `);
  }

  function renderBusinessClientResults(items) {
    if (!ampClientSearchResults) return;
    const list = Array.isArray(items) ? items : [];

    if (!list.length) {
      ampClientSearchResults.innerHTML = ampClientSearch?.value?.trim()
        ? '<div class="placeholder">No matching business clients found.</div>'
        : "";
      return;
    }

    ampClientSearchResults.innerHTML = list.map((item) => {
      const metaBits = [item.email, item.phone].filter(Boolean).join(" • ");
      return `
        <div class="amp-client-result">
          <div class="amp-client-result-main">
            <div class="amp-client-result-name">${escapeHtml(item.displayName || "Business Client")}</div>
            <div class="amp-client-result-meta">${escapeHtml(metaBits || "Business client")}</div>
          </div>
          <div style="display:flex; gap:8px; align-items:center; flex-wrap:wrap;">
            <span class="amp-client-result-pill">${item.hasSavedInputs ? "Saved Inputs" : "Profile Defaults"}</span>
            <button
              type="button"
              class="btn gold"
              data-select-client="${escapeHtml(item.clientUserId)}"
              data-client-profile-id="${escapeHtml(item.clientProfileId || "")}"
              data-client-name="${escapeHtml(item.displayName || "Business Client")}"
              data-client-email="${escapeHtml(item.email || "")}"
              data-client-phone="${escapeHtml(item.phone || "")}"
              data-client-has-saved-inputs="${item.hasSavedInputs ? "true" : "false"}">Use Client</button>
          </div>
        </div>
      `;
    }).join("");
  }

  async function searchBusinessClients(query) {
    const seq = ++clientSearchSeq;
    const q = (query || "").trim();

    try {
      const data = await fetchJson(`/Clients/AdvancedMarketsBusinessClients?q=${encodeURIComponent(q)}`);
      if (seq !== clientSearchSeq) return;
      renderBusinessClientResults(data);
    } catch (error) {
      if (seq !== clientSearchSeq || !ampClientSearchResults) return;
      ampClientSearchResults.innerHTML = `<div class="placeholder">${escapeHtml(error?.message || "Client search failed.")}</div>`;
    }
  }

  function queueBusinessClientSearch() {
    window.clearTimeout(clientSearchTimer);
    clientSearchTimer = window.setTimeout(() => {
      searchBusinessClients(ampClientSearch?.value || "");
    }, 180);
  }

  async function loadSelectedBusinessClientInputs(force = false) {
    if (!selectedBusinessClient?.clientUserId) return null;
    if (selectedBusinessClientInputs && !force) return selectedBusinessClientInputs;

    const requestedClientId = selectedBusinessClient.clientUserId;
    const requestedProfileId = selectedBusinessClient.clientProfileId || ampClientProfileIdHidden?.value || "";
    const requestSeq = ++clientInputsRequestSeq;
    const qs = new URLSearchParams();
    qs.set("clientUserId", requestedClientId);
    if (requestedProfileId) qs.set("clientProfileId", requestedProfileId);
    let data = null;

    // Primary
    try {
      const primary = await fetchJson(`/Clients/AdvancedMarketsInputs?${qs.toString()}`);
      data = primary;
    } catch (error) {
      console.warn("Advanced Markets primary load failed", error);
    }

    // Fallback
    const missingSaved =
      !data?.hasSavedInputs ||
      !data?.inputs ||
      (typeof data?.fingerprint === "string" && data?.fingerprint === "(none)");

    if (!data || missingSaved) {
      try {
        const fsQs = new URLSearchParams();
        if (requestedProfileId) fsQs.set("clientProfileId", requestedProfileId);
        fsQs.set("clientUserId", requestedClientId);
        fsQs.set("toolId", "AdvancedMarketsInputs");
        const fsRes = await fetchJson(`/api/finance-state/load?${fsQs.toString()}`);
        if (fsRes?.found && fsRes.jsonState) {
          const parsed = JSON.parse(fsRes.jsonState || "{}");
          data = {
            ...(data || {}),
            hasSavedInputs: true,
            inputs: parsed,
            clientUserId: requestedClientId,
            clientProfileId: fsRes.clientProfileId || requestedProfileId,
            fingerprint: "(from finance-state)",
            updatedUtc: fsRes.updatedUtc
          };
        }
      } catch (error) {
        console.warn("Advanced Markets finance-state fallback failed", error);
      }
    }

    if (!data) {
      data = { hasSavedInputs: false, inputs: {}, clientUserId: requestedClientId, clientProfileId: requestedProfileId };
    }

    if (requestSeq !== clientInputsRequestSeq) return null;
    if (selectedBusinessClient?.clientUserId !== requestedClientId) return null;

    const hasContent = hasInputs(data);

    selectedBusinessClient = {
      ...selectedBusinessClient,
      displayName: data.clientName || selectedBusinessClient.displayName,
      clientProfileId: data.clientProfileId || selectedBusinessClient.clientProfileId,
      hasSavedInputs: hasContent || !!data.hasSavedInputs
    };
    selectedBusinessClientInputs = data;
    renderSelectedBusinessClient();
    return data;
  }

  function applyBusinessClientPrefill(payload) {
    if (!form || !hasInputs(payload)) return;

    $$("[name]", form).forEach((control) => {
      const path = control.getAttribute("name") || "";
      if (!path || path.startsWith("Strategy.")) return;

      if (control.type === "checkbox") {
        control.checked = false;
      } else {
        control.value = "";
      }

      const value = getNestedValue(payload.inputs, path);
      if (value === undefined || value === null) return;

      if (control.type === "checkbox") {
        control.checked = !!value;
        return;
      }

      control.value = `${value}`;
      if (control.tagName === "SELECT") {
        control.dataset.current = `${value}`;
      }
    });
  }

  async function ensureSelectedBusinessClientPrefill() {
    if (!selectedBusinessClient?.clientUserId) return;
    const currentPrefillKey = selectedBusinessClient.clientProfileId || selectedBusinessClient.clientUserId;
    if (appliedPrefillClientId === currentPrefillKey) return;

    try {
      const payload = await loadSelectedBusinessClientInputs();
      if (!payload || !hasInputs(payload)) {
        // fall back to local draft if server returns empty
        const drafts = JSON.parse(localStorage.getItem("legend_adv_markets_drafts_v1") || "{}");
        const draft = drafts[selectedBusinessClient.clientProfileId || selectedBusinessClient.clientUserId];
        if (draft?.inputs && Object.keys(draft.inputs).length) {
          applyBusinessClientPrefill({ inputs: draft.inputs });
          appliedPrefillClientId = currentPrefillKey;
        }
        return;
      }
      applyBusinessClientPrefill(payload);
      appliedPrefillClientId = currentPrefillKey;
    } catch (error) {
      setClientSelectionMarkup(`
        <span class="amp-client-selection-label">${escapeHtml(selectedBusinessClient.displayName || "Business Client")}</span>
        <div class="amp-client-selection-meta">Saved input prefill could not be loaded. You can still enter values manually.</div>
      `);
      throw error;
    }
  }

  function setSelectedBusinessClient(item) {
    clientInputsRequestSeq += 1;
    selectedBusinessClient = item ? { ...item } : null;
    selectedBusinessClientInputs = null;
    appliedPrefillClientId = "";

    if (ampClientSearch) {
      ampClientSearch.value = item?.displayName || "";
    }
    if (ampClientSearchResults) {
      ampClientSearchResults.innerHTML = "";
    }

    if (ampClientUserIdHidden) ampClientUserIdHidden.value = item?.clientUserId || "";
    if (ampClientProfileIdHidden) ampClientProfileIdHidden.value = item?.clientProfileId || "";

    renderSelectedBusinessClient();
  }

  function setBusy(button, isBusy) {
    if (!button) return;
    button.classList.toggle("is-busy", !!isBusy);
    button.disabled = !!isBusy;
  }

  function showView(name) {
    Object.entries(views).forEach(([key, section]) => {
      if (!section) return;
      const active = key === name;
      section.classList.toggle("is-hidden", !active);
      section.classList.toggle("view-active", active);
    });
  }

  function applySelectDefaults(scope) {
    $$("select[data-current]", scope || root).forEach((select) => {
      const current = (select.dataset.current || "").trim();
      if (current && Array.from(select.options).some((opt) => opt.value === current)) {
        select.value = current;
      } else if (!current) {
        select.value = "";
      }
    });
  }

  function syncSensitivity(value) {
    const next = value || initialSensitivity || "Base";
    if (ampSensitivitySelect && Array.from(ampSensitivitySelect.options).some((opt) => opt.value === next)) {
      ampSensitivitySelect.value = next;
    }
    if (ampSensitivityProxy && Array.from(ampSensitivityProxy.options).some((opt) => opt.value === next)) {
      ampSensitivityProxy.value = next;
    }
  }

  function updateStrategyOverview(strategyKey) {
    const meta = strategyMeta[strategyKey] || strategyMeta[initialStrategy];
    if (!meta) return;

    if (ampSelectedStrategyDisplay) ampSelectedStrategyDisplay.textContent = meta.title;
    if (ampSelectedStrategySub) ampSelectedStrategySub.textContent = meta.subtitle;

    const map = {
      title: meta.title,
      tagline: meta.tagline,
      what: meta.what,
      bestFor: meta.bestFor,
      why: meta.why,
      tradeoff: meta.tradeoff,
      noFit: meta.noFit,
      notes: meta.notes
    };

    Object.entries(map).forEach(([field, value]) => {
      if (strategyFieldEls[field]) strategyFieldEls[field].textContent = value;
    });
  }

  function renderGuide(strategyKey) {
    const meta = strategyMeta[strategyKey] || strategyMeta[initialStrategy];
    if (!meta) return;

    if (guideEls.title) guideEls.title.textContent = `${meta.title} Guide`;
    if (guideEls.tagline) guideEls.tagline.textContent = meta.tagline;
    if (guideEls.overview) guideEls.overview.textContent = meta.overview;

    fillList(guideEls.fit, meta.fit);
    fillList(guideEls.poorFit, meta.poorFit);
    fillList(guideEls.inputs, meta.inputs);
    fillList(guideEls.inputsGuide, meta.inputsGuide);
    fillList(guideEls.fields, meta.fields);
    fillList(guideEls.math, meta.math);
    fillList(guideEls.outputs, meta.outputs);
    fillList(guideEls.results, meta.results);
    fillList(guideEls.risks, meta.risks);
    fillList(guideEls.guardrails, meta.guardrails);
    fillList(guideEls.questions, meta.questions);
    fillList(guideEls.next, meta.next);
    fillList(guideEls.talking, meta.talking);
  }

  function toggleStrategySections(strategyKey) {
    strategySections.forEach((section) => {
      const allowed = (section.dataset.strategies || "")
        .split(",")
        .map((item) => item.trim())
        .filter(Boolean);
      const visible = !allowed.length || allowed.includes(strategyKey);
      section.classList.toggle("is-hidden", !visible);
      $$("input, select, textarea, button", section).forEach((control) => {
        control.disabled = !visible;
      });
    });
  }

  function setStrategy(nextStrategy) {
    const strategyKey = normalizeStrategy(nextStrategy || strategyInput?.value);
    if (strategyInput) strategyInput.value = strategyKey;

    strategyCards.forEach((card) => {
      const active = card.getAttribute("data-strategy") === strategyKey;
      card.classList.toggle("active", active);
      card.setAttribute("aria-pressed", active ? "true" : "false");
    });

    updateStrategyOverview(strategyKey);
    renderGuide(strategyKey);
    toggleStrategySections(strategyKey);
  }

  function restoreInitialState(options = {}) {
    if (!form) return;

    form.reset();
    appliedPrefillClientId = "";
    applySelectDefaults(form);
    syncSensitivity(initialSensitivity);
    setStrategy(initialStrategy);

    if (options.clearResults && resultsHost) {
      resultsHost.innerHTML = placeholderMarkup;
    }
  }

  function decorateComparisonTable(scope) {
    $$(".cmp-table .delta", scope).forEach((cell) => {
      const numeric = Number((cell.textContent || "").replace(/[^0-9.-]/g, ""));
      cell.classList.remove("pos", "neg");
      if (Number.isFinite(numeric) && numeric > 0) cell.classList.add("pos");
      if (Number.isFinite(numeric) && numeric < 0) cell.classList.add("neg");
    });
  }

  function renderMiniCharts(scope) {
    $$(".mini-chart", scope).forEach((chart) => {
      const raw = chart.getAttribute("data-series");
      const bars = $(".bars", chart);
      if (!raw || !bars) return;

      let series;
      try {
        series = JSON.parse(raw);
      } catch {
        return;
      }

      const data = Array.isArray(series?.data)
        ? series.data
        : Array.isArray(series?.Data)
        ? series.Data
        : [];
      const labels = Array.isArray(series?.labels)
        ? series.labels
        : Array.isArray(series?.Labels)
        ? series.Labels
        : [];
      const seriesName = series?.name || series?.Name || chart?.querySelector(".chart-title")?.textContent || "";
      const max = Math.max(...data.map((value) => Math.abs(Number(value) || 0)), 1);

      bars.innerHTML = "";
      data.forEach((value, index) => {
        const numeric = Number(value) || 0;
        const label = labels[index] || `Point ${index + 1}`;
        const palette = getBarPalette(seriesName, label);
        const row = document.createElement("div");
        row.className = "bar";
        row.style.width = `${Math.max(14, (Math.abs(numeric) / max) * 100)}%`;
        row.style.backgroundImage = `linear-gradient(90deg, ${palette.from}, ${palette.to})`;
        row.style.color = palette.text;
        row.innerHTML = `
          <span class="bar-label">${escapeHtml(label)}</span>
          <span class="bar-value">${escapeHtml(numeric.toLocaleString("en-US", { maximumFractionDigits: 0 }))}</span>
        `;
        bars.appendChild(row);
      });
    });
  }

  function getBarPalette(seriesName = "", label = "") {
    const name = (seriesName || "").toLowerCase();
    const lbl = (label || "").toLowerCase();

    // Palettes
    const gold = { from: "#b88a2c", to: "#f2cf63", text: "#0f172a" };
    const green = { from: "#1b7f4b", to: "#31c48d", text: "#0b1526" };
    const navy = { from: "#0f1b2d", to: "#1e2f4a", text: "#f8fafc" };
    const slate = { from: "#475569", to: "#6b7a90", text: "#f8fafc" };
    const steel = { from: "#5b6378", to: "#7a8498", text: "#f8fafc" };
    const teal = { from: "#0f809a", to: "#26c3d6", text: "#06202a" };

    // Annual Tax Savings
    if (name.includes("tax savings")) return green;

    // Total projected value
    if (name.includes("total projected value")) return gold;

    // Retirement income sources
    if (name.includes("retirement income sources")) {
      if (lbl.includes("strategy")) return gold;
      if (lbl.includes("current")) return navy;
      if (lbl.includes("outside")) return slate;
      return gold;
    }

    // Current asset buckets
    if (name.includes("asset buckets")) {
      if (lbl.includes("qualified")) return gold;
      if (lbl.includes("tax-free")) return teal;
      if (lbl.includes("taxable")) return steel;
      return gold;
    }

    // Default strategy/proposed
    return gold;
  }

  function enhanceResults() {
    if (!resultsHost) return;
    decorateComparisonTable(resultsHost);
    renderMiniCharts(resultsHost);
  }

  function renderError(statusCode, message) {
    if (!resultsHost) return;
    resultsHost.innerHTML = `
      <div class="error-card">
        <div class="error-title">Advanced Markets calculation failed</div>
        <div class="error-meta">Status: ${escapeHtml(statusCode)}</div>
        <pre class="error-detail">${escapeHtml(message || "Unknown error")}</pre>
      </div>
    `;
  }

  function stripMoneySeparators() {
    $$("input.money-input", form).forEach((input) => {
      input.value = (input.value || "").replace(/,/g, "").trim();
    });
  }

  async function calculate() {
    if (!form || !resultsHost) return;

    setBusy(ampCalc, true);
    setBusy(ampRecalc, true);

    try {
      stripMoneySeparators();
      const formData = new FormData(form);
      const token = $('input[name="__RequestVerificationToken"]', form)?.value || "";
      const response = await fetch(calcUrl, {
        method: "POST",
        headers: {
          RequestVerificationToken: token
        },
        credentials: "include",
        body: formData
      });

      const body = await response.text();
      if (!response.ok) {
        renderError(response.status, body);
        showView("results");
        return;
      }

      resultsHost.innerHTML = body;
      enhanceResults();
      showView("results");
    } catch (error) {
      renderError("client", error?.message || "Unexpected error");
      showView("results");
    } finally {
      setBusy(ampCalc, false);
      setBusy(ampRecalc, false);
    }
  }

  strategyCards.forEach((card) => {
    card.addEventListener("click", () => {
      setStrategy(card.getAttribute("data-strategy"));
    });
  });

  ampSensitivityProxy?.addEventListener("change", () => syncSensitivity(ampSensitivityProxy.value));
  ampSensitivitySelect?.addEventListener("change", () => syncSensitivity(ampSensitivitySelect.value));

  ampClientSearch?.addEventListener("input", queueBusinessClientSearch);
  ampClientSearch?.addEventListener("focus", () => {
    if (!ampClientSearchResults?.innerHTML) {
      searchBusinessClients(ampClientSearch.value || "");
    }
  });

  ampClientSearchResults?.addEventListener("click", (event) => {
    const target = event.target;
    if (!(target instanceof Element)) return;

    const button = target.closest("[data-select-client]");
    if (!button) return;

    const userId = button.getAttribute("data-select-client");
    const profileId = button.getAttribute("data-client-profile-id") || "";
    if (!userId) return;

    setSelectedBusinessClient({
      clientUserId: userId,
      clientProfileId: profileId,
      displayName: button.getAttribute("data-client-name") || "Business Client",
      email: button.getAttribute("data-client-email") || "",
      phone: button.getAttribute("data-client-phone") || "",
      hasSavedInputs: (button.getAttribute("data-client-has-saved-inputs") || "").toLowerCase() === "true"
    });
  });

  form?.addEventListener("input", (event) => {
    const source = event.target;
    if (!(source instanceof HTMLInputElement || source instanceof HTMLSelectElement || source instanceof HTMLTextAreaElement)) return;
    syncNamedFieldMirrors(source, form);
    saveAdvancedMarketsState();
  });

  form?.addEventListener("change", (event) => {
    const source = event.target;
    if (!(source instanceof HTMLInputElement || source instanceof HTMLSelectElement || source instanceof HTMLTextAreaElement)) return;
    syncNamedFieldMirrors(source, form);
    saveAdvancedMarketsState({ immediate: true });
  });

  ampClearClientSelection?.addEventListener("click", () => {
    setSelectedBusinessClient(null);
  });

  ampContinueToInputs?.addEventListener("click", async () => {
    setStrategy(strategyInput?.value);
    try {
      await ensureSelectedBusinessClientPrefill();
    } catch (error) {
      console.error(error);
    }
    showView("input");
  });

  ampOpenGuide?.addEventListener("click", () => {
    setStrategy(strategyInput?.value);
    showView("guide");
  });

  ampGuideBack?.addEventListener("click", () => showView("strategy"));
  ampGuideToInputs?.addEventListener("click", async () => {
    try {
      await ensureSelectedBusinessClientPrefill();
    } catch (error) {
      console.error(error);
    }
    showView("input");
  });
  ampBackToStrategies?.addEventListener("click", () => showView("strategy"));
  ampBackToEdit?.addEventListener("click", () => showView("input"));

  ampCalc?.addEventListener("click", calculate);
  ampRecalc?.addEventListener("click", calculate);

  ampReset?.addEventListener("click", () => {
    restoreInitialState();
    showView("input");
  });

  ampResetFromResults?.addEventListener("click", () => {
    restoreInitialState({ clearResults: true });
    showView("strategy");
  });

  applySelectDefaults(root);
  syncSensitivity(initialSensitivity);
  setStrategy(initialStrategy);
  renderSelectedBusinessClient();
  enhanceResults();
})();
