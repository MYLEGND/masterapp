(() => {
    'use strict';
    const excluded = 'script,style,code,pre,textarea,[translate="no"],[data-user-content],.message-body,.message-content,.messaging-message-body,[data-message-body]';
    const sources = new WeakMap();
    const attributes = new WeakMap();
    let catalog = new Map();
    let pending;
    let fillAttempts = 0;
    let retryTimer;
    let status;
    let scheduled = false;
    const normalized = value => value.replace(/\s+/g, ' ').trim();
    function resolve(source, context) {
        const translated = catalog.get(context + '\n' + normalized(source));
        return translated ? (source.match(/^\s*/)?.[0] || '') + translated + (source.match(/\s*$/)?.[0] || '') : source;
    }
    function present(root) {
        if (!root || !catalog.size) return;
        const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
        for (let node = walker.nextNode(); node; node = walker.nextNode()) {
            if (!node.parentElement || node.parentElement.closest(excluded)) continue;
            const prior = sources.get(node);
            const source = prior && node.nodeValue === prior.rendered ? prior.source : node.nodeValue;
            const rendered = resolve(source, 'visual interface copy');
            sources.set(node, { source, rendered });
            if (node.nodeValue !== rendered) node.nodeValue = rendered;
        }
        root.querySelectorAll('[placeholder],[title],[aria-label],[alt]').forEach(element => {
            if (element.closest(excluded)) return;
            const saved = attributes.get(element) || {};
            ['placeholder', 'title', 'aria-label', 'alt'].forEach(name => {
                if (!element.hasAttribute(name)) return;
                const current = element.getAttribute(name);
                const prior = saved[name];
                const source = prior && current === prior.rendered ? prior.source : current;
                const context = name === 'aria-label' || name === 'alt' ? 'accessibility copy' : 'visual interface copy';
                const rendered = resolve(source, context);
                saved[name] = { source, rendered };
                if (current !== rendered) element.setAttribute(name, rendered);
            });
            attributes.set(element, saved);
        });
    }
    async function refresh() {
        if (pending) return pending;
        pending = (async () => {
            try {
                const response = await fetch('/localization/catalog', { credentials: 'same-origin', headers: { Accept: 'application/json' } });
                if (!response.ok || !response.headers.get('content-type')?.includes('application/json')) return;
                const value = await response.json();
                if (!Array.isArray(value.entries) || !value.languageCode) return;
                catalog = new Map(value.entries.filter(entry => !entry.failureCode && entry.text && entry.source)
                    .map(entry => [entry.context + '\n' + normalized(entry.source), entry.text]));
                document.documentElement.lang = value.languageCode;
                const locale = new Intl.Locale(value.locale || value.languageCode);
                document.documentElement.dir = locale.getTextInfo?.().direction || locale.textInfo?.direction || 'ltr';
                const blocked = value.entries.some(entry => entry.failureCode && !['translation_pending', 'approved_translation_unavailable', 'translation_output_invalid', 'translation_provider_failed'].includes(entry.failureCode));
                const filling = !blocked && value.entries.some(entry => entry.failureCode === 'translation_pending');
                if (filling || value.entries.some(entry => entry.failureCode && entry.failureCode !== 'approved_translation_unavailable')) {
                    if (!status) {
                        status = document.createElement('aside');
                        status.className = 'alert alert-info mb-0';
                        status.setAttribute('role', 'status');
                        document.body.prepend(status);
                    }
                    status.textContent = filling ? 'Updating your language…' : 'Some text could not be translated. Your language preference is saved.';
                } else if (status) { status.remove(); status = null; }
                present(document.body);
                clearTimeout(retryTimer);
                if (filling && fillAttempts++ < 60) retryTimer = setTimeout(refresh, 1000);
                else fillAttempts = 0;
            } catch { /* Keep source or previously validated copy when offline. */ }
            finally { pending = null; }
        })();
        return pending;
    }
    const observer = new MutationObserver(() => {
        if (scheduled) return;
        scheduled = true;
        requestAnimationFrame(() => { scheduled = false; present(document.body); });
    });
    function start() {
        observer.observe(document.body, { subtree: true, childList: true, characterData: true, attributes: true, attributeFilter: ['placeholder', 'title', 'aria-label', 'alt'] });
        refresh();
    }
    window.addEventListener('focus', refresh);
    window.addEventListener('legend:preferred-language-changed', refresh);
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true });
    else start();
})();
