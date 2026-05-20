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

  function formatMonthlyStartingPoint(low, high) {
    const lowValue = Math.max(0, Math.round(Number(low || 0)));
    const highValue = Math.max(lowValue, Math.round(Number(high || 0)));
    if (!lowValue && !highValue) {
      return '';
    }

    const midpoint = Math.round((lowValue + highValue) / 2);
    return `About $${midpoint.toLocaleString('en-US')}/mo`;
  }

  function joinNaturalLanguage(items) {
    const normalizedItems = items.filter(Boolean);
    if (normalizedItems.length === 0) {
      return '';
    }
    if (normalizedItems.length === 1) {
      return normalizedItems[0];
    }
    if (normalizedItems.length === 2) {
      return `${normalizedItems[0]} and ${normalizedItems[1]}`;
    }

    return `${normalizedItems.slice(0, -1).join(', ')}, and ${normalizedItems[normalizedItems.length - 1]}`;
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
          <div class="lq-estimate-price-label">Estimated monthly range</div>
          <div class="lq-estimate-price">${escapeHtml(formatCurrencyRange(normalized.estimatedLowMonthly, normalized.estimatedHighMonthly))}</div>
        </div>
        <div class="lq-estimate-reason">${escapeHtml(normalized.recommendationReason)}</div>
        ${reasonsListHtml}
      </article>
    `;
  }

  function buildContactSummaryTitle(preview) {
    return `Estimated ${escapeHtml(preview.primary.policyType)} range: ${escapeHtml(formatCurrencyRangeCompact(preview.primary.estimatedLowMonthly, preview.primary.estimatedHighMonthly))}`;
  }

  function buildContactSummaryCopy(preview) {
    const policyType = escapeHtml(preview.primary.policyType || 'this option');
    const coverageTarget = formatCoverageFigure(preview.requestedCoverageAmount || preview.primary.coverageAmount);
    const coveragePhrase = coverageTarget ? ` around ${escapeHtml(coverageTarget)} of coverage` : '';

    if (preview.displayMode === 'single') {
      return `This first look points most strongly toward ${policyType}${coveragePhrase}.`;
    }

    return `A common first comparison for answers like yours starts with ${policyType}${coveragePhrase}.`;
  }

  function buildRecommendationTier(preview) {
    if (preview.displayMode === 'comparison' && preview.secondary && preview.secondary.policyKey) {
      return 'Strong match';
    }

    return 'Likely fit';
  }

  function buildSignalCopy(preview) {
    const pieces = buildProfileSignals(preview)
      .slice(0, 4)
      .map((signal) => escapeHtml(signal));

    if (pieces.length === 0) {
      return `${escapeHtml(preview.primary.policyType)} rose to the top as the clearest starting point for the answers you gave.`;
    }

    return `${joinNaturalLanguage(pieces)} all pushed ${escapeHtml(preview.primary.policyType)} to the top as the clearest starting point.`;
  }

  function buildPersonalizedProtectionSummary(preview) {
    const protectingLabel = humanizeProtectingWho(preview.protectingWho);
    const goalLabel = humanizeCoverageGoal(preview.coverageGoal);
    const coverageTarget = formatCoverageFigure(preview.requestedCoverageAmount || preview.primary.coverageAmount);
    const tobaccoLabel = humanizeTobaccoUse(preview.tobaccoUse);
    const summaryPieces = [];

    if (protectingLabel) {
      summaryPieces.push(`protecting ${escapeHtml(protectingLabel.toLowerCase())}`);
    }
    if (goalLabel) {
      summaryPieces.push(`focused on ${escapeHtml(goalLabel.toLowerCase())}`);
    }
    if (coverageTarget) {
      summaryPieces.push(`around ${escapeHtml(coverageTarget)} of coverage`);
    }
    if (preview.ageBand) {
      summaryPieces.push(`age ${escapeHtml(preview.ageBand)}`);
    }
    if (tobaccoLabel) {
      summaryPieces.push(`${escapeHtml(tobaccoLabel.toLowerCase())} pricing assumptions`);
    }

    if (summaryPieces.length === 0) {
      return 'Sized around the answers you gave so you can see a real starting point before sharing your info.';
    }

    return `Sized for ${joinNaturalLanguage(summaryPieces)}.`;
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
        <div class="lq-contact-estimate-personal">
          <div class="lq-contact-estimate-personal-label">Personalized protection summary</div>
          <div class="lq-contact-estimate-personal-copy">${buildPersonalizedProtectionSummary(normalized)}</div>
        </div>
        <div class="lq-contact-estimate-signal">
          <div class="lq-contact-estimate-signal-badge">Why this showed up first</div>
          <div class="lq-contact-estimate-signal-copy">${buildSignalCopy(normalized)}</div>
        </div>
        <div class="lq-contact-estimate-stats" aria-label="Estimate highlights">
          <div class="lq-contact-estimate-stat">
            <div class="lq-contact-estimate-stat-label">Estimated monthly range</div>
            <div class="lq-contact-estimate-stat-value">${escapeHtml(formatCurrencyRangeCompact(normalized.primary.estimatedLowMonthly, normalized.primary.estimatedHighMonthly))}</div>
          </div>
          <div class="lq-contact-estimate-stat">
            <div class="lq-contact-estimate-stat-label">Likely monthly starting point</div>
            <div class="lq-contact-estimate-stat-value">${escapeHtml(formatMonthlyStartingPoint(normalized.primary.estimatedLowMonthly, normalized.primary.estimatedHighMonthly))}</div>
          </div>
          <div class="lq-contact-estimate-stat">
            <div class="lq-contact-estimate-stat-label">Recommendation tier</div>
            <div class="lq-contact-estimate-stat-value">${escapeHtml(buildRecommendationTier(normalized))}</div>
          </div>
        </div>
        ${profileSignalsHtml}
        <div class="lq-contact-estimate-card-wrap">
          ${buildEstimateCard(normalized.primary, hasSecondary ? 'Recommended First' : 'Your Likely Fit', 'primary')}
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
