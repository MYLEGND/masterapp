// Wire identity shared by Azure transport, Worker runtime and security validation.
// Protocol changes require coordinated clients and a new version, not silent fallback.
export const PROTOCOL_VERSION = 'legend-cloudflare.v1';

// Explicit model choice is v2; v1 remains supported without a manual model.
export const MODEL_SELECTION_PROTOCOL_VERSION = 'legend-cloudflare.v2';
