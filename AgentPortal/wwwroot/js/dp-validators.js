;(function(global){
  const { DP_BOUNDS, DP_WITHDRAWAL_ORDER_DEFAULT, DP_SCHEMA_VERSION } = global.DP_CONSTANTS || {};

  function fail(field, message){
    return { field, message };
  }

  function validateAllocations(inv, li, ann){
    const errs = [];
    const total = inv + li + ann;
    if (Math.abs(total - 100) > 0.001){
      errs.push(fail("allocations", `Allocations must total 100%. Currently ${total.toFixed(3)}.`));
    }
    ["invAllocPct","liAllocPct","annAllocPct"].forEach((k, idx) => {
      const val = [inv, li, ann][idx];
      if (val < 0 || val > 100) errs.push(fail(k, `${k} must be between 0 and 100.`));
    });
    return errs;
  }

  function validateRange(val, min, max, field){
    if (val < min || val > max) return [fail(field, `${field} must be between ${min} and ${max}.`)];
    return [];
  }

  function validatePlanInput(input){
    const errs = [];
    if (!input) return [fail("input","Missing plan input")];
    if (input.schemaVersion && input.schemaVersion !== DP_SCHEMA_VERSION){
      errs.push(fail("schemaVersion", `Unsupported schemaVersion ${input.schemaVersion}`));
    }
    const reqPos = ["retireAge","endAge","retirementBase","desiredIncome","guaranteedIncome","emergencyReserve"];
    reqPos.forEach(f=>{
      const v = Number(input[f]);
      if (isNaN(v) || v < 0) errs.push(fail(f, `${f} must be >= 0`));
    });
    if (Number(input.retireAge) <= 0) errs.push(fail("retireAge","retireAge must be > 0"));
    if (Number(input.endAge) <= Number(input.retireAge)) errs.push(fail("endAge","endAge must be greater than retireAge"));

    errs.push(...validateAllocations(
      Number(input.invAllocPct||0),
      Number(input.liAllocPct||0),
      Number(input.annAllocPct||0)
    ));

    [["invReturnPct","return"],["liReturnPct","return"],["annReturnPct","return"]].forEach(([f,type])=>{
      errs.push(...validateRange(Number(input[f]||0), DP_BOUNDS.returnMin, DP_BOUNDS.returnMax, f));
    });
    [["invTaxPct","tax"],["liTaxPct","tax"],["annTaxPct","tax"]].forEach(([f,type])=>{
      errs.push(...validateRange(Number(input[f]||0), DP_BOUNDS.taxMin, DP_BOUNDS.taxMax, f));
    });

    return errs;
  }

  function normalizeInput(input={}){
    const out = Object.assign({}, input);
    out.schemaVersion = DP_SCHEMA_VERSION;
    out.planVersion = out.planVersion || 1;
    out.withdrawalOrder = Array.isArray(out.withdrawalOrder) && out.withdrawalOrder.length ? out.withdrawalOrder : DP_WITHDRAWAL_ORDER_DEFAULT.slice();
    return out;
  }

  global.DP_VALIDATORS = { validatePlanInput, normalizeInput };
})(window);
