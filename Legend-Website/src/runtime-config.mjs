// Public websites use Protect Website as the canonical website/analytics authority.
// Keep this value centralized so the static LEGEND build and business compiler cannot drift.
export const publicApiBase = 'https://masterapp-protect.azurewebsites.net';
export const publicRuntimeAssets = Object.freeze({
  tracking: '/legend-public-tracking.js',
  metaSignal: '/legend-public-meta-signal-intelligence.js'
});
