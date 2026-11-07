(() => {
    'use strict';
    const form = document.getElementById('store-session-handoff');
    if (!form || form.dataset.submitted === 'true') return;
    const button = form.querySelector('button[type="submit"]');
    form.addEventListener('submit', event => {
        if (form.dataset.submitted === 'true') {
            event.preventDefault();
            return;
        }
        form.dataset.submitted = 'true';
        if (button) button.hidden = true;
    });
    // Normal navigation retains the server-rendered antiforgery token, nonce,
    // scoped destination and single-use continuation. Never retry a redemption.
    form.requestSubmit();
})();
