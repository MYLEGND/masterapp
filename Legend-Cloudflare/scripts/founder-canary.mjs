import { randomBytes, randomUUID } from 'node:crypto';
import { hmacSign, sha256 } from '../src/security/crypto.mjs';
import { FOUNDER_BASELINE_PRIMARY_MODEL } from '../src/runtime/registry.mjs';
import fs from 'node:fs';

function required(name) {
  const value = process.env[name]?.trim();
  if (!value) throw new Error(`missing_${name.toLowerCase()}`);
  return value;
}

const endpoint = new URL(required('LEGEND_CANARY_ENDPOINT'));
if (endpoint.protocol !== 'https:' || endpoint.pathname !== '/v1/legend/respond' || endpoint.search)
  throw new Error('invalid_canary_endpoint');

const accountId = required('LEGEND_ACCOUNT_ID');
const tenantId = required('LEGEND_TENANT_ID');
const userId = required('LEGEND_FOUNDER_USER_ID');
const keyId = required('LEGEND_SERVICE_KEY_ID');
const secret = required('LEGEND_SERVICE_SIGNING_KEY');
const now = Date.now();
const requestId = randomUUID();
const nonce = randomBytes(18).toString('base64url');
const sessionId = randomUUID();
const conversationId = randomUUID();
const envelope = {
  version: 'legend-cloudflare.v1',
  requestId,
  issuedAt: now,
  expiresAt: now + 60_000,
  scope: {
    accountId, tenantId, userId, sessionId, conversationId,
    roles: ['Founder'], authorizationVersion: 'release-canary-v1',
  },
  task: {
    kind: 'general',
    messages: [{ role: 'user', content: 'Reply with a short acknowledgement that the LEGEND Founder foundation is reachable.' }],
    tools: [],
    requiredCapabilities: [],
  },
  limits: {
    deadlineUnixMs: now + 55_000,
    maxOutputTokens: 128,
    maxIterations: 1,
    maxModelCalls: 1,
    maxToolCalls: 0,
    maxCostMicrousd: 3_000_000,
  },
  stream: false,
};
const body = JSON.stringify(envelope);
const bodyDigest = await sha256(new TextEncoder().encode(body));
const signing = ['legend-service.v1', 'POST', '/v1/legend/respond', keyId, String(now), nonce, bodyDigest].join('\n');
const signature = await hmacSign(secret, signing);
const response = await fetch(endpoint, {
  method: 'POST',
  headers: {
    'Content-Type': 'application/json',
    'X-Legend-Key-Id': keyId,
    'X-Legend-Timestamp': String(now),
    'X-Legend-Nonce': nonce,
    'X-Legend-Signature': signature,
  },
  body,
  signal: AbortSignal.timeout(65_000),
});
const payload = await response.json().catch(() => ({}));
if (!response.ok || payload?.status !== 'completed') throw new Error(`canary_failed_${payload?.error?.code ?? response.status}`);
if (payload?.provider?.name !== 'cloudflare-workers-ai' || payload?.provider?.hosting !== 'cloudflare')
  throw new Error('canary_provider_provenance_invalid');
if (payload?.provider?.modelId !== FOUNDER_BASELINE_PRIMARY_MODEL)
  throw new Error('canary_primary_model_invalid');
if (typeof payload?.text !== 'string' || !payload.text.trim())
  throw new Error('canary_answer_missing');
if (!Number.isSafeInteger(payload?.usage?.costMicrousd) || payload.usage.costMicrousd < 0 ||
    !['provider_usage', 'reserved_upper_bound'].includes(payload?.usage?.costEvidence))
  throw new Error('canary_cost_receipt_invalid');

const receipt = {
  schemaVersion: 1,
  responseAuthority: 'HostedFoundation',
  provider: 'Cloudflare Workers AI',
  foundationHosting: 'CloudflareHosted',
  foundationModel: payload.provider.modelId,
  billing: 'Cloudflare Workers AI',
  openAiApiUsed: false,
  openAiTeacherEscalation: false,
  costMicrousd: payload.usage.costMicrousd,
  costEvidence: payload.usage.costEvidence,
};
const output = process.env.LEGEND_CANARY_OUTPUT?.trim();
if (output) fs.writeFileSync(output, JSON.stringify(receipt, null, 2) + '\n', { mode: 0o600 });
console.log(JSON.stringify(receipt));
