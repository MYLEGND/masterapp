/* -------------------------------
    1️⃣ GOALS (Left-side)
--------------------------------*/
document.addEventListener("DOMContentLoaded", function () {
    const container = document.querySelector('.finance-action-container');
    if (!container) return;

    const financeRoot = document.getElementById("financeRoot");
    const financeApp = (financeRoot?.dataset.financeApp || "").trim().toLowerCase();
    const fallbackScope =
        financeRoot?.dataset.financeScopeFallback?.trim() ||
        (financeApp === "agent" ? "agent" : "client");
    const workspaceScope =
        financeRoot?.dataset.clientUserId?.trim() ||
        financeRoot?.dataset.clientProfileId?.trim() ||
        fallbackScope;
    const actionTrackerKey = `legend-finance:${workspaceScope}:ActionTracker`;
    const persistence = window.LegendFinancePersistence;

    // Build main structure
    container.innerHTML = `
        <div class="finance-support-panel-head">
            <h3 class="finance-support-panel-title">Goals</h3>
            <p class="finance-support-panel-subtitle">Track your goals & habits to hit them.</p>
        </div>

        <div id="actionContainer" class="finance-support-list d-flex flex-column gap-2"></div>

        <div id="actionControls" class="finance-support-controls d-flex justify-content-center gap-2 mt-3">
            <button id="atAddAction" class="btn finance-support-action-btn">+ Add Goal</button>

            <button id="atDelAction" class="btn finance-support-action-btn">- Delete Last</button>
        </div>
    `;

    const addBtn = document.getElementById('atAddAction');
    const delBtn = document.getElementById('atDelAction');
    const actionContainer = document.getElementById('actionContainer');

    let actionCount = 0;

    const normalizeGoals = (value) => {
        const rawGoals = Array.isArray(value)
            ? value
            : Array.isArray(value?.goals)
                ? value.goals
                : Array.isArray(value?.items)
                    ? value.items
                    : [];

        return rawGoals.map(goal => ({
            name: typeof goal?.name === 'string' ? goal.name : '',
            done: Boolean(goal?.done)
        }));
    };

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
        window.dispatchEvent(new CustomEvent('legend:actiontracker:changed', {
            detail: {
                scope: workspaceScope,
                goals
            }
        }));
    };

    // Load goals from localStorage
    const loadState = async () => {
        actionContainer.innerHTML = '';
        actionCount = 0;

        const persistedGoals = persistence
            ? await persistence.loadState('ActionTracker')
            : JSON.parse(localStorage.getItem(actionTrackerKey) || '[]');
        const goals = normalizeGoals(persistedGoals);
        goals.forEach(g => createGoalRow(++actionCount, g.name, g.done));

        // If nothing saved, initialize with 3 empty goals
        if(goals.length === 0){
            for(let i = 0; i < 3; i++) createGoalRow(++actionCount);
        }
    };

    // Create a single goal row
    const createGoalRow = (index, nameVal = '', doneVal = false) => {
        const row = document.createElement('div');
        row.className = 'action-row finance-goal-row d-flex align-items-center gap-2';

        // Goal Name Input
        const nameInput = document.createElement('input');
        nameInput.className = 'form-control action-name finance-goal-input';
        nameInput.placeholder = `Goal ${index}`;
        nameInput.value = nameVal;
        nameInput.addEventListener('input', saveState);

        // Done Checkbox (gold when checked)
        const doneInput = document.createElement('input');
        doneInput.type = 'checkbox';
        doneInput.className = 'action-done finance-goal-check';
        doneInput.checked = doneVal;
        doneInput.addEventListener('change', saveState);

        // Delete button
        const delBtnRow = document.createElement('button');
        delBtnRow.textContent = '✕';
        delBtnRow.type = 'button';
        delBtnRow.className = 'finance-goal-delete';
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
