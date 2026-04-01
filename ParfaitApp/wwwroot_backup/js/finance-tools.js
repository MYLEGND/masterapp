document.addEventListener("DOMContentLoaded", function () {
    const dropdown = document.getElementById("budgetDropdown");
    const embedContainer = document.getElementById("budget-embed");

    // ------------------- Persistence Helpers (UPDATED) -------------------
    function saveToolState(toolId) {
        const container = embedContainer.querySelector('.networth-tool');
        if (!container) return;

        const state = {};

        // Save all inputs
        container.querySelectorAll('input').forEach(input => state[input.id] = input.value);

        // Save all outputs (span, td)
        container.querySelectorAll('span, td').forEach(el => {
            if(el.id) state[el.id] = el.textContent;
        });

        // Save tips/advice/recommendations
        container.querySelectorAll('.advice, [id$="Advice"], [id$="Tip"], p.text-muted').forEach(el => {
            if(el.id) state[el.id] = el.textContent;
        });

        localStorage.setItem(`toolState-${toolId}`, JSON.stringify(state));
    }

    function loadToolState(toolId) {
        const saved = JSON.parse(localStorage.getItem(`toolState-${toolId}`) || '{}');
        const container = embedContainer.querySelector('.networth-tool');
        if (!container) return;

        Object.keys(saved).forEach(id => {
            const el = document.getElementById(id);
            if(el) {
                if(el.tagName === 'INPUT') el.value = saved[id];
                else el.textContent = saved[id];
            }
        });

        // Re-apply saved tips/advice
        container.querySelectorAll('.advice, [id$="Advice"], [id$="Tip"], p.text-muted').forEach(el => {
            if(el.id && saved[el.id]) el.textContent = saved[el.id];
        });
    }

    function clearToolState(toolId) {
        localStorage.removeItem(`toolState-${toolId}`);
    }

    // ------------------- Clear Button -------------------
    function addClearButton(container, onClear) {
        if (!container) return;
        container.style.position = 'relative';

        const btn = document.createElement('button');
        btn.type = 'button';
        btn.textContent = 'Clear';
        btn.className = 'btn btn-outline-secondary btn-sm clear-btn';
        btn.style.position = 'absolute';
        btn.style.top = '30px';
        btn.style.right = '10px';
        btn.style.zIndex = '10';
        btn.addEventListener('click', onClear);
        container.appendChild(btn);
    }

    // ------------------- Tool Box Sizing -------------------
    // ⚡ Adjust these values if you want a different default size
    const TOOL_WIDTH = 700;   // width in pixels
    const TOOL_HEIGHT = 550;  // height in pixels
    const TOOL_PADDING = 100;  // padding inside the box

function applyToolBoxStyles(container) {
    if (!container) return;

    // Visual styling only, no width/height
    container.style.boxSizing = 'border-box';
    container.style.overflow = 'visible';
    container.style.border = '1px solid #d6c48a';
    container.style.borderRadius = '16px';
container.style.backgroundColor = '#ffffff'; // pure white
container.style.boxShadow = '0 10px 28px rgba(166,128,35,0.12)'; // soft gold shadow
    container.style.margin = '0 auto 50px auto';
}

    // ------------------- Tools -------------------
    const tools = [
        { id: "WealthForecast", name: "Wealth Forecast" },
        { id: "SavingsAccelerator", name: "Savings Accelerator" },
        { id: "ExpenseLens", name: "Expense Lens" },
        { id: "NetWorth", name: "Net Worth Tracker" },
        { id: "CashFlow", name: "Cash Flow Map" },
        { id: "DebtClarity", name: "Debt Clarity" },
        { id: "FinancialBuffer", name: "Financial Buffer" },
        { id: "WealthProjection", name: "Wealth Projection" },
        { id: "FreedomIndex", name: "Freedom Index" },
        { id: "DebtAssetPulse", name: "Debt vs Asset Pulse" }
    ];

    // Populate dropdown
    tools.forEach(tool => {
        const option = document.createElement("option");
        option.value = tool.id;
        option.textContent = tool.name;
        dropdown.appendChild(option);
    });

    function parsePercent(value) {
        return parseFloat(value.replace('%', '')) / 100 || 0;
    }
    function formatDollar(value) {
        return `$${(+value || 0).toLocaleString()}`;
    }

    // ------------------- Tool Renderer -------------------
    dropdown.addEventListener("change", function () {
        const t = tools.find(x => x.id === this.value);
        embedContainer.innerHTML = '';
        if (!t) return;

if (t.id === "WealthBuildingPotential") {
/* -------------------------------
    1️⃣ WEALTH FORECAST (ELEVATED)
--------------------------------*/
    embedContainer.innerHTML = `
<div class="networth-tool" style="
    background:#ffffff; 
    padding:40px; 
    border-radius:20px; 
    box-shadow:0 12px 35px rgba(166,128,35,0.15); 
    max-width:1200px; 
    margin:0 auto;
    font-family: 'Inter', sans-serif;
">
    <h3 style="color:#a68023; font-weight:900; font-size:2.2rem; margin-bottom:30px; letter-spacing:0.5px;">
        ${t.name}
    </h3>
    <div style="display:flex; flex-wrap:wrap; gap:50px;">
        <!-- Inputs Column -->
        <div style="flex:1; min-width:400px;">
            <label style="font-weight:500; font-size:rem; margin-top:15px;">Annual Income</label>
            <div style="position:relative;">
                <input id="wbIncome" type="text" class="form-control" style="font-weight:700; font-size:1.1rem; color:#a68023; padding-right:30px;" />
                <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#a68023;">$</span>
            </div>

            <label style="font-weight:500; font-size:1rem; margin-top:15px;">Working Period (Years)</label>
            <input id="wbYears" type="text" class="form-control" style="font-weight:700; font-size:1.1rem; color:#a68023;" />

            <label style="font-weight:500; font-size:1rem; margin-top:15px;">Inflation</label>
            <div style="position:relative;">
                <input id="wbInflation" type="text" class="form-control" style="font-weight:700; font-size:1.1rem; color:#a68023; padding-right:30px;" />
                <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#a68023;">%</span>
            </div>

            <label style="font-weight:500; font-size:1rem; margin-top:15px;">After-Tax Rate of Return</label>
            <div style="position:relative;">
                <input id="wbReturn" type="text" class="form-control" style="font-weight:700; font-size:1.1rem; color:#a68023; padding-right:30px;" />
                <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#a68023;">%</span>
            </div>

            <label style="font-weight:500; font-size:1rem; margin-top:15px;">Tax Bracket</label>
            <div style="position:relative;">
                <input id="wbTax" type="text" class="form-control" style="font-weight:700; font-size:1.1rem; color:#a68023; padding-right:30px;" />
                <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#a68023;">%</span>
            </div>

            <label style="font-weight:500; font-size:1rem; margin-top:15px;">Fixed Liabilities</label>
            <div style="position:relative;">
                <input id="wbLiabilities" type="text" class="form-control" style="font-weight:700; font-size:1.1rem; color:#a68023; padding-right:30px;" />
                <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#a68023;">%</span>
            </div>

            <label style="font-weight:500; font-size:1rem; margin-top:15px;">Lifestyle Spending</label>
            <div style="position:relative;">
                <input id="wbLifestyle" type="text" class="form-control" style="font-weight:700; font-size:1.1rem; color:#a68023; padding-right:30px;" />
                <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#a68023;">%</span>
            </div>
        </div>

        <!-- Outputs Column -->
        <div style="flex:1; min-width:400px;">
            <table class="table table-bordered mb-4" style="color:#111; font-weight:700; font-size:1.1rem;">
                <tr><th>Total Earnings</th><td id="wbEarnings" style="color:#222; font-weight:900;">$0</td></tr>
                <tr><th>Projected Wealth</th><td id="wbWealth" style="color:#222; font-weight:900;">$0</td></tr>
                <tr><th>Real Growth Rate</th><td id="wbRealGrowth" style="color:#222; font-weight:900;">0%</td></tr>
                <tr><th>Savings</th><td id="wbSavingsPercent" style="color:#222; font-weight:900;">0%</td></tr>
            </table>

            <table class="table table-bordered" style="color:#111; font-weight:700; font-size:1.1rem;">
                <tr><th>Annual Savings</th><td id="wbActualSavings" style="color:#222; font-weight:900;">$0</td></tr>
                <tr><th>Tips & Suggestions</th><td id="wbSavingsTips" style="font-style:italic; color:#555;">
                    Enter your profile above to calculate savings.
                </td></tr>
            </table>
        </div>
    </div>
</div>`;

    // Grab container and elements
    const container = embedContainer.querySelector('.networth-tool');
    const incomeEl = document.getElementById("wbIncome");
    const yearsEl = document.getElementById("wbYears");
    const inflEl = document.getElementById("wbInflation");
    const retEl = document.getElementById("wbReturn");
    const taxEl = document.getElementById("wbTax");
    const liabEl = document.getElementById("wbLiabilities");
    const lifeEl = document.getElementById("wbLifestyle");

    const earningsOut = document.getElementById("wbEarnings");
    const wealthOut = document.getElementById("wbWealth");
    const realGrowthOut = document.getElementById("wbRealGrowth");
    const savingsPercentOut = document.getElementById("wbSavingsPercent");

    const actualSavingsOut = document.getElementById("wbActualSavings");
    const savingsTipsOut = document.getElementById("wbSavingsTips");

    // Apply visual styles
    applyToolBoxStyles(container);

    // Load saved state AFTER DOM exists
    loadToolState("WealthBuildingPotential");

    // ==============================
    // Format inputs with commas on blur
    // ==============================
    [incomeEl, yearsEl, inflEl, retEl, taxEl, liabEl, lifeEl].forEach(el => {
        el.addEventListener("blur", () => {
            let val = el.value.replace(/,/g, '');
            if (!isNaN(val) && val !== '') {
                el.value = Number(val).toLocaleString();
            }
        });
    });

    // Main calculation function
    function calcWealthBuildingPotential() {
        const income = +incomeEl.value.replace(/,/g,'') || 0;
        const years = +yearsEl.value.replace(/,/g,'') || 0;
        const inflation = (+inflEl.value.replace(/,/g,'') || 0) / 100;
        const nominalReturn = (+retEl.value.replace(/,/g,'') || 0) / 100;
        const tax = (+taxEl.value.replace(/,/g,'') || 0) / 100;
        const liabilities = (+liabEl.value.replace(/,/g,'') || 0) / 100;
        const lifestyle = (+lifeEl.value.replace(/,/g,'') || 0) / 100;

        const savingsRate = Math.max(1 - tax - liabilities - lifestyle, 0);
        const annualSavings = income * savingsRate;
        const realGrowthRate = (1 + nominalReturn) / (1 + inflation) - 1;

        let investedBalance = 0;
        for (let y = 1; y <= years; y++) {
            investedBalance = investedBalance * (1 + realGrowthRate) + annualSavings;
        }

        // Update outputs
        earningsOut.textContent = `$${(income * years).toLocaleString()}`;
        wealthOut.textContent = `$${investedBalance.toLocaleString()}`;
        realGrowthOut.textContent = `${(realGrowthRate * 100).toFixed(2)}%`;
        savingsPercentOut.textContent = `${(savingsRate * 100).toFixed(2)}%`;
        actualSavingsOut.textContent = `$${annualSavings.toLocaleString()}`;

        const sTips = savingsRate < 0.2 
            ? 'Savings potential is low; reduce lifestyle/fixed liabilities.' 
            : 'Savings rate is strong; maximize to grow wealth.';
        savingsTipsOut.textContent = sTips;

        saveToolState("WealthBuildingPotential");
    }

    // Attach input listeners for calculation
    [incomeEl, yearsEl, inflEl, retEl, taxEl, liabEl, lifeEl].forEach(el => {
        el.addEventListener("input", calcWealthBuildingPotential);
    });

    // Clear button
    addClearButton(container, () => {
        [incomeEl, yearsEl, inflEl, retEl, taxEl, liabEl, lifeEl].forEach(el => el.value = '');
        earningsOut.textContent = '$0';
        wealthOut.textContent = '$0';
        realGrowthOut.textContent = '0%';
        savingsPercentOut.textContent = '0%';
        actualSavingsOut.textContent = '$0';
        savingsTipsOut.textContent = 'Enter your profile above to calculate savings.';
        clearToolState("WealthBuildingPotential");
    });

    // Initial calculation
    calcWealthBuildingPotential();
}

/* -------------------------------
    2️⃣ SAVINGS ACCELERATOR (ELEVATED)
--------------------------------*/
if (t.id === "SavingsAccelerator") {
    embedContainer.innerHTML = `
    <div class="networth-tool p-4" 
         style="background:#ffffff; 
                border-radius:20px; 
                box-shadow:0 12px 35px rgba(166,128,35,0.15); 
                border:1px solid rgba(166,128,35,0.35); 
                max-width:1200px; 
                margin:0 auto;
                font-family: 'Inter', sans-serif;">

        <h3 style="color:#a68023; font-weight:900; letter-spacing:0.5px; font-size:2rem;">
            ${t.name}
        </h3>

        <p style="font-style:italic; color:#666; margin-bottom:20px;">
            Calculate your monthly surplus and optimize how you allocate it for maximum wealth building.
        </p>

        <div class="row mb-3" style="display:flex; gap:20px; flex-wrap:wrap;">
            <div style="flex:1; min-width:200px;">
                <label class="form-label fw-bold" style="color:#a68023;">Net Cash Flow</label>
                <div style="position:relative;">
                    <input id="saNet" type="text" class="form-control" placeholder="e.g., 2,000"
                           style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; font-size:1.1rem; color:#a68023; padding-right:30px;" />
                    <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#a68023;">$</span>
                </div>
            </div>
            <div style="flex:1; min-width:200px;">
                <label class="form-label fw-bold" style="color:#a68023;">Essential Expenses</label>
                <div style="position:relative;">
                    <input id="saEss" type="text" class="form-control" placeholder="e.g., 1,500"
                           style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; font-size:1.1rem; color:#a68023; padding-right:30px;" />
                    <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#a68023;">$</span>
                </div>
            </div>
        </div>

        <h5 style="margin-top:10px; font-weight:700;">
            Surplus:
            <span id="saOut" style="color:#222; font-weight:900;">$0</span>
        </h5>

        <div class="mt-4">
            <h5 style="color:#a68023; font-weight:700; border-bottom:1px solid rgba(166,128,35,0.35); padding-bottom:6px;">
                Cash Flow Allocation
            </h5>

            <div class="mb-3" style="font-weight:800; font-size:1.1rem;">
                Total Allocated:
                <span id="saPctTotal" style="color:#a68023; font-size:1.25rem;">0%</span>
            </div>

            <div id="allocationContainer" class="mt-3"></div>

            <div class="d-flex gap-2 mt-3">
                <button id="saAddCat" class="btn btn-outline-gold"
                        style="border:1px solid #a68023; color:#a68023; font-weight:600;">+ Add Category</button>
                <button id="saDelCat" class="btn btn-outline-gold"
                        style="border:1px solid #a68023; color:#a68023; font-weight:600;">- Delete Last</button>
            </div>
        </div>

        <div id="saTips"
             style="padding:14px;
                    background:linear-gradient(135deg, #f1ede3, #e1d6b8);
                    border-left:5px solid #a68023;
                    font-style:italic;
                    color:#333;
                    margin-top:20px;
                    border-radius:10px;
                    box-shadow:inset 0 0 12px rgba(166,128,35,0.25);">
            Direct extra cash strategically across savings, debt reduction, and key priorities.
        </div>
    </div>`;

    const container = embedContainer.querySelector('.networth-tool');
    const saNetInput = document.getElementById('saNet');
    const saEssInput = document.getElementById('saEss');
    const saOut = document.getElementById('saOut');
    const saTips = document.getElementById('saTips');
    const allocationContainer = document.getElementById('allocationContainer');
    const addBtn = document.getElementById('saAddCat');
    const delBtn = document.getElementById('saDelCat');
    const saPctTotal = document.getElementById('saPctTotal');

    let categoryCount = 0;

    const formatNumber = (val) => {
        val = val.toString().replace(/,/g,'');
        return !isNaN(val) && val !== '' ? Number(val).toLocaleString() : '';
    };

    const saveAllocationState = () => {
        const net = saNetInput.value || '';
        const ess = saEssInput.value || '';
        const allocations = [];
        document.querySelectorAll('.allocation-row').forEach(row => {
            allocations.push({
                name: row.querySelector('.allocation-name').value || '',
                percent: row.querySelector('.allocation-percent').value || ''
            });
        });
        localStorage.setItem('SavingsAccelerator', JSON.stringify({ net, ess, allocations }));
    };

    const loadAllocationState = () => {
        allocationContainer.innerHTML = '';
        categoryCount = 0;
        let created = 0;

        const state = JSON.parse(localStorage.getItem('SavingsAccelerator') || '{}');
        saNetInput.value = state.net || '';
        saEssInput.value = state.ess || '';

        (state.allocations || []).forEach(a => {
            createAllocationRow(++categoryCount, a.name, a.percent);
            created++;
        });

        while (created < 3) {
            createAllocationRow(++categoryCount);
            created++;
        }

        refreshSurplus();
    };

    const createAllocationRow = (index, preName = '', prePercent = '') => {
        const row = document.createElement('div');
        row.className = 'allocation-row d-flex align-items-center mb-2 gap-2';
        row.style.cssText = 'background:#fafafa;padding:8px;border-radius:10px;border:1px solid #eee;';

        const name = document.createElement('input');
        name.className = 'form-control allocation-name';
        name.style.flex = '2';
        name.placeholder = `Category ${index}`;
        name.value = preName;
        name.addEventListener('input', saveAllocationState);

        const amtWrap = document.createElement('div');
        amtWrap.style.cssText = 'flex:1;position:relative;';

        const amt = document.createElement('input');
        amt.className = 'form-control allocation-amount';
        amt.readOnly = true;
        amt.style.cssText = 'border:1px solid #d6c48a;font-weight:700;color:#a68023;background:#f3f0e8;';
        amt.value = '';

        const dollar = document.createElement('span');
        dollar.textContent = '$';
        dollar.style.cssText = 'position:absolute;right:10px;top:50%;transform:translateY(-50%);font-weight:700;color:#a68023;';

        amtWrap.appendChild(amt);
        amtWrap.appendChild(dollar);

        const pctWrap = document.createElement('div');
        pctWrap.style.cssText = 'flex:1;position:relative;';

        const pct = document.createElement('input');
        pct.className = 'form-control allocation-percent';
        pct.value = prePercent || '';
        pct.style.cssText = 'font-weight:700;color:#a68023;padding-right:28px;';
        pct.oninput = refreshSurplus;

        const pctSign = document.createElement('span');
        pctSign.textContent = '%';
        pctSign.style.cssText = 'position:absolute;right:10px;top:50%;transform:translateY(-50%);font-weight:700;color:#a68023;';

        pctWrap.appendChild(pct);
        pctWrap.appendChild(pctSign);

        const del = document.createElement('button');
        del.textContent = '✕';
        del.style.cssText = 'border:none;background:transparent;color:#a68023;font-weight:900;cursor:pointer;';
        del.onclick = () => { allocationContainer.removeChild(row); refreshSurplus(); };

        row.append(name, amtWrap, pctWrap, del);
        allocationContainer.appendChild(row);
    };

    const refreshSurplus = () => {
        const net = +saNetInput.value.replace(/,/g,'') || 0;
        const ess = +saEssInput.value.replace(/,/g,'') || 0;
        const surplus = net - ess;
        saOut.textContent = `$${surplus.toLocaleString()}`;

        let usedPct = 0;

        document.querySelectorAll('.allocation-row').forEach(row => {
            const pctInput = row.querySelector('.allocation-percent');
            const amtInput = row.querySelector('.allocation-amount');

            let pct = +pctInput.value || 0;
            if (usedPct + pct > 100) pct = Math.max(0, 100 - usedPct);
            usedPct += pct;

            pctInput.value = pct;
            const amt = surplus > 0 ? (pct / 100) * surplus : 0;
            amtInput.value = amt.toLocaleString();
        });

        saPctTotal.textContent = usedPct.toFixed(1) + '%';

        saTips.textContent = surplus <= 0
            ? '⚠️ Your expenses match or exceed your net cash flow. Adjust your budget or increase income.'
            : '✅ Good surplus! Use surplus funds strategically for savings and financial goals.';

        saveAllocationState();
    };

    saNetInput.oninput = saEssInput.oninput = refreshSurplus;
    saNetInput.onblur = () => saNetInput.value = formatNumber(saNetInput.value);
    saEssInput.onblur = () => saEssInput.value = formatNumber(saEssInput.value);

    addBtn.onclick = () => createAllocationRow(++categoryCount);
    delBtn.onclick = () => {
        const last = allocationContainer.lastElementChild;
        if (last) { allocationContainer.removeChild(last); refreshSurplus(); }
    };

    addClearButton(container, () => {
        saNetInput.value = saEssInput.value = '';
        allocationContainer.innerHTML = '';
        categoryCount = 0;
        for (let i = 0; i < 3; i++) createAllocationRow(++categoryCount);
        saOut.textContent = '$0';
        saPctTotal.textContent = '0%';
        saTips.textContent = 'Direct extra cash strategically across savings, debt reduction, and key priorities.';
        localStorage.removeItem('SavingsAccelerator');
    });

    loadAllocationState();
}


/* -------------------------------
    3️⃣ EXPENSE LENS (ELEVATED)
--------------------------------*/
if (t.id === "ExpenseLens") {
    try {
        embedContainer.innerHTML = `
        <div class="networth-tool p-4" 
             style="background:#ffffff; 
                    border-radius:20px; 
                    box-shadow:0 12px 35px rgba(166,128,35,0.15); 
                    border:1px solid rgba(166,128,35,0.35); 
                    max-width:1200px; margin:0 auto;
                    font-family: 'Inter', sans-serif;">

            <h3 style="color:#a68023; font-weight:900; letter-spacing:0.5px; font-size:2rem;">
                ${t.name}
            </h3>

            <p style="font-style:italic; color:#666; margin-bottom:20px;">
                Break down your income into categories and visualize spending percentages for better budgeting.
            </p>

            <label class="form-label fw-bold" style="color:#a68023;">Total Income</label>
            <div style="position:relative; margin-bottom:15px;">
                <input id="elIncome" type="text" 
                       class="form-control mb-3"
                       placeholder="Enter total monthly income"
                       style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; color:#a68023; padding-right:30px;" />
                <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#a68023;">$</span>
            </div>

            <div id="elCategories" style="margin-top:10px;"></div>

            <div class="d-flex gap-2 mt-3">
                <button id="elAddCat" 
                        class="btn btn-outline-gold"
                        style="border:1px solid #a68023; color:#a68023; font-weight:600;">
                    + Add Category
                </button>
                <button id="elDelCat" 
                        class="btn btn-outline-gold"
                        style="border:1px solid #a68023; color:#a68023; font-weight:600;">
                    - Delete Last
                </button>
            </div>

            <div id="elTips"
                 style="padding:14px; 
                        background:linear-gradient(135deg, #f1ede3, #e1d6b8); 
                        border-left:5px solid #a68023; 
                        font-style:italic; 
                        color:#333; 
                        margin-top:20px; 
                        border-radius:10px;
                        box-shadow:inset 0 0 12px rgba(166,128,35,0.25);">
                Monitor each category to identify areas to save or invest.
            </div>

            <div id="elMargin"
                 style="margin-top:18px;
                        padding:16px;
                        background:#f8f6f0;
                        border-radius:12px;
                        font-weight:800;
                        color:#222;
                        font-size:1.1rem;
                        text-align:center;
                        border:1px solid #dbd9d3;">
                Remaining Balance: $0
            </div>
        </div>`;

        const container = embedContainer.querySelector('.networth-tool');
        const categoriesContainer = document.getElementById("elCategories");
        const addBtn = document.getElementById("elAddCat");
        const delBtn = document.getElementById("elDelCat");
        const elTips = document.getElementById("elTips");
        const elMargin = document.getElementById("elMargin");
        const elIncome = document.getElementById("elIncome");

        let categoryCount = 0;

        // -----------------------------
        // Format numbers with commas
        // -----------------------------
        const formatNumber = (val) => {
            val = val.toString().replace(/,/g,'');
            return !isNaN(val) && val !== '' ? Number(val).toLocaleString() : '';
        };

        // -----------------------------
        // State Handling
        // -----------------------------
        const saveExpenseLensState = () => {
            try {
                const income = elIncome.value || '';
                const categories = [];
                document.querySelectorAll('[id^="elCatRow"]').forEach(row => {
                    const index = row.id.replace('elCatRow', '');
                    const name = document.getElementById(`elCatName${index}`).value || '';
                    const amount = document.getElementById(`elCatAmount${index}`).value || '';
                    categories.push({ index, name, amount });
                });
                const state = { income, categories };
                localStorage.setItem('ExpenseLens', JSON.stringify(state));
            } catch (e) { console.error(e); }
        };

        const loadExpenseLensState = () => {
            try {
                const stateStr = localStorage.getItem('ExpenseLens');
                categoriesContainer.innerHTML = '';
                categoryCount = 0;
                let categoriesCreated = 0;

                if (stateStr) {
                    const state = JSON.parse(stateStr);
                    elIncome.value = state.income || '';

                    if (state.categories && state.categories.length > 0) {
                        state.categories.forEach(cat => {
                            createCategoryRow(++categoryCount, cat.name, cat.amount);
                            categoriesCreated++;
                        });
                    }
                }
                if (categoriesCreated === 0) createCategoryRow(++categoryCount);
                refreshExpenseLens();
            } catch (e) { console.error(e); }
        };

        const clearExpenseLensState = () => localStorage.removeItem('ExpenseLens');

        // -----------------------------
        // Create Category Row
        // -----------------------------
        const createCategoryRow = (index, preName = '', preAmount = '') => {
            const div = document.createElement("div");
            div.className = "d-flex align-items-center mb-2 gap-2";
            div.id = `elCatRow${index}`;
            div.style.background = "#fafafa";
            div.style.padding = "8px";
            div.style.borderRadius = "10px";
            div.style.border = "1px solid #eee";

            const nameInput = document.createElement("input");
            nameInput.type = "text";
            nameInput.id = `elCatName${index}`;
            nameInput.className = "form-control flex-grow-1";
            nameInput.placeholder = `Category ${index} Name`;
            nameInput.style.border = "1px solid #ddd";
            nameInput.style.color = "#a68023";
            nameInput.value = preName;
            nameInput.addEventListener("input", saveExpenseLensState);

            const amountWrapper = document.createElement("div");
            amountWrapper.style.position = "relative";
            amountWrapper.style.flex = "0 0 120px";

            const amountInput = document.createElement("input");
            amountInput.type = "text";
            amountInput.id = `elCatAmount${index}`;
            amountInput.className = "form-control";
            amountInput.placeholder = "Amount";
            amountInput.style.width = "100%";
            amountInput.style.border = "1px solid #d6c48a";
            amountInput.style.fontWeight = "700";
            amountInput.style.color = "#a68023";
            amountInput.value = preAmount;

            const dollarSpan = document.createElement("span");
            dollarSpan.textContent = "$";
            dollarSpan.style.position = "absolute";
            dollarSpan.style.right = "10px";
            dollarSpan.style.top = "50%";
            dollarSpan.style.transform = "translateY(-50%)";
            dollarSpan.style.fontWeight = "700";
            dollarSpan.style.color = "#a68023";

            amountWrapper.appendChild(amountInput);
            amountWrapper.appendChild(dollarSpan);

            const percentSpan = document.createElement("span");
            percentSpan.id = `elOut${index}`;
            percentSpan.style.minWidth = "80px";
            percentSpan.style.textAlign = "right";
            percentSpan.style.fontWeight = "700";
            percentSpan.style.color = "#a68023";

            const deleteBtn = document.createElement("button");
            deleteBtn.textContent = "✕";
            deleteBtn.style.border = "none";
            deleteBtn.style.background = "transparent";
            deleteBtn.style.color = "#a68023";
            deleteBtn.style.fontWeight = "900";
            deleteBtn.style.cursor = "pointer";
            deleteBtn.addEventListener("click", () => {
                categoriesContainer.removeChild(div);
                refreshExpenseLens();
            });

            // Format numbers with commas on blur
            amountInput.addEventListener("blur", () => {
                amountInput.value = formatNumber(amountInput.value);
            });

            amountInput.addEventListener("input", refreshExpenseLens);

            div.appendChild(nameInput);
            div.appendChild(amountWrapper);
            div.appendChild(percentSpan);
            div.appendChild(deleteBtn);
            categoriesContainer.appendChild(div);

            if(preAmount) refreshExpenseLens();
        };

        // -----------------------------
        // Refresh Function
        // -----------------------------
        const refreshExpenseLens = () => {
            const income = +elIncome.value.replace(/,/g,'') || 0;
            let totalSpent = 0;
            document.querySelectorAll('[id^="elCatAmount"]').forEach(input => {
                const val = +input.value.replace(/,/g,'') || 0;
                totalSpent += val;
                const index = input.id.replace('elCatAmount','');
                document.getElementById(`elOut${index}`).textContent = income > 0 ? ((val/income)*100).toFixed(1)+'%' : '0%';
            });

            elMargin.textContent = `Remaining Balance: $${(income - totalSpent).toLocaleString()}`;

            const pct = income > 0 ? (totalSpent / income * 100).toFixed(1) : 0;
            if(pct > 100) elTips.textContent = `⚠️ You are overspending by ${pct - 100}% of your income!`;
            else if(pct > 80) elTips.textContent = `You are spending ${pct}% of your income. Consider reducing your spending.`;
            else elTips.textContent = `✅ You are spending ${pct}% of your income. Good balance!`;

            saveExpenseLensState();
        };

        // -----------------------------
        // Event Listeners
        // -----------------------------
        elIncome.addEventListener("input", refreshExpenseLens);
        elIncome.addEventListener("blur", () => { elIncome.value = formatNumber(elIncome.value); });

        addBtn.addEventListener("click", () => createCategoryRow(++categoryCount));
        delBtn.addEventListener("click", () => {
            const lastRow = categoriesContainer.lastElementChild;
            if(lastRow){
                categoriesContainer.removeChild(lastRow);
                refreshExpenseLens();
            }
        });

        addClearButton(container, () => {
            elIncome.value = '';
            categoriesContainer.innerHTML = '';
            categoryCount = 0;
            createCategoryRow(++categoryCount);
            elTips.textContent = 'Monitor each category to identify areas to save or invest.';
            elMargin.textContent = 'Remaining Balance: $0';
            clearExpenseLensState();
        });

        loadExpenseLensState();

    } catch (e) {
        console.error('ExpenseLens initialization error:', e);
    }
}

/* -------------------------------
    4️⃣ NET WORTH (ELEVATED)
--------------------------------*/
if (t.id === "NetWorth") {
    embedContainer.innerHTML = `
  <div class="networth-tool p-4" 
       style="background:#ffffff;
              border-radius:20px;
              box-shadow:0 12px 35px rgba(166,128,35,0.15);
              max-width:1200px; 
              margin:0 auto;
              font-family: 'Inter', sans-serif;">
      
        <h3 class="fw-bold mb-3" style="color:#a68023; font-weight:900; letter-spacing:0.5px; font-size:2rem;">
            ${t.name}
        </h3>

        <p style="font-style:italic; color:#666; margin-bottom:18px;">
            Track your total assets, liabilities, and net worth. See insights to grow your wealth.
        </p>

        <div class="row mb-3" style="display:flex; gap:20px; flex-wrap:wrap;">
            <div style="flex:1; min-width:200px;">
                <label class="form-label fw-bold" style="color:#a68023;">Total Assets</label>
                <div style="position:relative;">
                    <input id="assets" type="text" class="form-control" placeholder="e.g., 150,000"
                           style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; font-size:1.1rem; color:#a68023; padding-right:30px;" />
                    <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#a68023;">$</span>
                </div>
            </div>
            <div style="flex:1; min-width:200px;">
                <label class="form-label fw-bold" style="color:#a68023;">Total Liabilities</label>
                <div style="position:relative;">
                    <input id="liabs" type="text" class="form-control" placeholder="e.g., 50,000"
                           style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; font-size:1.1rem; color:#a68023; padding-right:30px;" />
                    <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#a68023;">$</span>
                </div>
            </div>
        </div>

        <table class="table mt-3"
               style="background:#fafafa;
                      border-radius:12px;
                      overflow:hidden;
                      border:1px solid #eee; font-weight:700; font-size:1.1rem; color:#222;">
            <tr>
                <th>Assets</th>
                <th>Liabilities</th>
                <th>Net Worth</th>
            </tr>
            <tr>
                <td id="aVal">$0</td>
                <td id="lVal">$0</td>
                <td id="nVal">$0</td>
            </tr>
        </table>

        <table class="table mt-3"
               style="background:#fafafa;
                      border-radius:12px;
                      overflow:hidden;
                      border:1px solid #eee; font-weight:700; font-size:1.1rem; color:#222;">
            <tr>
                <th>Net Worth to Assets Ratio</th>
                <td id="nwRatio">0%</td>
            </tr>
            <tr>
                <th>Liabilities to Assets Ratio</th>
                <td id="liabRatio">0%</td>
            </tr>
            <tr>
                <th>Wealth Status</th>
                <td id="wealthStatus">—</td>
            </tr>
        </table>

        <div id="nwTips"
             style="padding:12px;
                    background:linear-gradient(135deg,#f1f3f6,#e4e7ec);
                    border-left:4px solid #a68023;
                    border-radius:8px;
                    font-style:italic;
                    color:#333;
                    margin-top:14px;">
            Enter your assets and liabilities to get personalized insights.
        </div>

    </div>`;

    const container = embedContainer.querySelector('.networth-tool');
    loadToolState('NetWorth');

    const assets = document.getElementById('assets');
    const liabs = document.getElementById('liabs');
    const aVal = document.getElementById('aVal');
    const lVal = document.getElementById('lVal');
    const nVal = document.getElementById('nVal');

    const nwRatio = document.getElementById('nwRatio');
    const liabRatio = document.getElementById('liabRatio');
    const wealthStatus = document.getElementById('wealthStatus');
    const nwTips = document.getElementById('nwTips');

    // ==============================
    // Format inputs with commas on blur
    // ==============================
    [assets, liabs].forEach(el => {
        el.addEventListener("blur", () => {
            let val = el.value.replace(/,/g, '');
            if (!isNaN(val) && val !== '') {
                el.value = Number(val).toLocaleString();
            }
        });
    });

    addClearButton(container, () => {
        assets.value = liabs.value = '';
        aVal.textContent = lVal.textContent = nVal.textContent = '$0';
        nwRatio.textContent = liabRatio.textContent = '0%';
        wealthStatus.textContent = '—';
        nwTips.textContent = 'Enter your assets and liabilities to get personalized insights.';
        clearToolState('NetWorth');
    });

    function formatDollar(val) {
        return `$${val.toLocaleString()}`;
    }

    function calc() {
        const a = +assets.value.replace(/,/g,'') || 0;
        const l = +liabs.value.replace(/,/g,'') || 0;
        const net = a - l;

        aVal.textContent = formatDollar(a);
        lVal.textContent = formatDollar(l);
        nVal.textContent = formatDollar(net);

        const ratio = a > 0 ? (net / a) * 100 : 0;
        const liabR = a > 0 ? (l / a) * 100 : 0;
        nwRatio.textContent = `${ratio.toFixed(1)}%`;
        liabRatio.textContent = `${liabR.toFixed(1)}%`;

        let status = '';
        if (net <= 0) status = '⚠️Negative Net Worth';
        else if (ratio < 25) status = '🔹 Early Stage';
        else if (ratio < 50) status = '🔸 Growing';
        else if (ratio < 75) status = '⭐ Solid';
        else status = 'Wealthy';
        wealthStatus.textContent = status;

        let tips = '';
        if (ratio < 25) tips += '💡 Focus on reducing liabilities and increasing savings.\n';
        else if (ratio < 50) tips += 'Your net worth is growing steadily; Maintain consistent financial habits.\n';
        else tips += '✅ Strong net worth! Continue smart asset allocation to preserve and grow wealth.\n';

        if (liabR > 50) tips += '⚠️ High liabilities relative to assets; consider risk mitigation planning.\n';
        nwTips.textContent = tips.trim();

        saveToolState('NetWorth');
    }

    assets.oninput = liabs.oninput = calc;
}

/* -------------------------------
    5️⃣ CASH FLOW MAP (ELEVATED)
--------------------------------*/
if (t.id === "CashFlow") {
    embedContainer.innerHTML = `
   <div class="networth-tool p-4"
        style="background:#ffffff;
               border-radius:20px;
               box-shadow:0 12px 35px rgba(166,128,35,0.15);
               max-width:1200px; 
               margin:0 auto;
               font-family: 'Inter', sans-serif;">
       
        <h3 style="color:#a68023; font-weight:900; letter-spacing:0.5px; font-size:2rem;">
            ${t.name}
        </h3>

        <p style="font-style:italic; color:#666; margin-bottom:18px;">
            Understand your monthly cash flow and uncover opportunities to save or invest.
        </p>

        <div class="row mb-3" style="display:flex; gap:20px; flex-wrap:wrap;">
            <div style="flex:1; min-width:200px;">
                <label class="form-label fw-bold" style="color:#a68023;">Monthly Income</label>
                <div style="position:relative;">
                    <input id="cfIncome" type="text" class="form-control"
                           placeholder="e.g., 5,000"
                           style="border:1px solid #d6c48a;
                                  box-shadow:inset 0 0 6px rgba(166,128,35,0.15);
                                  font-weight:700; font-size:1.1rem; color:#a68023; padding-right:30px;" />
                    <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#a68023;">$</span>
                </div>
            </div>
            <div style="flex:1; min-width:200px;">
                <label class="form-label fw-bold" style="color:#a68023;">Monthly Bills</label>
                <div style="position:relative;">
                    <input id="cfBills" type="text" class="form-control"
                           placeholder="e.g., 2,500"
                           style="border:1px solid #d6c48a;
                                  box-shadow:inset 0 0 6px rgba(166,128,35,0.15);
                                  font-weight:700; font-size:1.1rem; color:#a68023; padding-right:30px;" />
                    <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#a68023;">$</span>
                </div>
            </div>
        </div>

        <h5 style="font-weight:700; margin-top:6px;">
            Net Cash Flow:
            <span id="cfResult" style="color:#222; font-weight:900;">$0</span>
        </h5>

        <table class="table mt-3"
               style="background:#fafafa;
                      border-radius:12px;
                      overflow:hidden;
                      border:1px solid #eee; font-weight:700; font-size:1.1rem; color:#222;">
            <tr>
                <th style="width:50%; background:#f3f3f3;">Savings Potential</th>
                <td id="cfSavingsPotential">$0</td>
            </tr>
            <tr>
                <th style="background:#f3f3f3;">Suggested Allocation</th>
                <td id="cfInvestPct">0%</td>
            </tr>
        </table>

        <div id="cfTips"
             style="padding:12px;
                    background:linear-gradient(135deg,#f1f3f6,#e4e7ec);
                    border-left:4px solid #a68023;
                    border-radius:8px;
                    font-style:italic;
                    color:#333;
                    margin-top:14px;">
            Enter your monthly income and bills to get personalized tips.
        </div>
    </div>`;

    const container = embedContainer.querySelector('.networth-tool');
    loadToolState('CashFlow');

    const cfIncome = document.getElementById('cfIncome');
    const cfBills = document.getElementById('cfBills');
    const cfResult = document.getElementById('cfResult');

    const cfSavingsPotential = document.getElementById('cfSavingsPotential');
    const cfInvestPct = document.getElementById('cfInvestPct');
    const cfTips = document.getElementById('cfTips');

    // Format inputs with commas on blur
    [cfIncome, cfBills].forEach(el => {
        el.addEventListener("blur", () => {
            let val = el.value.replace(/,/g, '');
            if (!isNaN(val) && val !== '') {
                el.value = Number(val).toLocaleString();
            }
        });
    });

    addClearButton(container, () => {
        cfIncome.value = cfBills.value = '';
        cfResult.textContent = '$0';
        cfSavingsPotential.textContent = '$0';
        cfInvestPct.textContent = '0%';
        cfTips.textContent = 'Enter your monthly income and bills to get personalized tips.';
        clearToolState('CashFlow');
    });

    function formatDollar(val) {
        return `$${val.toLocaleString()}`;
    }

    function calcCashFlow() {
        const income = +cfIncome.value.replace(/,/g,'') || 0;
        const bills = +cfBills.value.replace(/,/g,'') || 0;
        const net = income - bills;

        cfResult.textContent = formatDollar(net);

        const savingsPotential = Math.max(net * 0.5, 0);
        const investPct = income > 0 ? Math.min((net / income) * 100, 100).toFixed(0) : 0;

        cfSavingsPotential.textContent = formatDollar(savingsPotential);
        cfInvestPct.textContent = `${investPct}%`;

        let tips = '';
        if (net <= 0)
            tips = '⚠️ Your expenses exceed or equal your income. Reduce bills or increase income.';
        else if (net < income * 0.2)
            tips = '💡 Your net cash flow is tight. Focus on budgeting and increasing savings.';
        else
            tips = '✅ Strong cash flow. Use surplus funds strategically for savings and financial goals.';

        cfTips.textContent = tips;

        saveToolState('CashFlow');
    }

    cfIncome.oninput = cfBills.oninput = calcCashFlow;
}

/* -------------------------------
    6️⃣ DEBT CLARITY (ELEVATED)
--------------------------------*/
if (t.id === "DebtClarity") {
    embedContainer.innerHTML = `
   <div class="networth-tool p-4"
        style="background:#ffffff;
               border-radius:20px;
               box-shadow:0 12px 35px rgba(166,128,35,0.15);
               max-width:1200px; 
               margin:0 auto;
               font-family: 'Inter', sans-serif;">
       
        <h3 style="color:#a68023; font-weight:900; letter-spacing:0.5px; font-size:2rem;">
            ${t.name}
        </h3>

        <p style="font-style:italic; color:#666; margin-bottom:18px;">
            Quickly calculate your Debt-to-Income (DTI) ratio and get actionable guidance.
        </p>

        <div class="row mb-3" style="display:flex; gap:20px; flex-wrap:wrap;">
            <div style="flex:1; min-width:200px;">
                <label class="form-label fw-bold" style="color:#a68023;">Total Liabilities</label>
                <div style="position:relative;">
                    <input id="dcDebt" type="text" class="form-control"
                           placeholder="e.g., 40,000"
                           style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; font-size:1.1rem; color:#a68023; padding-right:30px;" />
                    <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#a68023;">$</span>
                </div>
            </div>
            <div style="flex:1; min-width:200px;">
                <label class="form-label fw-bold" style="color:#a68023;">Annual Income</label>
                <div style="position:relative;">
                    <input id="dcIncome" type="text" class="form-control"
                           placeholder="e.g., 80,000"
                           style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; font-size:1.1rem; color:#a68023; padding-right:30px;" />
                    <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#a68023;">$</span>
                </div>
            </div>
        </div>

        <h5 style="font-weight:700; margin-top:8px;">
            DTI Ratio:
            <span id="dcResult" style="color:#222; font-weight:900;">0%</span>
        </h5>

        <table class="table mt-3"
               style="background:#fafafa;
                      border-radius:12px;
                      overflow:hidden;
                      border:1px solid #eee; font-weight:700; font-size:1.1rem; color:#222;">
            <tr>
                <th style="width:40%; background:#f3f3f3;">DTI Status</th>
                <td id="dcStatus">—</td>
            </tr>
            <tr>
                <th style="background:#f3f3f3;">Recommendation</th>
                <td id="dcTips">Enter your liabilities and income to receive guidance.</td>
            </tr>
        </table>
    </div>`;

    const container = embedContainer.querySelector('.networth-tool');
    loadToolState('DebtClarity');

    const dcDebt = document.getElementById('dcDebt');
    const dcIncome = document.getElementById('dcIncome');
    const dcResult = document.getElementById('dcResult');
    const dcStatus = document.getElementById('dcStatus');
    const dcTips = document.getElementById('dcTips');

    // Format inputs with commas on blur
    [dcDebt, dcIncome].forEach(el => {
        el.addEventListener("blur", () => {
            let val = el.value.replace(/,/g, '');
            if (!isNaN(val) && val !== '') {
                el.value = Number(val).toLocaleString();
            }
        });
    });

    addClearButton(container, () => {
        dcDebt.value = dcIncome.value = '';
        dcResult.textContent = '0%';
        dcStatus.textContent = '—';
        dcTips.textContent = 'Enter your liabilities and income to receive guidance.';
        clearToolState('DebtClarity');
    });

    function calcDebtClarity() {
        const debt = +dcDebt.value.replace(/,/g,'') || 0;
        const income = +dcIncome.value.replace(/,/g,'') || 1;
        const dti = ((debt / income) * 100).toFixed(1);

        dcResult.textContent = `${dti}%`;

        let status = '';
        let tips = '';

        if (dti > 50) {
            status = '⚠️ High DTI';
            tips = 'Work toward increasing your assets and reduce debt over time through risk mitigationto avoid new liabilities.';
        } else if (dti > 30) {
            status = '🔹 Moderate DTI';
            tips = 'Monitor spending, pay down debt strategically.';
        } else {
            status = '✅ Healthy DTI';
            tips = 'Good balance. Continue disciplined financial habits.';
        }

        dcStatus.textContent = status;
        dcTips.textContent = tips;

        saveToolState('DebtClarity');
    }

    dcDebt.oninput = dcIncome.oninput = calcDebtClarity;
}

/* -------------------------------
    7️⃣ FINANCIAL BUFFER (ENHANCED & ELEVATED)
--------------------------------*/
if (t.id === "FinancialBuffer") {
    embedContainer.innerHTML = `
    <div class="networth-tool p-4"
         style="background:#ffffff;
                border-radius:20px;
                box-shadow:0 12px 35px rgba(166,128,35,0.12);
                border:1px solid rgba(166,128,35,0.35);
                max-width:600px;
                margin:0 auto;
                font-family:'Inter',sans-serif;">

        <h3 style="color:#a68023; font-weight:900; letter-spacing:0.5px; margin-bottom:12px; font-size:1.8rem;">
            ${t.name}
        </h3>

        <p style="font-style:italic; color:#555; margin-bottom:18px;">
            Build a financial safety net to protect yourself from unexpected expenses.
        </p>

        <label class="form-label fw-bold" style="font-weight:600; color:#444;">Monthly Bills</label>
        <div style="position:relative; margin-bottom:15px;">
            <input id="fbBills" type="text" class="form-control mb-3" placeholder="e.g., 2,500"
                   style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; color:#a68023; padding-right:30px;" />
            <span style="position:absolute; right:10px; top:50%; transform:translateY(-50%); font-weight:700; color:#a68023;">$</span>
        </div>

        <div class="mb-3">
            <h5 style="margin-bottom:6px;">1 Month Goal: <span id="fb1">$0</span></h5>
            <h5 style="margin-bottom:6px;">3–6 Month Goal: <span id="fb3">$0</span></h5>
            <h5 style="margin-bottom:6px;">12 Month Goal: <span id="fb12">$0</span></h5>
        </div>

        <div id="fbTips"
             style="padding:12px;
                    background:linear-gradient(135deg,#f1f3f6,#e4e7ec);
                    border-left:4px solid #a68023;
                    border-radius:8px;
                    font-style:italic;
                    color:#333;
                    margin-top:14px;">
            Tip: Save consistently each month to build your buffer. Consider automating transfers to a separate emergency account.
        </div>
    </div>`;

    const container = embedContainer.querySelector('.networth-tool');
    loadToolState('FinancialBuffer');

    const fbBillsInput = document.getElementById('fbBills');
    const fb1 = document.getElementById('fb1');
    const fb3 = document.getElementById('fb3');
    const fb12 = document.getElementById('fb12');
    const fbTips = document.getElementById('fbTips');

    const formatWithCommas = (val) => val ? (+val).toLocaleString() : '0';

    addClearButton(container, () => {
        fbBillsInput.value = '';
        fb1.textContent = '$0';
        fb3.textContent = '$0';
        fb12.textContent = '$0';
        fbTips.textContent = 'Tip: Save consistently each month to build your buffer. Consider automating transfers to a separate emergency account.';
        clearToolState('FinancialBuffer');
    });

    const updateBuffer = () => {
        let bills = +fbBillsInput.value.toString().replace(/,/g,'') || 0;

        fb1.textContent = `$${formatWithCommas(bills)}`;
        fb3.textContent = `$${formatWithCommas(bills * 6)}`;
        fb12.textContent = `$${formatWithCommas(bills * 12)}`;

        if(bills <= 0) fbTips.textContent = '⚠️ Enter your monthly bills to calculate your buffer goals.';
        else if(bills < 1000) fbTips.textContent = 'Your bills are low; consider using this buffer to accelerate growth.';
        else fbTips.textContent = '✅ Your buffer goals are ready. Automate savings to reach these targets efficiently.';

        saveToolState('FinancialBuffer');
    };

    fbBillsInput.addEventListener('input', updateBuffer);
    fbBillsInput.addEventListener('blur', () => {
        let bills = +fbBillsInput.value.toString().replace(/,/g,'') || 0;
        fbBillsInput.value = bills ? bills.toLocaleString() : '';
        updateBuffer();
    });
}


/* -------------------------------
    8️⃣ WEALTH PROJECTION (ENHANCED & ELEVATED)
--------------------------------*/
if (t.id === "WealthProjection") {
    embedContainer.innerHTML = `
    <div class="networth-tool p-4"
         style="background:#ffffff;
                border-radius:20px;
                box-shadow:0 12px 35px rgba(166,128,35,0.15);
                border:1px solid rgba(166,128,35,0.35);
                max-width:600px;
                margin:0 auto;
                font-family:'Inter',sans-serif;">

        <h3 style="color:#a68023; font-weight:900; letter-spacing:0.5px; margin-bottom:12px; font-size:1.8rem;">
            ${t.name}
        </h3>

        <p style="font-style:italic; color:#555; margin-bottom:18px;">
            Project your net worth growth based on current savings and surplus. Visualize both short and long-term potential.
        </p>

        <label class="form-label fw-bold" style="color:#444;">Current Net Worth</label>
        <input id="wpNet" type="text" class="form-control mb-2" placeholder="e.g., 50,000"
               style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; color:#a68023;" />

        <label class="form-label fw-bold" style="color:#444;">Monthly Surplus</label>
        <input id="wpSurplus" type="text" class="form-control mb-2" placeholder="e.g., 2,000"
               style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; color:#a68023;" />

        <label class="form-label fw-bold" style="color:#444;">Custom Months</label>
        <input id="wpMonths" type="number" class="form-control mb-3" placeholder="e.g., 18"
               style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; color:#a68023;" />

        <div style="background:#fafafa; border-radius:12px; padding:14px; border:1px solid #eee; margin-bottom:10px;">
            <h5 style="font-weight:700;">
                Projected Net Worth (Custom Months): 
                <span id="wpOut" style="color:#a68023; font-weight:800;">$0</span>
            </h5>
            <h6 style="margin-top:8px;">
                Projection in 6 Months: 
                <span id="wp6" style="color:#a68023; font-weight:700;">$0</span>
            </h6>
            <h6>
                Projection in 12 Months: 
                <span id="wp12" style="color:#a68023; font-weight:700;">$0</span>
            </h6>
        </div>

        <div id="wpTips"
             style="padding:14px; 
                    background:linear-gradient(135deg, #f1ede3, #e1d6b8); 
                    border-left:5px solid #a68023; 
                    font-style:italic; 
                    color:#333; 
                    margin-top:15px; 
                    border-radius:10px;
                    box-shadow:inset 0 0 12px rgba(166,128,35,0.25);">
            Tip: Regularly increase your monthly surplus to accelerate your wealth growth.
        </div>
    </div>`;

    const container = embedContainer.querySelector('.networth-tool');

    const wpNet = document.getElementById('wpNet');
    const wpSurplus = document.getElementById('wpSurplus');
    const wpMonths = document.getElementById('wpMonths');
    const wpOut = document.getElementById('wpOut');
    const wp6 = document.getElementById('wp6');
    const wp12 = document.getElementById('wp12');
    const wpTips = document.getElementById('wpTips');

    const formatWithCommas = (val) => val ? (+val).toLocaleString() : '0';
    const parseNumber = (val) => +val.toString().replace(/,/g,'') || 0;

    // --- PERSISTENCE ---
    const loadWP = () => {
        const state = JSON.parse(localStorage.getItem('WealthProjection') || '{}');
        if(state.wpNet) wpNet.value = state.wpNet;
        if(state.wpSurplus) wpSurplus.value = state.wpSurplus;
        if(state.wpMonths) wpMonths.value = state.wpMonths;
        if(state.wpOut) wpOut.textContent = state.wpOut;
        if(state.wp6) wp6.textContent = state.wp6;
        if(state.wp12) wp12.textContent = state.wp12;
        if(state.wpTips) wpTips.textContent = state.wpTips;
    };
    const saveWP = () => {
        localStorage.setItem('WealthProjection', JSON.stringify({
            wpNet: wpNet.value,
            wpSurplus: wpSurplus.value,
            wpMonths: wpMonths.value,
            wpOut: wpOut.textContent,
            wp6: wp6.textContent,
            wp12: wp12.textContent,
            wpTips: wpTips.textContent
        }));
    };
    loadWP();

    addClearButton(container, () => {
        wpNet.value = wpSurplus.value = wpMonths.value = '';
        wpOut.textContent = wp6.textContent = wp12.textContent = '$0';
        wpTips.textContent = 'Tip: Regularly increase your monthly surplus to accelerate your wealth growth.';
        localStorage.removeItem('WealthProjection');
    });

    const updateWealthProjection = () => {
        let net = parseNumber(wpNet.value);
        let surplus = parseNumber(wpSurplus.value);
        let months = +wpMonths.value || 0;

        wpOut.textContent = `$${formatWithCommas(net + surplus * months)}`;
        wp6.textContent = `$${formatWithCommas(net + surplus * 6)}`;
        wp12.textContent = `$${formatWithCommas(net + surplus * 12)}`;

        if(net <= 0 && surplus <= 0) wpTips.textContent = '⚠️ Enter your current net worth and surplus to see projections.';
        else if(surplus <= 0) wpTips.textContent = '⚠️ Your surplus is zero; focus on increasing your savings to grow wealth.';
        else wpTips.textContent = '✅ Good! Keep adding to your surplus consistently to maximize growth over time.';

        saveWP();
    };

    [wpNet, wpSurplus, wpMonths].forEach(input => {
        input.addEventListener('input', updateWealthProjection);
        input.addEventListener('blur', () => {
            if(input.id !== 'wpMonths') input.value = parseNumber(input.value).toLocaleString();
            updateWealthProjection();
        });
    });
}

/* -------------------------------
    9️⃣ FREEDOM INDEX (ENHANCED & ELEVATED)
--------------------------------*/
if (t.id === "FreedomIndex") {
    embedContainer.innerHTML = `
    <div class="networth-tool p-4"
         style="background:#ffffff; border-radius:20px; box-shadow:0 12px 35px rgba(166,128,35,0.15); border:1px solid rgba(166,128,35,0.35); max-width:600px; margin:0 auto; font-family:'Inter',sans-serif;">
        <h3 style="color:#a68023; font-weight:900; letter-spacing:0.5px; margin-bottom:12px; font-size:1.8rem;">${t.name}</h3>
        <p style="font-style:italic; color:#555; margin-bottom:18px;">Measure your financial freedom: how long you could live off your net worth and passive income.</p>

        <label class="form-label fw-bold" style="color:#444;">Net Worth</label>
        <input id="fiNet" type="text" class="form-control mb-2" placeholder="e.g., 150,000" style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; color:#a68023;" />

        <label class="form-label fw-bold" style="color:#444;">Annual Expenses</label>
        <input id="fiExp" type="text" class="form-control mb-2" placeholder="e.g., 50,000" style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; color:#a68023;" />

        <label class="form-label fw-bold" style="color:#444;">Passive Income (Optional)</label>
        <input id="fiPassive" type="text" class="form-control mb-3" placeholder="e.g., 10,000" style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; color:#a68023;" />

        <h5 style="font-weight:700; margin-top:10px;">Freedom Index: <span id="fiOut" style="color:#a68023; font-weight:800;">0</span></h5>

        <table class="table mt-3" style="background:#fafafa; border-radius:12px; overflow:hidden; border:1px solid #eee;">
            <tr><th style="width:45%; background:#f3f3f3;">Net Worth</th><td id="fiNetOut">$0</td></tr>
            <tr><th style="background:#f3f3f3;">Annual Expenses</th><td id="fiExpOut">$0</td></tr>
            <tr><th style="background:#f3f3f3;">Passive Income</th><td id="fiPassiveOut">$0</td></tr>
            <tr><th style="background:#f3f3f3;">Months of Freedom</th><td id="fiMonths">0</td></tr>
        </table>

        <div id="fiAdvice" style="padding:14px; background:linear-gradient(135deg, #f1ede3, #e1d6b8); border-left:5px solid #a68023; font-style:italic; color:#333; margin-top:15px; border-radius:10px; box-shadow:inset 0 0 12px rgba(166,128,35,0.25);">
            Enter your values to see recommendations.
        </div>
    </div>`;

    const fiNet = document.getElementById('fiNet');
    const fiExp = document.getElementById('fiExp');
    const fiPassive = document.getElementById('fiPassive');
    const fiOut = document.getElementById('fiOut');
    const fiNetOut = document.getElementById('fiNetOut');
    const fiExpOut = document.getElementById('fiExpOut');
    const fiPassiveOut = document.getElementById('fiPassiveOut');
    const fiMonths = document.getElementById('fiMonths');
    const fiAdvice = document.getElementById('fiAdvice');

    const formatWithCommas = (val) => val ? (+val).toLocaleString() : '0';
    const parseNumber = (val) => +val.toString().replace(/,/g,'') || 0;

    // --- PERSISTENCE ---
    const loadFI = () => {
        const state = JSON.parse(localStorage.getItem('FreedomIndex') || '{}');
        if(state.fiNet) fiNet.value = state.fiNet;
        if(state.fiExp) fiExp.value = state.fiExp;
        if(state.fiPassive) fiPassive.value = state.fiPassive;
        if(state.fiOut) fiOut.textContent = state.fiOut;
        if(state.fiNetOut) fiNetOut.textContent = state.fiNetOut;
        if(state.fiExpOut) fiExpOut.textContent = state.fiExpOut;
        if(state.fiPassiveOut) fiPassiveOut.textContent = state.fiPassiveOut;
        if(state.fiMonths) fiMonths.textContent = state.fiMonths;
        if(state.fiAdvice) fiAdvice.textContent = state.fiAdvice;
    };
    const saveFI = () => {
        localStorage.setItem('FreedomIndex', JSON.stringify({
            fiNet: fiNet.value,
            fiExp: fiExp.value,
            fiPassive: fiPassive.value,
            fiOut: fiOut.textContent,
            fiNetOut: fiNetOut.textContent,
            fiExpOut: fiExpOut.textContent,
            fiPassiveOut: fiPassiveOut.textContent,
            fiMonths: fiMonths.textContent,
            fiAdvice: fiAdvice.textContent
        }));
    };
    loadFI();

    addClearButton(embedContainer.querySelector('.networth-tool'), () => {
        fiNet.value = fiExp.value = fiPassive.value = '';
        fiOut.textContent = '0';
        fiNetOut.textContent = fiExpOut.textContent = fiPassiveOut.textContent = '$0';
        fiMonths.textContent = '0';
        fiAdvice.textContent = 'Enter your values to see recommendations.';
        localStorage.removeItem('FreedomIndex');
    });

    const updateFreedom = () => {
        const net = parseNumber(fiNet.value);
        const exp = parseNumber(fiExp.value) || 1;
        const passive = parseNumber(fiPassive.value);

        fiNetOut.textContent = `$${formatWithCommas(net)}`;
        fiExpOut.textContent = `$${formatWithCommas(exp)}`;
        fiPassiveOut.textContent = `$${formatWithCommas(passive)}`;

        const fi = (net / exp).toFixed(1);
        fiOut.textContent = fi;

        const months = Math.floor(((net + passive * 12) / exp) * 12);
        fiMonths.textContent = months;

        let advice = '';
        if(fi < 3) advice = '⚠️ Urgent: Increase savings and reduce expenses immediately.';
        else if(fi < 5) advice = 'Moderate: Keep growing assets, manage expenses wisely.';
        else if(fi < 7) advice = '✅ Good: You have partial financial freedom; keep building passive income.';
        else advice = '🌟 Excellent: Approaching full financial independence! Consider early investment opportunities.';

        fiAdvice.textContent = advice;
        saveFI();
    };

    [fiNet, fiExp, fiPassive].forEach(input => {
        input.addEventListener('input', updateFreedom);
        input.addEventListener('blur', () => {
            input.value = parseNumber(input.value).toLocaleString();
            updateFreedom();
        });
    });
}

/* -------------------------------
    🔟 DEBT VS ASSET PULSE (ENHANCED & ELEVATED)
--------------------------------*/
if (t.id === "DebtAssetPulse") {
    embedContainer.innerHTML = `
    <div class="networth-tool p-4"
         style="background:#ffffff; border-radius:20px; box-shadow:0 12px 35px rgba(166,128,35,0.15); border:1px solid rgba(166,128,35,0.35); max-width:600px; margin:0 auto; font-family:'Inter',sans-serif;">
        <h3 style="color:#a68023; font-weight:900; letter-spacing:0.5px; margin-bottom:12px; font-size:1.8rem;">
            ${t.name}
        </h3>

        <p style="font-style:italic; color:#555; margin-bottom:18px;">
            Evaluate your financial health by comparing assets to liabilities and assess your risk.
        </p>

        <label class="form-label fw-bold" style="color:#444;">Total Assets</label>
        <input id="dapA" type="text" class="form-control mb-2" placeholder="e.g., 100,000"
               style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; color:#a68023;" />

        <label class="form-label fw-bold" style="color:#444;">Total Liabilities</label>
        <input id="dapL" type="text" class="form-control mb-2" placeholder="e.g., 50,000"
               style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; color:#a68023;" />

        <label class="form-label fw-bold" style="color:#444;">Monthly Income (Optional)</label>
        <input id="dapIncome" type="text" class="form-control mb-3" placeholder="e.g., 6,000"
               style="border:1px solid #d6c48a; box-shadow:inset 0 0 6px rgba(166,128,35,0.15); font-weight:700; color:#a68023;" />

        <h5 style="font-weight:700; margin-top:10px;">
            Debt-to-Asset Ratio:
            <span id="dapOut" style="color:#a68023; font-weight:800;">0</span>
        </h5>

        <table class="table mt-3"
               style="background:#fafafa; border-radius:12px; overflow:hidden; border:1px solid #eee;">
            <tr><th style="width:45%; background:#f3f3f3;">Assets</th><td id="dapAssets">$0</td></tr>
            <tr><th style="background:#f3f3f3;">Liabilities</th><td id="dapLiabilities">$0</td></tr>
            <tr><th style="background:#f3f3f3;">Net Worth</th><td id="dapNetWorth">$0</td></tr>
            <tr><th style="background:#f3f3f3;">Monthly Income</th><td id="dapMonthlyIncome">$0</td></tr>
        </table>

        <div id="dapAdvice"
             style="padding:14px; background:linear-gradient(135deg, #f1ede3, #e1d6b8); border-left:5px solid #a68023; font-style:italic; color:#333; margin-top:15px; border-radius:10px; box-shadow:inset 0 0 12px rgba(166,128,35,0.25);">
            Enter values to get guidance on your financial health.
        </div>
    </div>`;

    const dapA = document.getElementById('dapA');
    const dapL = document.getElementById('dapL');
    const dapIncome = document.getElementById('dapIncome');
    const dapOut = document.getElementById('dapOut');
    const dapAssets = document.getElementById('dapAssets');
    const dapLiabilities = document.getElementById('dapLiabilities');
    const dapNetWorth = document.getElementById('dapNetWorth');
    const dapMonthlyIncome = document.getElementById('dapMonthlyIncome');
    const dapAdvice = document.getElementById('dapAdvice');

    const parseNumber = (v) => +v.toString().replace(/,/g,'') || 0;
    const formatWithCommas = (v) => v ? (+v).toLocaleString() : '0';

    /* ---------- PERSISTENCE ---------- */
    const loadDAP = () => {
        const s = JSON.parse(localStorage.getItem('DebtAssetPulse') || '{}');
        if(s.dapA) dapA.value = s.dapA;
        if(s.dapL) dapL.value = s.dapL;
        if(s.dapIncome) dapIncome.value = s.dapIncome;
        if(s.dapOut) dapOut.textContent = s.dapOut;
        if(s.dapAssets) dapAssets.textContent = s.dapAssets;
        if(s.dapLiabilities) dapLiabilities.textContent = s.dapLiabilities;
        if(s.dapNetWorth) dapNetWorth.textContent = s.dapNetWorth;
        if(s.dapMonthlyIncome) dapMonthlyIncome.textContent = s.dapMonthlyIncome;
        if(s.dapAdvice) dapAdvice.textContent = s.dapAdvice;
    };

    const saveDAP = () => {
        localStorage.setItem('DebtAssetPulse', JSON.stringify({
            dapA: dapA.value,
            dapL: dapL.value,
            dapIncome: dapIncome.value,
            dapOut: dapOut.textContent,
            dapAssets: dapAssets.textContent,
            dapLiabilities: dapLiabilities.textContent,
            dapNetWorth: dapNetWorth.textContent,
            dapMonthlyIncome: dapMonthlyIncome.textContent,
            dapAdvice: dapAdvice.textContent
        }));
    };

    loadDAP();

    addClearButton(embedContainer.querySelector('.networth-tool'), () => {
        dapA.value = dapL.value = dapIncome.value = '';
        dapOut.textContent = '0';
        dapAssets.textContent = dapLiabilities.textContent =
        dapNetWorth.textContent = dapMonthlyIncome.textContent = '$0';
        dapAdvice.textContent = 'Enter values to get guidance on your financial health.';
        localStorage.removeItem('DebtAssetPulse');
    });

    const updateDAP = () => {
        const assets = parseNumber(dapA.value);
        const liabilities = parseNumber(dapL.value);
        const income = parseNumber(dapIncome.value);

        dapAssets.textContent = `$${formatWithCommas(assets)}`;
        dapLiabilities.textContent = `$${formatWithCommas(liabilities)}`;
        dapNetWorth.textContent = `$${formatWithCommas(assets - liabilities)}`;
        dapMonthlyIncome.textContent = `$${formatWithCommas(income)}`;

        const ratio = liabilities > 0 ? (assets / liabilities).toFixed(2) : assets > 0 ? '∞' : '0';
        dapOut.textContent = ratio;

            let advice = '';
            if(liabilities > assets) advice = '⚠️ High risk: Liabilities exceed assets. Reduce debt immediately.';
            else if(assets <= liabilities * 1.25) advice = '⚠️ Caution: Assets barely cover liabilities.';
            else if(assets <= liabilities * 2) advice = 'Moderate: Assets exceed liabilities — keep building.';
            else advice = '✅ Healthy: Strong asset base relative to debt.';
    
            if(income > 0 && liabilities > 0) {
                const months = Math.ceil(liabilities / income);
                advice += ` You could cover liabilities in ~${months} month${months !== 1 ? 's' : ''}.`;
            }
    
            dapAdvice.textContent = advice;
            saveDAP();
        };
    
                    [dapA, dapL, dapIncome].forEach(input => {
                        input.addEventListener('input', updateDAP);
                        input.addEventListener('blur', () => {
                            input.value = formatWithCommas(parseNumber(input.value));
                            updateDAP();
                        });
                    });
                }
            });
        });
