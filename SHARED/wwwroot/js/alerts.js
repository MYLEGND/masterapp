/* -------------------------------
    2️⃣ ALERTS PANEL (Right-side)
--------------------------------*/
document.addEventListener("DOMContentLoaded", function() {

    const container = document.querySelector('.finance-goals-container');
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

    // Build panel
    container.innerHTML = `
        <div class="finance-support-panel-head">
            <h3 class="finance-support-panel-title">Alerts</h3>
            <p class="finance-support-panel-subtitle">Top recommendations based on your goals.</p>
        </div>
        <div id="alertsList" class="finance-support-list d-flex flex-column gap-2"></div>
    `;

    const alertsList = document.getElementById('alertsList');

    const normalizeActions = (value) => {
        const rawActions = Array.isArray(value)
            ? value
            : Array.isArray(value?.goals)
                ? value.goals
                : Array.isArray(value?.items)
                    ? value.items
                    : [];

        return rawActions.map(action => ({
            name: typeof action?.name === 'string' ? action.name : '',
            done: Boolean(action?.done)
        }));
    };

    const readActions = async () => {
        if (persistence) {
            const state = await persistence.loadState('ActionTracker');
            return normalizeActions(state);
        }

        const raw = localStorage.getItem(actionTrackerKey) || '[]';
        try {
            const parsed = JSON.parse(raw);
            return normalizeActions(parsed);
        } catch {
            return [];
        }
    };

    // Function to generate alerts
    const generateAlerts = async (overrideActions) => {
        alertsList.innerHTML = '';

        const actions = Array.isArray(overrideActions)
            ? overrideActions
            : await readActions();

        const alerts = [];

        // Alert 1: Incomplete actions
        const incompleteActions = actions.filter(a => !a.done);
        if (incompleteActions.length >= 1) {
            alerts.push(`You have ${incompleteActions.length} incomplete goal(s). Stay consistent!`);
        }

        // Alert 2: Encourage or warn if nothing completed
        const completedCount = actions.filter(a => a.done).length;
        if (completedCount === 0) {
            alerts.push(`No goals have been completed. Lock in soldier!`);
        } else {
            alerts.push(`Keep going! You've already completed ${completedCount} goal(s). Stay on track!`);
        }

        // Alert 3: Positive reinforcement
        if (completedCount > 0) {
            alerts.push(`Way to go soldier! You completed ${completedCount} goal(s). Keep up the good work! Consistency is key. Next goal awaits!`);
        }

        // Fill at least 3 alerts for consistent layout
        while (alerts.length < 3) alerts.push('—');

        // Render alerts
        alerts.forEach(alert => {
            const div = document.createElement('div');
            div.className = 'alert-row finance-alert-card';
            div.textContent = alert;
            alertsList.appendChild(div);
        });
    };

    // -------------------------------
    // Live update: observe Action Tracker container
    // -------------------------------
    const actionContainer = document.getElementById('actionContainer') || document.querySelector('.finance-action-container #actionContainer');

    if(actionContainer) {
        // Observe additions/removals and checkbox changes
        const observer = new MutationObserver(() => { generateAlerts(); });
        observer.observe(actionContainer, { childList: true, subtree: true });

        // Also listen for checkbox changes inside the Action Tracker
        actionContainer.addEventListener('change', (e) => {
            if(e.target && e.target.classList.contains('action-done')) {
                generateAlerts();
            }
        });

        // And input changes (optional, if we want alerts to respond to naming changes)
        actionContainer.addEventListener('input', () => generateAlerts());
    }

    window.addEventListener('legend:actiontracker:changed', (event) => {
        const eventScope = event?.detail?.scope;
        if (eventScope && eventScope !== workspaceScope) return;
        const goals = event?.detail?.goals;
        generateAlerts(Array.isArray(goals) ? goals : undefined);
    });

    // Initial render
    generateAlerts();
});
