// Canonical Distribution Planner constants (v1)
;(function(global){
  const DP_SCHEMA_VERSION = "1.0";

  const DP_DEFAULTS = {
    inflationPct: 3.0,
    invReturnPct: 6.0,
    invTaxPct: 22.0,
    liReturnPct: 4.0,
    liTaxPct: 0.0,
    annReturnPct: 4.0,
    annTaxPct: 22.0,
    emergencyReserve: 0,
    manualBaseOverride: false,
    withdrawalOrder: ["inv","li","ann","reserve"]
  };

  const DP_BOUNDS = {
    returnMin: -50,
    returnMax: 20,
    taxMin: 0,
    taxMax: 100
  };

  const DP_HIDDEN_CONTROLS_V1 = [
    "invDownMarket","liDownMarket","annDownMarket",
    "protectInvest",
    "annIncomeRider","annDbRider","annRollup",
    "liEfficiency","liDeath","annDeath",
    "scenarioMode","gapSource","downThreshold","loanRate"
  ];

  const DP_WITHDRAWAL_ORDER_DEFAULT = ["inv","li","ann","reserve"];

  global.DP_CONSTANTS = {
    DP_SCHEMA_VERSION,
    DP_DEFAULTS,
    DP_BOUNDS,
    DP_HIDDEN_CONTROLS_V1,
    DP_WITHDRAWAL_ORDER_DEFAULT
  };
})(window);
