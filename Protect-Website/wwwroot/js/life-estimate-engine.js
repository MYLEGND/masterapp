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
      age: Number(readValue(preview, 'age', 0)),
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

  function formatCoverageRange(low, high) {
    const lowValue = Math.max(0, Math.round(Number(low || 0)));
    const highValue = Math.max(lowValue, Math.round(Number(high || 0)));
    if (!lowValue && !highValue) {
      return '';
    }

    const lowDisplay = formatCoverageFigure(lowValue);
    const highDisplay = highValue >= 2000000
      ? '$2,000,000+'
      : formatCoverageFigure(highValue);

    if (!lowDisplay || !highDisplay) {
      return lowDisplay || highDisplay;
    }

    return `${lowDisplay}-${highDisplay}`;
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

  function isSingleProductPreview(preview) {
    return String(preview?.displayMode || '').trim().toLowerCase() === 'single'
      && String(preview?.offerKey || '').trim().toLowerCase() !== 'life';
  }

  function isGeneralLifePreview(preview) {
    return String(preview?.offerKey || '').trim().toLowerCase() === 'life';
  }

  function resolveOfferTopic(preview) {
    switch (String(preview?.offerKey || '').trim().toLowerCase()) {
      case 'term':
        return 'Term Life';
      case 'wholelife':
        return 'Whole Life';
      case 'finalexpense':
        return 'Final Expense';
      case 'mortgage':
        return 'Mortgage Protection';
      case 'iul':
        return 'Indexed Universal Life';
      default:
        return String(preview?.primary?.policyType || 'Life Insurance').replace(/\s+Insurance$/i, '').trim() || 'Life Insurance';
    }
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

  function formatAgeSignal(preview) {
    const exactAge = Math.round(Number(preview?.age || 0));
    if (Number.isFinite(exactAge) && exactAge > 0) {
      return `Age ${exactAge}`;
    }

    return preview?.ageBand ? `Age ${preview.ageBand}` : '';
  }

  function buildProfileSignals(preview) {
    const coverageTarget = formatCoverageFigure(preview.requestedCoverageAmount || preview.primary.coverageAmount);
    return [
      formatAgeSignal(preview),
      humanizeTobaccoUse(preview.tobaccoUse),
      humanizeProtectingWho(preview.protectingWho) ? `Protecting ${humanizeProtectingWho(preview.protectingWho)}` : '',
      humanizeCoverageGoal(preview.coverageGoal),
      coverageTarget ? `${coverageTarget} target` : ''
    ].filter(Boolean);
  }

  function buildGeneralLifeSignals(preview) {
    const protectingLabel = humanizeProtectingWho(preview.protectingWho);
    const goalLabel = humanizeCoverageGoal(preview.coverageGoal);

    return [
      formatAgeSignal(preview),
      humanizeTobaccoUse(preview.tobaccoUse),
      protectingLabel ? `Protecting ${protectingLabel}` : '',
      goalLabel
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
    if (isSingleProductPreview(preview)) {
      return `Estimated ${escapeHtml(resolveOfferTopic(preview).toLowerCase())} range: ${escapeHtml(formatCurrencyRangeCompact(preview.primary.estimatedLowMonthly, preview.primary.estimatedHighMonthly))}`;
    }

    return `A likely place to start: ${escapeHtml(preview.primary.policyType)} at ${escapeHtml(formatCurrencyRangeCompact(preview.primary.estimatedLowMonthly, preview.primary.estimatedHighMonthly))}`;
  }

  function resolveCoverageRangeLadder(preview) {
    switch (String(preview?.primary?.policyKey || preview?.offerKey || '').trim().toLowerCase()) {
      case 'term':
        return [250000, 500000, 1000000, 2000000];
      case 'wholelife':
        return [50000, 100000, 250000, 500000];
      case 'finalexpense':
        return [25000, 50000, 75000, 100000];
      case 'mortgage':
        return [150000, 250000, 500000, 750000];
      case 'iul':
        return [250000, 500000, 1000000, 2000000];
      default:
        return [100000, 250000, 500000, 1000000];
    }
  }

  function buildRecommendedCoverageRange(preview) {
    const targetCoverage = Number(preview?.requestedCoverageAmount || preview?.primary?.coverageAmount || 0);
    const ladder = resolveCoverageRangeLadder(preview);
    if (!Number.isFinite(targetCoverage) || targetCoverage <= 0 || ladder.length === 0) {
      return '';
    }

    if (ladder.length === 1) {
      return formatCoverageRange(ladder[0], ladder[0]);
    }

    if (targetCoverage <= ladder[0]) {
      return formatCoverageRange(ladder[0], ladder[1]);
    }

    for (let index = 1; index < ladder.length; index += 1) {
      if (targetCoverage <= ladder[index]) {
        return formatCoverageRange(ladder[index - 1], ladder[index]);
      }
    }

    return formatCoverageRange(ladder[ladder.length - 2], ladder[ladder.length - 1]);
  }

  function buildContactSummaryCopy(preview) {
    const policyType = escapeHtml(preview.primary.policyType || 'this option');
    const coverageTarget = formatCoverageFigure(preview.requestedCoverageAmount || preview.primary.coverageAmount);
    const coveragePhrase = coverageTarget ? ` around ${escapeHtml(coverageTarget)} of coverage` : '';
    const recommendedCoverageRange = buildRecommendedCoverageRange(preview);
    const coverageRangePhrase = recommendedCoverageRange
      ? ` A practical starting range to review from here is ${escapeHtml(recommendedCoverageRange)}.`
      : '';

    if (isSingleProductPreview(preview)) {
      return `This estimate stays centered on ${escapeHtml(resolveOfferTopic(preview).toLowerCase())}${coveragePhrase} based on the answers you gave.${coverageRangePhrase}`;
    }

    const secondaryPolicyType = escapeHtml(preview.secondary?.policyType || '');
    const secondaryComparisonPhrase = preview.secondary?.policyKey
      ? ` ${secondaryPolicyType} may still be worth reviewing if you want to compare a different long-term coverage path.`
      : '';

    return `Based on what you shared, ${policyType} looks like the most practical first option${coveragePhrase}.${coverageRangePhrase}${secondaryComparisonPhrase}`;
  }

  function buildRecommendationTier(preview) {
    if (preview.displayMode === 'comparison' && preview.secondary && preview.secondary.policyKey) {
      return 'Likely first option';
    }

    return 'Likely fit';
  }

  function buildSignalCopy(preview) {
    const pieces = buildProfileSignals(preview)
      .slice(0, 4)
      .map((signal) => escapeHtml(signal));

    if (isSingleProductPreview(preview)) {
      const topicLabel = escapeHtml(resolveOfferTopic(preview).toLowerCase());
      if (pieces.length === 0) {
        return `We used the details you shared to size this ${topicLabel} estimate.`;
      }

      return `${joinNaturalLanguage(pieces)} were used to size this ${topicLabel} estimate.`;
    }

    if (pieces.length === 0) {
      return `${escapeHtml(preview.primary.policyType)} rose to the top as the clearest starting point for the answers you gave.`;
    }

    return `${joinNaturalLanguage(pieces)} all pointed toward ${escapeHtml(preview.primary.policyType)} as the clearest first option to review.`;
  }

  function buildThirdStatLabel(preview) {
    return isSingleProductPreview(preview) ? 'Estimate focus' : 'Recommendation tier';
  }

  function buildThirdStatValue(preview) {
    return isSingleProductPreview(preview) ? resolveOfferTopic(preview) : buildRecommendationTier(preview);
  }

  function buildPrimaryBadgeLabel(preview) {
    if (isSingleProductPreview(preview)) {
      return `${resolveOfferTopic(preview)} Estimate`;
    }

    return preview.displayMode === 'comparison' && preview.secondary && preview.secondary.policyKey
      ? 'Likely First Option'
      : 'Likely Fit';
  }

  function buildCommonStartingPointCopy(preview) {
    const recommendedCoverageRange = buildRecommendedCoverageRange(preview);
    const primaryPolicyType = escapeHtml(preview?.primary?.policyType || 'this option');
    const secondaryPolicyType = escapeHtml(preview?.secondary?.policyType || '');
    if (!recommendedCoverageRange) {
      return isSingleProductPreview(preview)
        ? `This estimate is already centered on the first ${escapeHtml(resolveOfferTopic(preview).toLowerCase())} range that best matches the answers you gave.`
        : `This estimate starts with the clearest first option for the answers you gave before you decide whether to compare anything else.`;
    }

    if (isSingleProductPreview(preview)) {
      return `A common starting point for answers like yours is reviewing ${escapeHtml(recommendedCoverageRange)} of ${escapeHtml(resolveOfferTopic(preview).toLowerCase())} coverage first.`;
    }

    if (preview?.secondary?.policyKey) {
      return `A common next step is reviewing ${primaryPolicyType} in the ${escapeHtml(recommendedCoverageRange)} range first, then deciding whether it is worth comparing ${secondaryPolicyType}.`;
    }

    return `A common next step is reviewing ${primaryPolicyType} in the ${escapeHtml(recommendedCoverageRange)} range.`;
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
    if (preview.age > 0) {
      summaryPieces.push(`age ${escapeHtml(String(Math.round(preview.age)))}`);
    } else if (preview.ageBand) {
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

  function buildGeneralLifeSignalsHtml(preview) {
    const signals = buildGeneralLifeSignals(preview);
    if (signals.length === 0) {
      return '';
    }

    const chipsHtml = signals
      .map((signal) => `<span class="lq-contact-estimate-chip">${escapeHtml(signal)}</span>`)
      .join('');

    return `<div class="lq-contact-estimate-chips" aria-label="Estimate inputs">${chipsHtml}</div>`;
  }

  function buildGeneralLifeWhySentence(preview) {
    const policyType = escapeHtml(preview?.primary?.policyType || 'Life Insurance');
    return `Based on what you shared, ${policyType} looks like the clearest place to start reviewing options.`;
  }

  function buildGeneralLifeContactSummaryHtml(preview) {
    const normalized = normalizePreview(preview);
    const recommendedCoverageRange = buildRecommendedCoverageRange(normalized)
      || formatCoverageFigure(normalized.requestedCoverageAmount || normalized.primary.coverageAmount);
    const profileSignalsHtml = buildGeneralLifeSignalsHtml(normalized);

    return `
      <div class="lq-contact-estimate-wrap is-general-life-lite" id="lifeStep2EstimateSummary">
        <div class="lq-contact-estimate-card-wrap">
          <article class="lq-rec-card lq-estimate-card primary lq-estimate-card-compact" aria-label="Primary recommendation">
            <div class="lq-estimate-card-top">
              <span class="lq-rec-badge primary">${escapeHtml(buildRecommendationTier(normalized))}</span>
            </div>
            <div class="lq-contact-estimate-product">${escapeHtml(normalized.primary.policyType || 'Life Insurance')}</div>
            <div class="lq-contact-estimate-metrics" aria-label="Estimate highlights">
              <div class="lq-contact-estimate-metric">
                <div class="lq-contact-estimate-metric-label">Estimated monthly range</div>
                <div class="lq-contact-estimate-metric-value">${escapeHtml(formatCurrencyRangeCompact(normalized.primary.estimatedLowMonthly, normalized.primary.estimatedHighMonthly))}</div>
              </div>
              <div class="lq-contact-estimate-metric">
                <div class="lq-contact-estimate-metric-label">Recommended coverage range</div>
                <div class="lq-contact-estimate-metric-value">${escapeHtml(recommendedCoverageRange)}</div>
              </div>
            </div>
          </article>
        </div>
        <div class="lq-contact-estimate-signal is-general-life-lite">
          <div class="lq-contact-estimate-signal-copy">${buildGeneralLifeWhySentence(normalized)}</div>
        </div>
        ${profileSignalsHtml}
        <div class="lq-estimate-disclaimer">Estimates are illustrative only and not a final quote.</div>
      </div>
    `;
  }

  function buildContactSummaryHtml(preview) {
    const normalized = normalizePreview(preview);
    return buildGeneralLifeContactSummaryHtml(normalized);
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
