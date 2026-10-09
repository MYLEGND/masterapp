import { randomBytes, randomUUID } from 'node:crypto';
import { hmacSign, sha256 } from '../src/security/crypto.mjs';
import { FOUNDER_BASELINE_MODEL_IDS, FOUNDER_BASELINE_POLICY_VERSION, FOUNDER_BASELINE_PRIMARY_MODEL } from '../src/runtime/registry.mjs';
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
const sessionId = randomUUID();

async function signedPost(path, body, timeoutMs = 65_000) {
  const target = new URL(endpoint);
  target.pathname = path;
  target.search = '';
  const raw = JSON.stringify(body);
  const nonce = randomBytes(18).toString('base64url');
  const bodyDigest = await sha256(new TextEncoder().encode(raw));
  const signing = ['legend-service.v1', 'POST', path, keyId, String(body.issuedAt), nonce, bodyDigest].join('\n');
  const signature = await hmacSign(secret, signing);
  const response = await fetch(target, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'X-Legend-Key-Id': keyId,
      'X-Legend-Timestamp': String(body.issuedAt),
      'X-Legend-Nonce': nonce,
      'X-Legend-Signature': signature,
    },
    body: raw,
    signal: AbortSignal.timeout(timeoutMs),
  });
  return { response, payload: await response.json().catch(() => ({})) };
}

function scope(conversationId) {
  return {
    accountId, tenantId, userId, sessionId, conversationId,
    roles: ['Founder'], authorizationVersion: 'release-canary-v1',
  };
}

const now = Date.now();
const requestId = randomUUID();
const envelope = {
  version: 'legend-cloudflare.v1',
  requestId,
  issuedAt: now,
  expiresAt: now + 60_000,
  scope: scope(randomUUID()),
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
const inference = await signedPost('/v1/legend/respond', envelope);
const response = inference.response;
const payload = inference.payload;
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

const statusNow = Date.now();
const statusEnvelope = {
  version: 'legend-cloudflare.v1',
  requestId: randomUUID(),
  issuedAt: statusNow,
  expiresAt: statusNow + 30_000,
  scope: scope(randomUUID()),
  task: {
    kind: 'general',
    messages: [{ role: 'user', content: 'Founder control-plane status request.' }],
    tools: [],
    requiredCapabilities: ['text'],
  },
  limits: {
    deadlineUnixMs: statusNow + 25_000,
    maxOutputTokens: 1,
    maxIterations: 1,
    maxModelCalls: 1,
    maxToolCalls: 0,
    maxCostMicrousd: 1,
  },
  stream: false,
};
const status = await signedPost('/v1/legend/status', statusEnvelope, 30_000);
if (!status.response.ok || status.payload?.status !== 'ready')
  throw new Error(`control_plane_canary_failed_${status.payload?.error ?? status.response.status}`);
if (status.payload?.executionMode !== 'founder_baseline' ||
    status.payload?.policyVersion !== FOUNDER_BASELINE_POLICY_VERSION ||
    status.payload?.policyPersistence !== 'persistent')
  throw new Error('control_plane_policy_invalid');
if (status.payload?.provider?.name !== 'cloudflare-workers-ai' ||
    status.payload?.billing !== 'Cloudflare Workers AI')
  throw new Error('control_plane_billing_invalid');
if (!Array.isArray(status.payload?.models) ||
    status.payload.models.length !== FOUNDER_BASELINE_MODEL_IDS.length ||
    status.payload.models.some((model, index) => model?.id !== FOUNDER_BASELINE_MODEL_IDS[index]))
  throw new Error('control_plane_model_registry_invalid');
if (!Number.isSafeInteger(status.payload?.budget?.releaseAuthorizedMicrousd) ||
    !Number.isSafeInteger(status.payload?.budget?.remainingMicrousd) ||
    status.payload.budget.releaseAuthorizedMicrousd !== 3_000_000 ||
    status.payload.budget.paused !== false)
  throw new Error('control_plane_budget_invalid');

const receipt = {
  schemaVersion: 2,
  responseAuthority: 'HostedFoundation',
  provider: 'Cloudflare Workers AI',
  foundationHosting: 'CloudflareHosted',
  foundationModel: payload.provider.modelId,
  billing: 'Cloudflare Workers AI',
  openAiApiUsed: false,
  openAiTeacherEscalation: false,
  costMicrousd: payload.usage.costMicrousd,
  costEvidence: payload.usage.costEvidence,
  controlPlaneVerified: true,
  policyVersion: status.payload.policyVersion,
  policyPersistence: status.payload.policyPersistence,
  modelCount: status.payload.models.length,
  releaseAuthorizedMicrousd: status.payload.budget.releaseAuthorizedMicrousd,
  remainingMicrousd: status.payload.budget.remainingMicrousd,
};
const output = process.env.LEGEND_CANARY_OUTPUT?.trim();
if (output) fs.writeFileSync(output, JSON.stringify(receipt, null, 2) + '\n', { mode: 0o600 });
console.log(JSON.stringify(receipt));
