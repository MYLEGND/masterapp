(function () {
  function readValue(source, key, fallback = '') {
    if (!source || typeof source !== 'object') {
      return fallback;
    }

    const pascalKey = key.charAt(0).toUpperCase() + key.slice(1);
    return source[key] ?? source[pascalKey] ?? fallback;
  }

  function normalizeEstimateResult(result) {
    return {
      policyKey: String(readValue(result, 'policyKey', '')),
      policyType: String(readValue(result, 'policyType', 'Life Insurance')),
      coverageAmount: Number(readValue(result, 'coverageAmount', 0)),
      estimatedLowMonthly: Number(readValue(result, 'estimatedLowMonthly', 0)),
      estimatedHighMonthly: Number(readValue(result, 'estimatedHighMonthly', 0)),
      recommendationReason: String(readValue(result, 'recommendationReason', '')),
      disclaimer: String(readValue(result, 'disclaimer', '')),
      reasons: Array.isArray(readValue(result, 'reasons', [])) ? readValue(result, 'reasons', []) : []
    };
  }

  function normalizePreview(preview) {
    const secondarySource = readValue(preview, 'secondary', null);
    return {
      primary: normalizeEstimateResult(readValue(preview, 'primary', {})),
      secondary: secondarySource ? normalizeEstimateResult(secondarySource) : null,
      offerKey: String(readValue(preview, 'offerKey', 'life')),
      displayMode: String(readValue(preview, 'displayMode', 'comparison')),
      ageBand: String(readValue(preview, 'ageBand', '')),
      requestedCoverageAmount: Number(readValue(preview, 'requestedCoverageAmount', 0)),
      tobaccoUse: String(readValue(preview, 'tobaccoUse', '')),
      coverageGoal: String(readValue(preview, 'coverageGoal', '')),
      protectingWho: String(readValue(preview, 'protectingWho', '')),
      healthAssumption: String(readValue(preview, 'healthAssumption', 'Average Health')),
      disclaimer: String(readValue(preview, 'disclaimer', ''))
    };
  }

  function escapeHtml(value) {
    return String(value ?? '')
      .replaceAll('&', '&amp;')
      .replaceAll('<', '&lt;')
      .replaceAll('>', '&gt;')
      .replaceAll('"', '&quot;')
      .replaceAll("'", '&#39;');
  }

  function formatCoverageAmount(amount) {
    const numericAmount = Number(amount || 0);
    if (!Number.isFinite(numericAmount) || numericAmount <= 0) {
      return '';
    }

    if (numericAmount >= 2000000) {
      return '$2,000,000+ coverage estimate';
    }

    if (numericAmount >= 1000000) {
      return `$${numericAmount.toLocaleString('en-US')} coverage estimate`;
    }

    return `$${numericAmount.toLocaleString('en-US')} coverage estimate`;
  }

  function formatCurrencyRange(low, high) {
    const lowValue = Math.max(0, Math.round(Number(low || 0)));
    const highValue = Math.max(lowValue, Math.round(Number(high || 0)));
    return `$${lowValue.toLocaleString('en-US')}–$${highValue.toLocaleString('en-US')}/mo estimated`;
  }

  function buildEstimateCard(result, badgeLabel, modifierClass) {
    const normalized = normalizeEstimateResult(result);
    const reasonsHtml = normalized.reasons
      .slice(0, 2)
      .map((reason) => `<li>${escapeHtml(reason)}</li>`)
      .join('');
    const reasonsListHtml = reasonsHtml ? `<ul class="lq-rec-bullets">${reasonsHtml}</ul>` : '';

    return `
      <article class="lq-rec-card lq-estimate-card ${escapeHtml(modifierClass)}">
        <div class="lq-estimate-card-top">
          <span class="lq-rec-badge ${escapeHtml(modifierClass)}">${escapeHtml(badgeLabel)}</span>
          <div class="lq-estimate-coverage">${escapeHtml(formatCoverageAmount(normalized.coverageAmount))}</div>
        </div>
        <div class="lq-rec-title">${escapeHtml(normalized.policyType)}</div>
        <div class="lq-estimate-price-wrap">
          <div class="lq-estimate-price-label">Illustrative monthly range</div>
          <div class="lq-estimate-price">${escapeHtml(formatCurrencyRange(normalized.estimatedLowMonthly, normalized.estimatedHighMonthly))}</div>
        </div>
        <div class="lq-estimate-reason">${escapeHtml(normalized.recommendationReason)}</div>
        ${reasonsListHtml}
      </article>
    `;
  }

  function buildContactSummaryTitle(preview) {
    if (preview.displayMode === 'single') {
      return `Estimated ${escapeHtml(preview.primary.policyType)} fit`;
    }

    return `Estimated ${escapeHtml(preview.primary.policyType)} fit`;
  }

  function buildContactSummaryCopy(preview) {
    if (preview.displayMode === 'single') {
      return `Finish below to review this estimate and how ${escapeHtml(preview.primary.policyType)} may fit.`;
    }

    return 'Finish below for a personal walkthrough of this estimate and what may fit best.';
  }

  function buildContactSummaryHtml(preview) {
    const normalized = normalizePreview(preview);
    const secondary = normalized.secondary;
    const hasSecondary = normalized.displayMode === 'comparison' && secondary && secondary.policyKey;
    const disclaimer = normalized.disclaimer || normalized.primary.disclaimer || secondary?.disclaimer || '';
    const secondaryNoteHtml = hasSecondary
      ? `<div class="lq-contact-estimate-alt">Also worth reviewing: <strong>${escapeHtml(secondary.policyType)}</strong></div>`
      : '';

    return `
      <div class="lq-contact-estimate-wrap" id="lifeStep2EstimateSummary">
        <div class="lq-contact-estimate-head">
          <div class="lq-contact-estimate-kicker">Your Estimate</div>
          <div class="lq-contact-estimate-title">${buildContactSummaryTitle(normalized)}</div>
          <div class="lq-contact-estimate-copy">${buildContactSummaryCopy(normalized)}</div>
        </div>
        <div class="lq-contact-estimate-card-wrap">
          ${buildEstimateCard(normalized.primary, hasSecondary ? 'Recommended Fit' : 'Your Estimate', 'primary')}
        </div>
        ${secondaryNoteHtml}
        <div class="lq-estimate-disclaimer">${escapeHtml(disclaimer)}</div>
      </div>
    `;
  }

  async function fetchPreview(form, url) {
    const response = await fetch(url, {
      method: 'POST',
      body: new FormData(form),
      headers: { 'X-Requested-With': 'fetch' }
    });

    if (!response.ok) {
      throw new Error('estimate_preview_failed');
    }

    return normalizePreview(await response.json());
  }

  window.lifeEstimateEngine = Object.freeze({
    buildContactSummaryHtml,
    fetchPreview,
    formatCoverageAmount,
    formatCurrencyRange,
    normalizePreview
  });
})();
