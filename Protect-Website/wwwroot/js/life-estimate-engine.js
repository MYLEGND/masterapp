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

    if (numericAmount >= 1000000) {
      return '$1,000,000+ coverage estimate';
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
      .slice(0, 3)
      .map((reason) => `<li>${escapeHtml(reason)}</li>`)
      .join('');

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
        <ul class="lq-rec-bullets">${reasonsHtml}</ul>
      </article>
    `;
  }

  function buildResultsHeading(preview) {
    if (preview.displayMode === 'single') {
      return `Here’s your estimated ${escapeHtml(preview.primary.policyType)} fit`;
    }

    return 'Here’s what may fit based on what you shared';
  }

  function buildResultsHelper(preview) {
    if (preview.displayMode === 'single') {
      return `This estimate is a practical starting point for ${escapeHtml(preview.primary.policyType)} based on what you shared.`;
    }

    return 'These estimated ranges are meant to give you a practical starting point before you talk through details with a licensed professional.';
  }

  function buildResultsNote(preview) {
    if (preview.displayMode === 'single') {
      return `Continue for a personal walkthrough of this estimate and how ${escapeHtml(preview.primary.policyType)} may fit.`;
    }

    return 'Continue for a personal walkthrough of these estimates and what may fit best.';
  }

  function buildResultsPanelHtml(preview, continueLabel) {
    const normalized = normalizePreview(preview);
    const secondary = normalized.secondary;
    const hasSecondary = normalized.displayMode === 'comparison' && secondary && secondary.policyKey;
    const disclaimer = normalized.disclaimer || normalized.primary.disclaimer || secondary?.disclaimer || '';

    return `
      <div class="lq-step-panel lq-estimate-panel" id="lifeMiniResultsPanel">
        <div class="lq-step-panel-pad">
          <div class="lq-step-progress">Your Results</div>
          <div class="lq-step-title">${buildResultsHeading(normalized)}</div>
          <div class="lq-step-helper">${buildResultsHelper(normalized)}</div>
          <div class="lq-estimate-grid">
            ${buildEstimateCard(normalized.primary, hasSecondary ? 'Recommended' : 'Your Estimate', 'primary')}
            ${hasSecondary ? buildEstimateCard(secondary, 'Also Worth Considering', 'secondary') : ''}
          </div>
          <div class="lq-estimate-disclaimer">${escapeHtml(disclaimer)}</div>
          <div class="lq-reach-note">${buildResultsNote(normalized)}</div>
          <div class="lq-step-actions">
            <button type="button" class="btn-gold w-100" id="continueToContactBtn">${escapeHtml(continueLabel || 'Continue & Review My Options')}</button>
          </div>
        </div>
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
    buildResultsPanelHtml,
    fetchPreview,
    formatCoverageAmount,
    formatCurrencyRange,
    normalizePreview
  });
})();
