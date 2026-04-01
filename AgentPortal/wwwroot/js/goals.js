/* -------------------------------
    1️⃣ GOALS (Left-side)
--------------------------------*/
document.addEventListener("DOMContentLoaded", function () {
    const container = document.querySelector('.finance-action-container');
    const financeRoot = document.getElementById("financeRoot");
    const workspaceScope =
        financeRoot?.dataset.clientUserId?.trim() ||
        financeRoot?.dataset.clientProfileId?.trim() ||
        "agent";
    const actionTrackerKey = `legend-finance:${workspaceScope}:ActionTracker`;
    const persistence = window.LegendFinancePersistence;

    // Build main structure
    container.innerHTML = `
        <h3 style="color:#000000; font-weight:900; font-size:1.8rem; margin-bottom:15px; text-align:center;">Goals</h3>
        <p style="font-style:italic; color:#555; margin-bottom:15px; text-align:center;">Track your goals & habits to hit them.</p>

        <div id="actionContainer" class="d-flex flex-column gap-2" style="flex:1; overflow-y:auto; padding-right:4px;"></div>

        <div id="actionControls" class="d-flex justify-content-center gap-2 mt-3">
            <button id="atAddAction" class="btn" style="
                border:1px solid #b28f35; 
                color:#b28f35; 
                font-weight:700; 
                padding:6px 14px; 
                border-radius:8px; 
                cursor:pointer;
                transition: all 0.2s ease;
                background:linear-gradient(135deg,#fff8e6,#f9f0d9);
            ">+ Add Goal</button>

            <button id="atDelAction" class="btn" style="
                border:1px solid #b28f35; 
                color:#b28f35; 
                font-weight:700; 
                padding:6px 14px; 
                border-radius:8px; 
                cursor:pointer;
                transition: all 0.2s ease;
                background:linear-gradient(135deg,#fff8e6,#f9f0d9);
            ">- Delete Last</button>
        </div>
    `;

    const addBtn = document.getElementById('atAddAction');
    const delBtn = document.getElementById('atDelAction');
    const actionContainer = document.getElementById('actionContainer');

    let actionCount = 0;

    // Save all goals to localStorage
    const saveState = () => {
        const goals = [];
        document.querySelectorAll('.action-row').forEach(row => {
            goals.push({
                name: row.querySelector('.action-name').value || '',
                done: row.querySelector('.action-done').checked
            });
        });
        if (persistence) persistence.saveState('ActionTracker', goals);
        else localStorage.setItem(actionTrackerKey, JSON.stringify(goals));
    };

    // Load goals from localStorage
    const loadState = async () => {
        actionContainer.innerHTML = '';
        actionCount = 0;

        const goals = persistence
            ? await persistence.loadState('ActionTracker')
            : JSON.parse(localStorage.getItem(actionTrackerKey) || '[]');
        goals.forEach(g => createGoalRow(++actionCount, g.name, g.done));

        // If nothing saved, initialize with 3 empty goals
        if(goals.length === 0){
            for(let i = 0; i < 3; i++) createGoalRow(++actionCount);
        }
    };

    // Create a single goal row
    const createGoalRow = (index, nameVal = '', doneVal = false) => {
        const row = document.createElement('div');
        row.className = 'action-row d-flex align-items-center gap-2';
        row.style.cssText = `
            padding:10px; 
            border-radius:10px; 
            border:1px solid #d6c48a; 
            background:linear-gradient(135deg,#fffdf2,#f8f2e3);
            box-shadow: 0 2px 6px rgba(0,0,0,0.08);
            transition: all 0.2s ease;
        `;

        // Hover effect
        row.onmouseover = () => row.style.boxShadow = '0 4px 12px rgba(0,0,0,0.15)';
        row.onmouseout = () => row.style.boxShadow = '0 2px 6px rgba(0,0,0,0.08)';

        // Goal Name Input
        const nameInput = document.createElement('input');
        nameInput.className = 'form-control action-name';
        nameInput.style.flex = '2';
        nameInput.style.borderRadius = '6px';
        nameInput.style.border = '1px solid #d6c48a';
        nameInput.style.padding = '6px 8px';
        nameInput.placeholder = `Goal ${index}`;
        nameInput.value = nameVal;
        nameInput.addEventListener('input', saveState);

        // Done Checkbox (gold when checked)
        const doneInput = document.createElement('input');
        doneInput.type = 'checkbox';
        doneInput.className = 'action-done';
        doneInput.checked = doneVal;
        doneInput.style.width = '22px';
        doneInput.style.height = '22px';
        doneInput.style.accentColor = '#b28f35'; // gold check
        doneInput.addEventListener('change', saveState);

        // Delete button
        const delBtnRow = document.createElement('button');
        delBtnRow.textContent = '✕';
        delBtnRow.style.cssText = `
            border:none;
            background:transparent;
            color:#d6c48a;
            font-weight:900;
            cursor:pointer;
            transition: color 0.2s;
        `;
        delBtnRow.onmouseover = () => delBtnRow.style.color = '#922c0f';
        delBtnRow.onmouseout = () => delBtnRow.style.color = '#b28f35';
        delBtnRow.onclick = () => { actionContainer.removeChild(row); saveState(); };

        row.append(nameInput, doneInput, delBtnRow);
        actionContainer.appendChild(row);

        // Scroll to bottom if adding new goal
        row.scrollIntoView({ behavior: "smooth", block: "end" });
    };

    // Button handlers
    addBtn.onclick = () => createGoalRow(++actionCount);
    delBtn.onclick = () => {
        const last = actionContainer.lastElementChild;
        if(last){ actionContainer.removeChild(last); saveState(); }
    };

    // Load saved goals on page load
    loadState();
});
