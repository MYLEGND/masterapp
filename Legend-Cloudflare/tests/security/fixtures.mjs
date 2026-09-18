import { createHmac, createHash } from 'node:crypto';
import { LegendGovernance, createGovernanceClient } from '../../src/security/governance.mjs';
import { authenticateRequest } from '../../src/security/authenticate.mjs';

export const NOW = Date.UTC(2026, 8, 18, 12);
export const TEST_KEY = Buffer.alloc(32, 91).toString('base64');

export function envelope(overrides = {}) {
  return { version: 'legend-cloudflare.v1', requestId: 'request-1', issuedAt: NOW, expiresAt: NOW + 120000,
    scope: { accountId: 'account-1', tenantId: 'tenant-1', userId: 'user-1', sessionId: 'session-1',
      conversationId: 'conversation-1', roles: ['founder'], authorizationVersion: 'permission-v1' },
    task: { kind: 'reasoning', messages: [{ role: 'user', content: 'A private request.' }], tools: [], requiredCapabilities: [] },
    limits: { deadlineUnixMs: NOW + 120000, maxOutputTokens: 64, maxIterations: 4,
      maxModelCalls: 4, maxToolCalls: 4, maxCostMicrousd: 1000 }, stream: false, ...overrides };
}

export function signedRequest(body = envelope(), { nonce = 'a'.repeat(32), keyId = 'azure-v1', key = TEST_KEY,
  path = '/v1/legend/respond', method = 'POST' } = {}) {
  const raw = typeof body === 'string' ? body : JSON.stringify(body);
  const timestamp = String(typeof body === 'string' ? NOW : body.issuedAt);
  const digest = createHash('sha256').update(raw).digest('hex');
  const message = ['legend-service.v1', method, path, keyId, timestamp, nonce, digest].join('\n');
  const signature = createHmac('sha256', Buffer.from(key, 'base64')).update(message).digest('hex');
  return new Request(`https://legend.example${path}`, { method, body: raw, headers: {
    'Content-Type': 'application/json', 'X-Legend-Key-Id': keyId, 'X-Legend-Timestamp': timestamp,
    'X-Legend-Nonce': nonce, 'X-Legend-Signature': signature,
  } });
}

export function environment(policy = {}) {
  return { LEGEND_ACCOUNT_ID: 'account-1', LEGEND_SERVICE_KEYS_JSON: JSON.stringify({ 'azure-v1': TEST_KEY }),
    LEGEND_BUDGET_POLICY_JSON: JSON.stringify({ period: 'fixed', periodMs: 86400000, retentionMs: 86400000,
      requestMicrousd: 1000, userMicrousd: 10000, tenantMicrousd: 10000, accountMicrousd: 10000,
      requestConcurrency: 16, userConcurrency: 32, tenantConcurrency: 64, accountConcurrency: 64, ...policy }) };
}

/** Deterministic transaction simulation, not a claim of live Cloudflare proof. */
export class TransactionStorage {
  data = new Map();
  tail = Promise.resolve();
  alarmAt = null;
  async transaction(fn) {
    const result = this.tail.then(async () => {
      const draft = new Map([...this.data].map(([key, value]) => [key, structuredClone(value)]));
      const result = await fn(this.access(draft));
      this.data = draft;
      return result;
    });
    this.tail = result.catch(() => {});
    return result;
  }
  access(map) {
    return {
      get: async key => structuredClone(map.get(key)),
      put: async (key, value) => { map.set(key, structuredClone(value)); },
      delete: async key => map.delete(key),
      list: async ({ prefix = '', limit = Infinity } = {}) => new Map([...map].filter(([key]) => key.startsWith(prefix))
        .sort(([a], [b]) => a.localeCompare(b)).slice(0, limit).map(([key, value]) => [key, structuredClone(value)])),
    };
  }
  async list(options) { return this.access(this.data).list(options); }
  async setAlarm(time) { this.alarmAt = time; }
  async deleteAlarm() { this.alarmAt = null; }
}

export function harness(policy = {}) {
  let now = NOW;
  const env = environment(policy);
  const storage = new TransactionStorage();
  let object = new LegendGovernance({ storage }, env, { now: () => now });
  env.LEGEND_GOVERNANCE = { idFromName: value => value, get: () => ({
    fetch: (url, init) => object.fetch(new Request(url, init)),
  }) };
  return { env, storage, now: () => now, advance: value => { now += value; },
    restart() { object = new LegendGovernance({ storage }, env, { now: () => now }); },
    alarm: () => object.alarm(),
    async session(body = envelope(), nonce = crypto.randomUUID().replaceAll('-', '')) {
      const { context } = await authenticateRequest(signedRequest(body, { nonce }), env, { now: () => now });
      const budget = createGovernanceClient(env, context);
      await budget.claim();
      return { context, budget };
    },
  };
}

export const reserve = ({ context, budget }, reservationId = 'reservation-1', amount = 100) => budget.reserve(context,
  { requestId: context.requestId, reservationId, maxCostMicrousd: amount, deadlineUnixMs: context.deadlineUnixMs });
