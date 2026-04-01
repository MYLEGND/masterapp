/* -------------------------------
    2️⃣ ALERTS PANEL (Right-side)
--------------------------------*/
document.addEventListener("DOMContentLoaded", function() {

    const container = document.querySelector('.finance-goals-container');
    if (!container) return;

    const financeRoot = document.getElementById("financeRoot");
    const workspaceScope =
        financeRoot?.dataset.clientUserId?.trim() ||
        financeRoot?.dataset.clientProfileId?.trim() ||
        "agent";
    const actionTrackerKey = `legend-finance:${workspaceScope}:ActionTracker`;
    const persistence = window.LegendFinancePersistence;

    // Build panel
    container.innerHTML = `
        <h3 style="color:#000000; font-weight:900; font-size:1.8rem; margin-bottom:10px; text-align:center;">Alerts</h3>
        <p style="font-style:italic; color:#555; margin-bottom:15px; text-align:center;">Top recommendations based on your goals.</p>
        <div id="alertsList" class="d-flex flex-column gap-2" style="flex:1; overflow-y:auto;"></div>
    `;

    const alertsList = document.getElementById('alertsList');

    const readActions = async () => {
        if (persistence) {
            const state = await persistence.loadState('ActionTracker');
            return Array.isArray(state) ? state : [];
        }

        const raw = localStorage.getItem(actionTrackerKey) || '[]';
        try {
            const parsed = JSON.parse(raw);
            return Array.isArray(parsed) ? parsed : [];
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
            div.textContent = alert;
            div.style.cssText = `
                padding:10px; 
                border-radius:10px; 
                background:linear-gradient(135deg,#fffdf2,#f8f2e3);
                border:1px solid #d6c48a; 
                color:#333; 
                font-weight:600;
                box-shadow: 0 2px 6px rgba(0,0,0,0.08);
                transition: all 0.2s ease;
            `;
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
