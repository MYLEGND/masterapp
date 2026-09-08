(function (global) {
    'use strict';
    const snapshots = new WeakMap();
    const escape = value => String(value ?? '').replace(/[&<>"']/g, character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[character]));
    const text = (root, selector) => root.querySelector(selector)?.textContent?.trim() || '';

    // Read the existing estimator's display values; this component never calculates a quote.
    function readEstimate(element, host) {
        if (!element) return snapshots.get(host) || { metrics: [], details: [], disclaimer: '' };
        const metrics = Array.from(element.querySelectorAll('.lq-contact-estimate-metric, .lq-contact-estimate-stat')).map(item => ({
            label: text(item, '.lq-contact-estimate-metric-label, .lq-contact-estimate-stat-label'),
            value: text(item, '.lq-contact-estimate-metric-value, .lq-contact-estimate-stat-value')
        })).filter(item => item.label && item.value);
        const details = Array.from(element.querySelectorAll('.lq-contact-estimate-copy, .lq-contact-estimate-carrier-main, .lq-contact-estimate-carrier-urgency')).map(item => item.textContent.trim()).filter(Boolean);
        const snapshot = { metrics, details, disclaimer: text(element, '.lq-estimate-disclaimer') };
        snapshots.set(host, snapshot);
        return snapshot;
    }

    function render(host, options) {
        const snapshot = options.snapshot || { metrics: [], details: [] };
        const metrics = snapshot.metrics || [];
        host.classList.add('quote-review-host');
        host.innerHTML = `
            <section class="quote-review" aria-labelledby="${escape(options.titleId)}">
                <header class="quote-review-header">
                    <div class="quote-review-status"><span aria-hidden="true">✓</span> ${escape(options.kicker || 'Review saved')}</div>
                    <h2 class="quote-review-title" id="${escape(options.titleId)}">${escape(options.title)}</h2>
                    <ul class="quote-review-assurances" aria-label="Review benefits">${(options.benefits || ['Personalized review', 'Licensed guidance', 'No obligation']).map(value => `<li>${escape(value)}</li>`).join('')}</ul>
                </header>
                ${metrics.length ? `<dl class="quote-review-metrics" aria-label="Your saved review">${metrics.map((item, index) => `<div class="quote-review-metric${index < 2 ? ' quote-review-metric-featured' : ''}"><dt>${escape(item.label)}</dt><dd>${escape(item.value)}</dd></div>`).join('')}</dl>` : ''}
                ${snapshot.disclaimer ? `<p class="quote-review-disclaimer">${escape(snapshot.disclaimer)}</p>` : ''}
                <div class="quote-review-insights">
                    <section class="quote-review-insight"><h3>Your priorities</h3><p>${escape(options.diagnosis)}</p></section>
                    <section class="quote-review-insight"><h3>What to consider</h3><p>${escape(options.implication)}</p></section>
                </div>
                <details class="quote-review-details">
                    <summary>What your review covers</summary>
                    <div class="quote-review-details-body">
                        <p>${escape(options.copy)}</p>
                        ${(snapshot.details || []).map(value => `<p>${escape(value)}</p>`).join('')}
                        <p>${escape(options.nextStep)}</p>
                        <ul class="quote-review-checklist">${(options.checklist || []).map(value => `<li><span aria-hidden="true">✓</span> ${escape(value)}</li>`).join('')}</ul>
                    </div>
                </details>
                <section class="quote-review-booking" aria-label="Schedule your review">
                    <div class="quote-review-booking-icon" aria-hidden="true"><svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7"><rect x="3" y="5" width="18" height="16" rx="3"/><path d="M7 3v4m10-4v4M3 10h18m-14 5h3m4 0h3"/></svg></div>
                    <div><span class="quote-review-eyebrow">Your next step</span><h3>${escape(options.bookingTitle)}</h3><p>${escape(options.bookingCopy)}</p></div>
                </section>
                ${options.loading ? `<p class="quote-review-message" role="status">${escape(options.loadingText)}</p>` : ''}
                ${options.showCalendar ? `<div class="lq-booking-frame-only" id="${escape(options.frameId)}"></div><div class="lq-booking-embed-status" id="${escape(options.statusId)}" role="status" hidden>${escape(options.loadingText)}</div>` : ''}
                ${options.unavailableText ? `<p class="quote-review-message" role="status">${escape(options.unavailableText)}</p>` : ''}
            </section>`;
        host.hidden = false;
    }
    global.LegendQuoteReview = Object.freeze({ readEstimate, render });
})(window);
