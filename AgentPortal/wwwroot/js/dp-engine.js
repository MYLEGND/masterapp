;(function(global){
  const { DP_SCHEMA_VERSION, DP_WITHDRAWAL_ORDER_DEFAULT } = global.DP_CONSTANTS || {};
  const { validatePlanInput, normalizeInput } = global.DP_VALIDATORS || {};

  function clampZero(val){ return val < 0 ? 0 : val; }

  function initBuckets(input){
    const base = Number(input.retirementBase||0);
    return {
      inv: base * (Number(input.invAllocPct||0)/100),
      li:  base * (Number(input.liAllocPct||0)/100),
      ann: base * (Number(input.annAllocPct||0)/100),
      reserve: Number(input.emergencyReserve||0)
    };
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

    const years = Math.max(0, Math.floor(endAge - retireAge + 1));
    const buckets = initBuckets(normalized);
    let dbLi = buckets.li; // simple display DB tracking

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

    let cumulativeSpend = 0;

    for(let y=0; y<years; y++){
      const age = Number(retireAge) + y;
      const inflFactor = Math.pow(1 + (inflationPct||0)/100, y);
      const desiredIncomeTarget = desiredIncome * inflFactor;
      const guaranteedIncomeCurrent = guaranteedIncome * inflFactor;

      const startInv = buckets.inv;
      const startLi  = buckets.li;
      const startAnn = buckets.ann;
      const startReserve = buckets.reserve;

      let remainingNetNeed = Math.max(desiredIncomeTarget - guaranteedIncomeCurrent, 0);

      let withdrawals = { inv:0, li:0, ann:0, reserve:0 };
      let taxes = { inv:0, li:0, ann:0 };

      const order = withdrawalOrder.length ? withdrawalOrder : DP_WITHDRAWAL_ORDER_DEFAULT;

      for(const bucket of order){
        if (remainingNetNeed <= 1e-6) break;
        if (bucket === "inv"){
          const taxRate = (invTaxPct||0)/100;
          const available = buckets.inv;
          const grossNeeded = remainingNetNeed / (1 - taxRate || 1); // avoid div0
          const gross = Math.min(available, grossNeeded);
          const tax = gross * taxRate;
          const net = gross - tax;
          buckets.inv -= gross;
          withdrawals.inv += gross;
          taxes.inv += tax;
          remainingNetNeed = Math.max(0, remainingNetNeed - net);
          if (buckets.inv <= 0 && depletionYearByBucket.inv === null) depletionYearByBucket.inv = y;
        } else if (bucket === "li"){
          if (liAccessMode === "none") continue;
          const taxable = liAccessMode === "withdrawal";
          const taxRate = taxable ? (liTaxPct||0)/100 : 0;
          const available = buckets.li;
          const grossNeeded = taxable ? remainingNetNeed / (1 - taxRate || 1) : remainingNetNeed;
          const gross = Math.min(available, grossNeeded);
          const tax = taxable ? gross * taxRate : 0;
          const net = gross - tax;
          buckets.li -= gross;
          withdrawals.li += gross;
          taxes.li += tax;
          remainingNetNeed = Math.max(0, remainingNetNeed - net);
          // death benefit tracking
          if (liAccessMode === "withdrawal") {
            dbLi = dbLi * (buckets.li / Math.max(startLi, 1e-9));
          } else if (liAccessMode === "loan") {
            dbLi = Math.max(0, dbLi - gross);
          }
          if (buckets.li <= 0 && depletionYearByBucket.li === null) depletionYearByBucket.li = y;
        } else if (bucket === "ann"){
          const taxRate = (annTaxPct||0)/100;
          const available = buckets.ann;
          const grossNeeded = remainingNetNeed / (1 - taxRate || 1);
          const gross = Math.min(available, grossNeeded);
          const tax = gross * taxRate;
          const net = gross - tax;
          buckets.ann -= gross;
          withdrawals.ann += gross;
          taxes.ann += tax;
          remainingNetNeed = Math.max(0, remainingNetNeed - net);
          if (buckets.ann <= 0 && depletionYearByBucket.ann === null) depletionYearByBucket.ann = y;
        } else if (bucket === "reserve"){
          const available = buckets.reserve;
          const gross = Math.min(available, remainingNetNeed);
          buckets.reserve -= gross;
          withdrawals.reserve += gross;
          // reserve not taxed
          remainingNetNeed = Math.max(0, remainingNetNeed - gross);
          if (buckets.reserve <= 0 && depletionYearByBucket.reserve === null) depletionYearByBucket.reserve = y;
        }
      }

      const taxesTotal = taxes.inv + taxes.li + taxes.ann;
      const netIncomeDelivered = desiredIncomeTarget - remainingNetNeed;
      const shortfall = remainingNetNeed;

      totalTaxesPaid += taxesTotal;
      totalShortfall += shortfall;
      totalGrossWithdrawals += withdrawals.inv + withdrawals.li + withdrawals.ann + withdrawals.reserve;
      totalNetIncome += netIncomeDelivered;

      cumulativeSpend += netIncomeDelivered;

      // Growth after withdrawals
      buckets.inv = clampZero(buckets.inv * (1 + (invReturnPct||0)/100));
      buckets.li  = clampZero(buckets.li  * (1 + (liReturnPct||0)/100));
      buckets.ann = clampZero(buckets.ann * (1 + (annReturnPct||0)/100));
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

      auditRows.push({
        yearIndex: y,
        age,
        inflationFactor: inflFactor,
        desiredIncomeTarget,
        guaranteedIncome: guaranteedIncomeCurrent,
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
        deathBenefitLi: dbLi,
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
        totalShortfall
      },
      warnings: [],
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
