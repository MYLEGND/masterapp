(() => {
    'use strict';
    document.querySelectorAll('[data-translation-limit-form]').forEach(form => {
        const mode = form.querySelector('[data-limit-mode]');
        const field = form.querySelector('[data-custom-limit]');
        const input = field.querySelector('input');
        const update = () => {
            const custom = mode.value === 'Custom';
            field.hidden = !custom;
            input.disabled = !custom;
            input.required = custom;
        };
        mode.addEventListener('change', update);
        update();
    });
})();
