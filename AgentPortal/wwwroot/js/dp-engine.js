;(function(global){
  const { DP_SCHEMA_VERSION, DP_WITHDRAWAL_ORDER_DEFAULT } = global.DP_CONSTANTS || {};
  const { validatePlanInput, normalizeInput } = global.DP_VALIDATORS || {};

  function clampZero(val){ return val < 0 ? 0 : val; }
  function clamp(val, min, max){ return Math.max(min, Math.min(max, val)); }

  function seededRandom(seed){
    let x = (seed || 1) >>> 0;
    return function(){
      x = (1664525 * x + 1013904223) >>> 0;
      return x / 4294967296;
    };
  }

  function initBuckets(input){
    const base = Number(input.retirementBase||0);
    return {
      inv: base * (Number(input.invAllocPct||0)/100),
      li:  base * (Number(input.liAllocPct||0)/100),
      ann: base * (Number(input.annAllocPct||0)/100),
      reserve: Number(input.emergencyReserve||0)
    };
  }

  function parsePriority(order){
    if (!Array.isArray(order)) return [];
    const map = { emergency: 'reserve', investments: 'inv', life: 'li', annuities: 'ann', reserve: 'reserve', inv: 'inv', li: 'li', ann: 'ann' };
    return order.map(x => map[x]).filter(Boolean);
  }

  function downGapOrder(gapSource, fallbackOrder){
    const base = Array.isArray(fallbackOrder) && fallbackOrder.length ? fallbackOrder.slice() : DP_WITHDRAWAL_ORDER_DEFAULT.slice();
    if (gapSource === 'life') return ['li','ann','reserve','inv'];
    if (gapSource === 'annuities') return ['ann','li','reserve','inv'];
    if (gapSource === 'lifeThenAnnuities') return ['li','ann','reserve','inv'];
    if (gapSource === 'annThenLife') return ['ann','li','reserve','inv'];
    if (gapSource === 'custom') return base;
    if (gapSource === 'split') return ['split-li-ann','reserve','inv'];
    return base;
  }

  function createInvReturnPath(years, normalized){
    const scenarioMode = normalized.scenarioMode || 'fixed';
    const baseInvR = Number(normalized.invReturnPct || 0);
    const downThreshold = Number(normalized.downThreshold || 0);
    const manual = Array.isArray(normalized.manualReturns) ? normalized.manualReturns.map(Number).filter(n=>!isNaN(n)) : [];
    const forceDown = !!normalized.forceDownMarket;
    if (forceDown) {
      const forced = Math.min(-8, downThreshold - 2);
      return Array.from({ length: years }, () => forced);
    }
    if (scenarioMode === 'manual' && manual.length) {
      return Array.from({ length: years }, (_, i) => manual[Math.min(i, manual.length - 1)]);
    }
    if (scenarioMode === 'random') {
      const rnd = seededRandom((Number(normalized.planVersion || 1) * 2654435761) >>> 0);
      return Array.from({ length: years }, () => clamp(baseInvR + ((rnd() - 0.5) * 20), -50, 20));
    }
    return Array.from({ length: years }, () => baseInvR);
  }

  function runDistributionPlan(planInput){
    const normalized = normalizeInput(Object.assign({}, planInput));
    const validationErrors = validatePlanInput(normalized);
    if (validationErrors.length){
      return { errors: validationErrors, schemaVersion: DP_SCHEMA_VERSION };
    }

    const {
      retireAge, endAge, inflationPct,
      desiredIncome, guaranteedIncome,
      invReturnPct, liReturnPct, annReturnPct,
      invTaxPct, liTaxPct, annTaxPct,
      liAccessMode,
      withdrawalOrder = DP_WITHDRAWAL_ORDER_DEFAULT
    } = normalized;
    const strategy = normalized.strategy || 'proportional';
    const protectInvest = !!normalized.protectInvest;
    const invDownMarket = normalized.invDownMarket !== false;
    const liDownMarket = normalized.liDownMarket !== false;
    const annDownMarket = normalized.annDownMarket !== false;
    const gapSource = normalized.gapSource || 'life';
    const downThreshold = Number(normalized.downThreshold || 0);
    const liEfficiency = clamp(Number(normalized.liEfficiencyPct || 100) / 100, 0, 1);
    const annIncomeRider = !!normalized.annIncomeRider;
    const annDbRider = !!normalized.annDbRider;
    const annRollupRate = Math.max(0, Number(normalized.annRollupPct || 0)) / 100;
    const liPolicyType = (normalized.liPolicyType || 'whole').toLowerCase();
    const annDesign = (normalized.annDesign || 'fixed').toLowerCase();
    const priorityOrder = parsePriority(normalized.priorityOrder || normalized.priorityOrderUi || []);

    const years = Math.max(0, Math.floor(endAge - retireAge + 1));
    const buckets = initBuckets(normalized);
    const invReturnPath = createInvReturnPath(years, normalized);
    let dbLi = buckets.li; // simple display DB tracking
    let dbAnn = Number(normalized.annDeathBenefit || buckets.ann || 0);
    let annIncomeBase = buckets.ann;
    const annPayoutRate = Number(normalized.annPayoutRatePct || 5.0) / 100;

    const series = {
      wealthSeries: [],
      spendSeries: [],
      netIncomeSeries: [],
      grossWithdrawalSeries: [],
      taxesSeries: [],
      bucketSeries: { inv:[], li:[], ann:[], reserve:[] }
    };
    const auditRows = [];
    const depletionYearByBucket = { inv:null, li:null, ann:null, reserve:null };
    let totalTaxesPaid=0, totalShortfall=0, totalGrossWithdrawals=0, totalNetIncome=0;
    let downYearCount = 0;
    let firstFailureYearIndex = null;
    let lastFullyFundedYearIndex = null;
    let guardrailActivationYears = 0;
    let totalRiderIncomeGross = 0;
    let totalRiderCharges = 0;

    let cumulativeSpend = 0;

    for(let y=0; y<years; y++){
      const age = Number(retireAge) + y;
      const inflFactor = Math.pow(1 + (inflationPct||0)/100, y);
      const desiredIncomeTarget = desiredIncome * inflFactor;
      const guaranteedIncomeCurrentBase = guaranteedIncome * inflFactor;
      const invYearR = Number(invReturnPath[y] ?? invReturnPct ?? 0);
      const isDownYear = invYearR <= downThreshold;
      if (isDownYear) downYearCount += 1;

      const startInv = buckets.inv;
      const startLi  = buckets.li;
      const startAnn = buckets.ann;
      const startReserve = buckets.reserve;

      let annRiderIncomeGross = 0;
      let annRiderIncomeNet = 0;
      let annRiderCharge = 0;
      if (annIncomeRider && annIncomeBase > 0) {
        annIncomeBase = annIncomeBase * (1 + annRollupRate);
        annRiderIncomeGross = annIncomeBase * annPayoutRate;
        annRiderIncomeNet = annRiderIncomeGross * (1 - ((annTaxPct||0) / 100));
        totalRiderIncomeGross += annRiderIncomeGross;
      }

      let guaranteedIncomeCurrent = guaranteedIncomeCurrentBase + annRiderIncomeNet;
      let remainingNetNeed = Math.max(desiredIncomeTarget - guaranteedIncomeCurrent, 0);

      let withdrawals = { inv:0, li:0, ann:0, reserve:0 };
      let taxes = { inv:0, li:0, ann:0 };

      const canUseBucket = (bucket) => {
        if (bucket === 'inv') {
          if (isDownYear && !invDownMarket) return false;
          if (isDownYear && protectInvest) return false;
          return true;
        }
        if (bucket === 'li') {
          if (liPolicyType === 'legacy_rpu') return false;
          if (liAccessMode === 'none') return false;
          if (isDownYear && !liDownMarket) return false;
          return true;
        }
        if (bucket === 'ann') {
          if (isDownYear && !annDownMarket) return false;
          return true;
        }
        return true;
      };

      const availableNetFromBucket = (bucket) => {
        if (bucket === 'inv') return buckets.inv * (1 - (invTaxPct||0)/100);
        if (bucket === 'li') {
          const availableGross = buckets.li * liEfficiency;
          if (liAccessMode === 'loan') return availableGross;
          return availableGross * (1 - (liTaxPct||0)/100);
        }
        if (bucket === 'ann') return buckets.ann * (1 - (annTaxPct||0)/100);
        if (bucket === 'reserve') return buckets.reserve;
        return 0;
      };

      const withdrawFromBucket = (bucket, targetNetNeed) => {
        if (targetNetNeed <= 1e-6 || !canUseBucket(bucket)) return 0;
        if (bucket === 'inv') {
          const taxRate = (invTaxPct||0)/100;
          const available = buckets.inv;
          const grossNeeded = taxRate < 1 ? targetNetNeed / (1 - taxRate) : targetNetNeed;
          const gross = Math.min(available, grossNeeded);
          const tax = gross * taxRate;
          const net = gross - tax;
          buckets.inv -= gross;
          withdrawals.inv += gross;
          taxes.inv += tax;
          if (buckets.inv <= 0 && depletionYearByBucket.inv === null) depletionYearByBucket.inv = y;
          return net;
        }
        if (bucket === 'li') {
          const taxable = liAccessMode === 'withdrawal';
          const taxRate = taxable ? (liTaxPct||0)/100 : 0;
          const availableGross = buckets.li * liEfficiency;
          const grossNeeded = taxRate < 1 ? targetNetNeed / (1 - taxRate) : targetNetNeed;
          const gross = Math.min(availableGross, grossNeeded);
          const tax = taxable ? gross * taxRate : 0;
          const net = gross - tax;
          buckets.li -= gross;
          withdrawals.li += gross;
          taxes.li += tax;
          if (liAccessMode === 'withdrawal') {
            dbLi = dbLi * (buckets.li / Math.max(startLi, 1e-9));
          } else if (liAccessMode === 'loan') {
            dbLi = Math.max(0, dbLi - gross);
          }
          if (buckets.li <= 0 && depletionYearByBucket.li === null) depletionYearByBucket.li = y;
          return net;
        }
        if (bucket === 'ann') {
          const taxRate = (annTaxPct||0)/100;
          const available = buckets.ann;
          const grossNeeded = taxRate < 1 ? targetNetNeed / (1 - taxRate) : targetNetNeed;
          const gross = Math.min(available, grossNeeded);
          const tax = gross * taxRate;
          const net = gross - tax;
          buckets.ann -= gross;
          withdrawals.ann += gross;
          taxes.ann += tax;
          if (buckets.ann <= 0 && depletionYearByBucket.ann === null) depletionYearByBucket.ann = y;
          return net;
        }
        if (bucket === 'reserve') {
          const gross = Math.min(buckets.reserve, targetNetNeed);
          buckets.reserve -= gross;
          withdrawals.reserve += gross;
          if (buckets.reserve <= 0 && depletionYearByBucket.reserve === null) depletionYearByBucket.reserve = y;
          return gross;
        }
        return 0;
      };

      const fallbackOrder = (strategy === 'priority' && priorityOrder.length)
        ? priorityOrder.slice()
        : (withdrawalOrder.length ? withdrawalOrder.slice() : DP_WITHDRAWAL_ORDER_DEFAULT.slice());

      let order = fallbackOrder.slice();
      if (isDownYear) {
        order = downGapOrder(gapSource, fallbackOrder);
        if (protectInvest) guardrailActivationYears += 1;
      } else if (strategy === 'proportional' || strategy === 'guardrail') {
        order = ['inv','li','ann','reserve'];
      }

      if ((strategy === 'proportional' || (strategy === 'guardrail' && !isDownYear)) && remainingNetNeed > 1e-6) {
        const candidates = ['inv','li','ann','reserve'].filter(canUseBucket);
        const totalAvailNet = candidates.reduce((sum, b) => sum + availableNetFromBucket(b), 0);
        if (totalAvailNet > 0) {
          for (const b of candidates) {
            if (remainingNetNeed <= 1e-6) break;
            const share = remainingNetNeed * (availableNetFromBucket(b) / totalAvailNet);
            const netPulled = withdrawFromBucket(b, share);
            remainingNetNeed = Math.max(0, remainingNetNeed - netPulled);
          }
        }
      }

      for(const bucket of order){
        if (remainingNetNeed <= 1e-6) break;
        if (bucket === 'split-li-ann') {
          const liAvail = canUseBucket('li') ? availableNetFromBucket('li') : 0;
          const annAvail = canUseBucket('ann') ? availableNetFromBucket('ann') : 0;
          const total = liAvail + annAvail;
          if (total > 0) {
            const liNeed = remainingNetNeed * (liAvail / total);
            const liNet = withdrawFromBucket('li', liNeed);
            remainingNetNeed = Math.max(0, remainingNetNeed - liNet);
            if (remainingNetNeed > 1e-6) {
              const annNet = withdrawFromBucket('ann', remainingNetNeed);
              remainingNetNeed = Math.max(0, remainingNetNeed - annNet);
            }
          }
          continue;
        }
        const net = withdrawFromBucket(bucket, remainingNetNeed);
        remainingNetNeed = Math.max(0, remainingNetNeed - net);
      }

      const taxesTotal = taxes.inv + taxes.li + taxes.ann;
      const netIncomeDelivered = desiredIncomeTarget - remainingNetNeed;
      const shortfall = remainingNetNeed;
      if (shortfall > 1e-6 && firstFailureYearIndex === null) firstFailureYearIndex = y;
      if (shortfall <= 1e-6) lastFullyFundedYearIndex = y;

      totalTaxesPaid += taxesTotal;
      totalShortfall += shortfall;
      totalGrossWithdrawals += withdrawals.inv + withdrawals.li + withdrawals.ann + withdrawals.reserve;
      totalNetIncome += netIncomeDelivered;

      cumulativeSpend += netIncomeDelivered;

      // Growth after withdrawals
      let liYearR = Number(liReturnPct||0);
      if (liPolicyType === 'iul') {
        liYearR = clamp(invYearR, 0, Number(liReturnPct||0));
      } else if (liPolicyType === 'vul') {
        liYearR = clamp(invYearR - 1.25, -50, 20);
      } else if (liPolicyType === 'legacy_rpu') {
        liYearR = clamp(Number(liReturnPct||2), -10, 8);
      }

      let annYearR = Number(annReturnPct||0);
      if (annDesign === 'fixedindexed') {
        annYearR = clamp(invYearR, 0, Number(annReturnPct||0));
      } else if (annDesign === 'variable') {
        annYearR = clamp(invYearR - 1.25, -50, 20);
      }

      buckets.inv = clampZero(buckets.inv * (1 + invYearR/100));
      buckets.li  = clampZero(buckets.li  * (1 + liYearR/100));
      buckets.ann = clampZero(buckets.ann * (1 + annYearR/100));
      if (annIncomeRider && annRiderIncomeGross > 0) {
        annRiderCharge = annIncomeBase * 0.006;
        totalRiderCharges += annRiderCharge;
        buckets.ann = clampZero(buckets.ann - annRiderCharge);
      }
      if (annDbRider) {
        const dbCharge = buckets.ann * 0.0025;
        buckets.ann = clampZero(buckets.ann - dbCharge);
        dbAnn = Math.max(dbAnn, buckets.ann);
      } else {
        dbAnn = buckets.ann;
      }
      // reserve no growth

      const endInv = buckets.inv;
      const endLi  = buckets.li;
      const endAnn = buckets.ann;
      const endReserve = buckets.reserve;
      const totalEndBalance = endInv + endLi + endAnn + endReserve;

      series.wealthSeries.push(totalEndBalance);
      series.spendSeries.push(cumulativeSpend);
      series.netIncomeSeries.push(netIncomeDelivered);
      series.grossWithdrawalSeries.push(withdrawals.inv + withdrawals.li + withdrawals.ann + withdrawals.reserve);
      series.taxesSeries.push(taxesTotal);
      series.bucketSeries.inv.push(endInv);
      series.bucketSeries.li.push(endLi);
      series.bucketSeries.ann.push(endAnn);
      series.bucketSeries.reserve.push(endReserve);

      const notes = [];
      if (shortfall > 0) notes.push("shortfall");
      if (withdrawals.inv > 0 && startInv <= 0) notes.push("inv negative start");
      if (isDownYear) notes.push("down market");

      const sourceUsed = [];
      if (withdrawals.inv > 0) sourceUsed.push('Investments');
      if (withdrawals.li > 0) sourceUsed.push('Life Insurance');
      if (withdrawals.ann > 0 || annRiderIncomeGross > 0) sourceUsed.push('Annuities');
      if (withdrawals.reserve > 0) sourceUsed.push('Emergency');

      auditRows.push({
        yearIndex: y,
        age,
        marketState: isDownYear ? 'down' : 'normal',
        strategyUsed: strategy,
        fundingSource: sourceUsed.length ? sourceUsed.join(' + ') : (shortfall > 0 ? 'Unfunded' : 'Guaranteed only'),
        inflationFactor: inflFactor,
        desiredIncomeTarget,
        guaranteedIncome: guaranteedIncomeCurrent,
        guaranteedIncomeBase: guaranteedIncomeCurrentBase,
        annRiderIncomeGross,
        annRiderIncomeNet,
        annRiderCharge,
        annIncomeBase,
        netRemainingNeedBeforeWithdrawals: Math.max(desiredIncomeTarget - guaranteedIncomeCurrent,0),
        withdrawalsInvGross: withdrawals.inv,
        withdrawalsLiGross: withdrawals.li,
        withdrawalsAnnGross: withdrawals.ann,
        withdrawalsReserveGross: withdrawals.reserve,
        taxesInv: taxes.inv,
        taxesLi: taxes.li,
        taxesAnn: taxes.ann,
        taxesTotal,
        fees: 0,
        netIncomeDelivered,
        shortfall,
        startInv,
        startLi,
        startAnn,
        startReserve,
        endInv,
        endLi,
        endAnn,
        endReserve,
        totalEndBalance,
        invReturnPct: invYearR,
        liReturnPct: liYearR,
        annReturnPct: annYearR,
        deathBenefitLi: dbLi,
        deathBenefitAnn: dbAnn,
        notes
      });
    }

    const depletionYearOverall = Object.values(depletionYearByBucket).filter(v=>v!==null).sort((a,b)=>a-b)[0] ?? null;
    const result = {
      schemaVersion: DP_SCHEMA_VERSION,
      planVersion: normalized.planVersion || 1,
      summary:{
        totalEndBalance: series.wealthSeries.length ? series.wealthSeries[series.wealthSeries.length-1] : 0,
        depletionYearOverall,
        depletionYearByBucket,
        avgIncomeDeliveredNet: series.netIncomeSeries.length ? totalNetIncome/series.netIncomeSeries.length : 0,
        avgGrossWithdrawals: series.grossWithdrawalSeries.length ? series.grossWithdrawalSeries.reduce((a,b)=>a+b,0)/series.grossWithdrawalSeries.length : 0,
        totalTaxesPaid,
        totalShortfall,
        downYearCount,
        firstFailureYearIndex,
        lastFullyFundedYearIndex,
        guardrailActivationYears,
        totalRiderIncomeGross,
        totalRiderCharges,
        strategy,
        scenarioMode: normalized.scenarioMode || 'fixed',
        gapSource,
        annIncomeRider,
        annDbRider,
        liPolicyType,
        annDesign
      },
      warnings: normalized.forceDownMarket ? [{ type: 'info', message: 'Down-market mode forced for stress test run.' }] : [],
      series,
      auditRows,
      labels:{
        wealthLabel:"Illustrative",
        spendingLabel:"Illustrative",
        guaranteedIncomeLabel:"Guaranteed",
        netIncomeLabel:"Illustrative Net Income"
      },
      flags:{
        hasShortfall: totalShortfall > 0,
        hasDepletion: depletionYearOverall !== null
      },
      displayMeta:{
        nominalDollars:true,
        inflationPct,
        simulationYears: years
      }
    };
    return result;
  }

  global.runDistributionPlan = runDistributionPlan;
})(window);
