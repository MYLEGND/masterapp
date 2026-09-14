(() => {
    'use strict';
    const excluded = 'script,style,code,pre,textarea,[translate="no"],[data-user-content],.message-body,.message-content,.messaging-message-body,[data-message-body]';
    const sources = new WeakMap();
    const attributes = new WeakMap();
    let catalog = new Map();
    let pending;
    let generation = 0;
    let retryTimer;
    let desiredLanguage;
    let currentVersion;
    let continuation;
    let pass;
    let notBefore = 0;
    let transportFailures = 0;
    const warmCatalogs = new Map(); // Bounded presentation cache of server-issued Global copy only.
    const visible = () => document.visibilityState !== 'hidden';
    let status;
    let scheduled = false;
    const normalized = value => value.replace(/\s+/g, ' ').trim();
    function resolve(source, context) {
        const translated = catalog.get(context + '\n' + normalized(source));
        return translated ? (source.match(/^\s*/)?.[0] || '') + translated + (source.match(/\s*$/)?.[0] || '') : source;
    }
    function present(root) {
        if (!root) return;
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
    function showStatus(message) {
        if (!message) { if (status) status.remove(); status = null; return; }
        if (!status) {
            status = document.createElement('aside');
            status.className = 'alert alert-info mb-0';
            status.setAttribute('role', 'status');
            document.body.prepend(status);
        }
        status.textContent = message;
    }
    function validCatalog(value) {
        if (!value || typeof value !== 'object' || typeof value.languageCode !== 'string' || !value.languageCode ||
            typeof value.catalogVersion !== 'string' || !value.catalogVersion || typeof value.isComplete !== 'boolean' ||
            !Array.isArray(value.entries) || !value.entries.length ||
            !value.entries.every(entry => entry && typeof entry.id === 'string' && entry.id &&
                typeof entry.source === 'string' && typeof entry.text === 'string' && typeof entry.context === 'string') ||
            new Set(value.entries.map(entry => entry.id)).size !== value.entries.length) return false;
        try { new Intl.Locale(value.locale || value.languageCode); } catch { return false; }
        return true;
    }
    function validContinuation(value) {
        const next = value?.continuation;
        if (!next || !['Pending', 'RetryableFailure'].includes(next.disposition) ||
            !Number.isInteger(next.remainingEntries) || next.remainingEntries < 1 ||
            !Number.isInteger(next.retryAfterSeconds) || next.retryAfterSeconds < 1 || next.retryAfterSeconds > 2678400 ||
            !Number.isInteger(next.maximumConsecutiveNoProgress) || next.maximumConsecutiveNoProgress < 1 || next.maximumConsecutiveNoProgress > 10 ||
            !Number.isInteger(next.maximumDurationSeconds) || next.maximumDurationSeconds < 1 || next.maximumDurationSeconds > 600 ||
            !Number.isInteger(next.maximumRequestsPerPass) || next.maximumRequestsPerPass < 1 || next.maximumRequestsPerPass > 256 ||
            !Number.isInteger(next.cooldownSeconds) || next.cooldownSeconds < 15 || next.cooldownSeconds > 900) return null;
        return next;
    }
    function apply(value) {
        catalog = new Map(value.entries.filter(entry => !entry.failureCode && entry.text && entry.source)
            .map(entry => [entry.context + '\n' + normalized(entry.source), entry.text]));
        document.documentElement.lang = value.languageCode;
        const locale = new Intl.Locale(value.locale || value.languageCode);
        document.documentElement.dir = locale.getTextInfo?.().direction || locale.textInfo?.direction || 'ltr';
        present(document.body);
    }
    function remember(value) {
        const key = value.catalogVersion + '\n' + value.languageCode.toLowerCase();
        warmCatalogs.delete(key); warmCatalogs.set(key, value);
        while (warmCatalogs.size > 8) warmCatalogs.delete(warmCatalogs.keys().next().value);
        currentVersion = value.catalogVersion;
        return value;
    }
    function resetPass() { pass = { started: Date.now(), requests: 0, unchanged: 0, identity: null, remaining: null }; }
    function cancel() {
        generation++;
        clearTimeout(retryTimer); retryTimer = null;
        pending?.abort(); pending = null;
    }
    function schedule(seconds, reset = false) {
        clearTimeout(retryTimer);
        notBefore = Math.max(notBefore, Date.now() + seconds * 1000);
        if (!visible()) return;
        const expected = generation;
        function wake() {
            retryTimer = null;
            if (expected !== generation || !visible()) return;
            const remaining = notBefore - Date.now();
            // Browser timers have a signed32-bit millisecond ceiling; monthly capacity holds do not.
            if (remaining > 0) { retryTimer = setTimeout(wake, Math.min(remaining, 86400000)); return; }
            if (reset) resetPass();
            refresh();
        }
        retryTimer = setTimeout(wake, Math.min(Math.max(0, notBefore - Date.now()), 86400000));
    }
    function resume(language) {
        cancel(); resetPass();
        if (typeof language === 'string' && language.trim()) {
            desiredLanguage = language.trim().toLowerCase();
            notBefore = 0; transportFailures = 0;
            const warm = warmCatalogs.get(currentVersion + '\n' + desiredLanguage) || [...warmCatalogs.values()].reverse().find(value => value.languageCode.toLowerCase() === desiredLanguage);
            if (warm) apply(warm); // Synchronous; network is revalidation, not presentation permission.
        }
        if (visible()) { if (notBefore > Date.now()) schedule(0); else refresh(); }
    }
    async function refresh() {
        if (pending || !visible()) return;
        const controller = new AbortController();
        pending = controller;
        const expected = generation;
        const deadline = setTimeout(() => controller.abort(), 30000);
        try {
            const response = await fetch('/localization/catalog', { credentials: 'same-origin', signal: controller.signal, headers: { Accept: 'application/json' } });
            if (expected !== generation || controller.signal.aborted) return;
            if ([401, 403].includes(response.status) || response.redirected) {
                cancel(); warmCatalogs.clear(); continuation = null; desiredLanguage = null;
                catalog = new Map(); notBefore = 0; document.documentElement.lang = "en"; document.documentElement.dir = "ltr"; present(document.body); showStatus(null); return;
            }
            if (!response.ok) {
                if (response.status >= 400 && response.status < 500 && ![408, 429].includes(response.status)) {
                    continuation = null; showStatus('Some text could not be translated. Your language preference is saved.'); return;
                }
                throw new Error('catalog_unavailable');
            }
            if (!response.headers.get('content-type')?.includes('application/json')) { continuation = null; showStatus('Some text could not be translated. Your language preference is saved.'); return; }
            let value;
            try { value = await response.json(); }
            catch {
                if (expected !== generation || controller.signal.aborted) return;
                continuation = null; showStatus('Some text could not be translated. Your language preference is saved.'); return;
            }
            if (expected !== generation || controller.signal.aborted) return;
            if (!validCatalog(value)) { continuation = null; showStatus('Some text could not be translated. Your language preference is saved.'); return; }
            // The server's canonical preference wins even if a stale picker supplied another language.
            desiredLanguage = value.languageCode.toLowerCase();
            transportFailures = 0; notBefore = 0;
            apply(remember(value));
            continuation = validContinuation(value);
            if (!continuation) {
                showStatus(value.isComplete ? null : 'Some text could not be translated. Your language preference is saved.');
                return;
            }
            showStatus('Updating your language…');
            const identity = value.catalogVersion + '\n' + desiredLanguage;
            pass.requests++;
            pass.unchanged = pass.identity === identity && pass.remaining !== null && continuation.remainingEntries >= pass.remaining ? pass.unchanged + 1 : 0;
            pass.identity = identity; pass.remaining = continuation.remainingEntries;
            const exhausted = pass.unchanged >= continuation.maximumConsecutiveNoProgress ||
                pass.requests >= continuation.maximumRequestsPerPass || Date.now() - pass.started >= continuation.maximumDurationSeconds * 1000;
            schedule(Math.max(continuation.retryAfterSeconds, exhausted ? continuation.cooldownSeconds : 0), exhausted);
        } catch {
            if (expected !== generation) return;
            showStatus('Some text could not be translated. Your language preference is saved.');
            // Recover transport of the same authorized GET. Provider continuation remains server-owned.
            transportFailures++;
            schedule(Math.max(continuation?.retryAfterSeconds || 0, continuation?.cooldownSeconds || 0,
                Math.min(60, 2 ** Math.min(transportFailures, 6))), true);
        } finally {
            clearTimeout(deadline);
            if (pending === controller) pending = null;
        }
    }
    const observer = new MutationObserver(() => {
        if (scheduled) return;
        scheduled = true;
        requestAnimationFrame(() => { scheduled = false; present(document.body); });
    });
    function start() {
        observer.observe(document.body, { subtree: true, childList: true, characterData: true, attributes: true, attributeFilter: ['placeholder', 'title', 'aria-label', 'alt'] });
        resume();
    }
    const resumeIfIdle = () => { if (!pending && !retryTimer) resume(); };
    window.addEventListener('focus', resumeIfIdle);
    window.addEventListener('pageshow', resumeIfIdle);
    window.addEventListener('online', resumeIfIdle);
    window.addEventListener('pagehide', () => { cancel(); warmCatalogs.clear(); continuation = null; });
    document.addEventListener('visibilitychange', () => { if (visible()) resume(); else cancel(); });
    window.addEventListener('legend:preferred-language-changed', event => {
        continuation = null;
        resume(event.detail?.languageCode);
    });
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true });
    else start();
})();
