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

  function formatCoverageFigure(amount) {
    const numericAmount = Number(amount || 0);
    if (!Number.isFinite(numericAmount) || numericAmount <= 0) {
      return '';
    }

    if (numericAmount >= 2000000) {
      return '$2,000,000+';
    }

    return `$${numericAmount.toLocaleString('en-US')}`;
  }

  function formatCurrencyRange(low, high) {
    const lowValue = Math.max(0, Math.round(Number(low || 0)));
    const highValue = Math.max(lowValue, Math.round(Number(high || 0)));
    return `$${lowValue.toLocaleString('en-US')}–$${highValue.toLocaleString('en-US')}/mo estimated`;
  }

  function formatCurrencyRangeCompact(low, high) {
    const lowValue = Math.max(0, Math.round(Number(low || 0)));
    const highValue = Math.max(lowValue, Math.round(Number(high || 0)));
    return `$${lowValue.toLocaleString('en-US')}-$${highValue.toLocaleString('en-US')}/mo`;
  }

  function humanizeProtectingWho(value) {
    switch (String(value || '').trim().toLowerCase()) {
      case 'spouse_or_partner':
        return 'Partner';
      case 'children':
        return 'Children';
      case 'family':
        return 'Family';
      case 'just_me':
        return 'Myself';
      default:
        return '';
    }
  }

  function humanizeCoverageGoal(value) {
    switch (String(value || '').trim().toLowerCase()) {
      case 'replace_income':
        return 'Income protection';
      case 'mortgage_or_bills':
        return 'Mortgage and bills';
      case 'final_expenses':
        return 'Final expenses';
      case 'leave_something':
      case 'leave_legacy':
        return 'Legacy planning';
      case 'protect_term_years':
        return 'Key protection years';
      case 'keep_costs_affordable':
        return 'Lower-cost protection';
      case 'lifelong_protection':
        return 'Lifelong coverage';
      case 'cash_value_growth':
        return 'Cash value growth';
      case 'burial_costs':
        return 'Burial costs';
      case 'final_bills':
        return 'Final bills';
      case 'ease_family_burden':
        return 'Ease family burden';
      case 'leave_small_benefit':
        return 'Small benefit';
      case 'mortgage_balance':
        return 'Mortgage balance';
      case 'monthly_payment':
        return 'Monthly payment help';
      case 'stay_in_home':
        return 'Staying in the home';
      case 'household_bills':
        return 'Household stability';
      case 'future_access':
        return 'Future cash value access';
      default:
        return '';
    }
  }

  function humanizeTobaccoUse(value) {
    switch (String(value || '').trim().toLowerCase()) {
      case 'non_smoker':
        return 'Non-smoker';
      case 'smoker':
        return 'Tobacco use';
      default:
        return '';
    }
  }

  function buildProfileSignals(preview) {
    const coverageTarget = formatCoverageFigure(preview.requestedCoverageAmount || preview.primary.coverageAmount);
    return [
      preview.ageBand ? `Age ${preview.ageBand}` : '',
      humanizeTobaccoUse(preview.tobaccoUse),
      humanizeProtectingWho(preview.protectingWho) ? `Protecting ${humanizeProtectingWho(preview.protectingWho)}` : '',
      humanizeCoverageGoal(preview.coverageGoal),
      coverageTarget ? `${coverageTarget} target` : ''
    ].filter(Boolean);
  }

  function buildEstimateCard(result, badgeLabel, modifierClass) {
    const normalized = normalizeEstimateResult(result);
    const reasonsHtml = normalized.reasons
      .slice(0, 3)
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
      return `Your ${escapeHtml(preview.primary.policyType)} estimate is ready`;
    }

    return `${escapeHtml(preview.primary.policyType)} looks like your strongest starting point`;
  }

  function buildContactSummaryCopy(preview) {
    if (preview.displayMode === 'single') {
      return `We sized this estimate around the answers you gave so you can see a real starting point before your walkthrough.`;
    }

    return `Among the options we compared, ${escapeHtml(preview.primary.policyType)} rose to the top first for the answers you gave.`;
  }

  function buildSignalCopy(preview, signalCount) {
    const pieces = [];

    if (preview.ageBand) {
      pieces.push(`age ${escapeHtml(preview.ageBand)}`);
    }

    const tobaccoLabel = humanizeTobaccoUse(preview.tobaccoUse);
    if (tobaccoLabel) {
      pieces.push(`${escapeHtml(tobaccoLabel.toLowerCase())} pricing assumptions`);
    }

    const goalLabel = humanizeCoverageGoal(preview.coverageGoal);
    if (goalLabel) {
      pieces.push(escapeHtml(goalLabel.toLowerCase()));
    }

    if (pieces.length === 0) {
      return `We used ${signalCount} details from your answers to size this first-look estimate.`;
    }

    const leadingPieces = pieces.slice(0, 3).join(', ');
    return `We used ${signalCount} details from your answers, including ${leadingPieces}, to size this first-look estimate.`;
  }

  function buildProfileSignalsHtml(preview) {
    const signals = buildProfileSignals(preview);
    if (signals.length === 0) {
      return '';
    }

    const chipsHtml = signals
      .map((signal) => `<span class="lq-contact-estimate-chip">${escapeHtml(signal)}</span>`)
      .join('');

    return `<div class="lq-contact-estimate-chips" aria-label="Profile signals">${chipsHtml}</div>`;
  }

  function buildContactSummaryHtml(preview) {
    const normalized = normalizePreview(preview);
    const secondary = normalized.secondary;
    const hasSecondary = normalized.displayMode === 'comparison' && secondary && secondary.policyKey;
    const disclaimer = normalized.disclaimer || normalized.primary.disclaimer || secondary?.disclaimer || '';
    const signalCount = buildProfileSignals(normalized).length;
    const coverageTarget = formatCoverageFigure(normalized.requestedCoverageAmount || normalized.primary.coverageAmount);
    const profileSignalsHtml = buildProfileSignalsHtml(normalized);
    const secondaryNoteHtml = hasSecondary
      ? `<div class="lq-contact-estimate-alt">Also worth comparing next: <strong>${escapeHtml(secondary.policyType)}</strong> at ${escapeHtml(formatCurrencyRangeCompact(secondary.estimatedLowMonthly, secondary.estimatedHighMonthly))}</div>`
      : '';

    return `
      <div class="lq-contact-estimate-wrap" id="lifeStep2EstimateSummary">
        <div class="lq-contact-estimate-head">
          <div class="lq-contact-estimate-kicker">Based on your answers</div>
          <div class="lq-contact-estimate-title">${buildContactSummaryTitle(normalized)}</div>
          <div class="lq-contact-estimate-copy">${buildContactSummaryCopy(normalized)}</div>
        </div>
        <div class="lq-contact-estimate-signal">
          <div class="lq-contact-estimate-signal-badge">${signalCount || 5} profile signals used</div>
          <div class="lq-contact-estimate-signal-copy">${buildSignalCopy(normalized, signalCount || 5)}</div>
        </div>
        <div class="lq-contact-estimate-stats" aria-label="Estimate highlights">
          <div class="lq-contact-estimate-stat">
            <div class="lq-contact-estimate-stat-label">Projected monthly range</div>
            <div class="lq-contact-estimate-stat-value">${escapeHtml(formatCurrencyRangeCompact(normalized.primary.estimatedLowMonthly, normalized.primary.estimatedHighMonthly))}</div>
          </div>
          <div class="lq-contact-estimate-stat">
            <div class="lq-contact-estimate-stat-label">Coverage target</div>
            <div class="lq-contact-estimate-stat-value">${escapeHtml(coverageTarget || formatCoverageFigure(normalized.primary.coverageAmount))}</div>
          </div>
        </div>
        ${profileSignalsHtml}
        <div class="lq-contact-estimate-card-wrap">
          ${buildEstimateCard(normalized.primary, hasSecondary ? 'Top Recommendation' : 'Your Estimate', 'primary')}
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
