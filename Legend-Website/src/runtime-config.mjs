// Public websites use Protect Website as the canonical website/analytics authority.
// Keep this value centralized so the static LEGEND build and business compiler cannot drift.
export const publicApiBase = 'https://masterapp-protect.azurewebsites.net';
// Canonical published LEGEND icon selector shared by the static build and compiler.
export const publishedLegendFaviconUrl = publicApiBase + '/api/website-content/public/legend/favicon';
export const editorAuthorizationBase = 'https://portal.mylegnd.com';
export const publicRuntimeAssets = Object.freeze({
  tracking: '/legend-public-tracking.js',
  metaSignal: '/legend-public-meta-signal-intelligence.js',
  openAiMeasurement: '/legend-public-openai-measurement.js'
});
