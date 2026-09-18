// Operator-owned registry. Catalog presence does not qualify an engine for use.
export const REGISTRY_VERSION = '2026-09-18.1';

const candidates = [
  ['@cf/qwen/qwen3-30b-a3b-fp8', 'efficient', 32768, 0.0509, 0.335, false, 'max_tokens'],
  ['@cf/openai/gpt-oss-120b', 'general', 128000, 0.35, 0.75, false, 'max_tokens'],
  ['@cf/zai-org/glm-5.3-flash', 'coding', 1310720, 0.15, 0.50, true, 'max_completion_tokens'],
  ['@cf/zai-org/glm-5.3', 'architecture', 1310720, 1.40, 4.40, false, 'max_completion_tokens'],
  ['@cf/deepseek-ai/deepseek-v4-pro-0813', 'reasoning', 1048576, 1.32, 3.96, false, 'max_completion_tokens'],
];

export const MODEL_REGISTRY = Object.freeze(candidates.map(([id, role, contextTokens, inputUsdPerMillion, outputUsdPerMillion, vision, outputLimitParameter]) => Object.freeze({
  id, role, contextTokens, inputUsdPerMillion, outputUsdPerMillion, outputLimitParameter,
  provider: 'cloudflare-workers-ai', hosting: 'cloudflare',
  capabilities: Object.freeze(['text', 'tools', 'reasoning', 'streaming', ...(vision ? ['vision'] : [])]),
  enabled: false, qualification: null,
  documentation: `https://developers.cloudflare.com/workers-ai/models/${id.split('/').at(-1)}/`,
  verifiedCatalogAt: '2026-09-18',
})));

export class RuntimeFailure extends Error {
  constructor(code, retryable = false) { super(code); this.name = 'RuntimeFailure'; this.code = code; this.retryable = retryable; }
}

export function estimateCostMicrousd(model, inputTokens, outputTokens) {
  if (![inputTokens, outputTokens].every(n => Number.isSafeInteger(n) && n >= 0)) throw new RuntimeFailure('invalid_usage');
  // USD / million tokens is numerically equal to micro-USD / token.
  return Math.ceil(inputTokens * model.inputUsdPerMillion + outputTokens * model.outputUsdPerMillion);
}

export function routeModel({ task, accountId, inputTokens, maxOutputTokens, remainingCostMicrousd, registry = MODEL_REGISTRY, now = Date.now(), excluded = [] }) {
  const capabilities = new Set(['text', ...(task.requiredCapabilities ?? []), ...(task.tools?.length ? ['tools'] : [])]);
  const eligible = registry.filter(model => model.enabled === true && model.provider === 'cloudflare-workers-ai' && model.hosting === 'cloudflare'
    && typeof accountId === 'string' && model.qualification?.accountId === accountId
    && model.qualification?.accountCanaryPassed === true && model.qualification?.heldOutPassed === true
    && model.qualification?.expiresAt > now && !excluded.includes(model.id)
    && [...capabilities].every(capability => model.capabilities.includes(capability))
    && inputTokens + maxOutputTokens <= model.contextTokens
    && estimateCostMicrousd(model, inputTokens, maxOutputTokens) <= remainingCostMicrousd);
  eligible.sort((a, b) => Number(b.role === task.kind) - Number(a.role === task.kind)
    || estimateCostMicrousd(a, inputTokens, maxOutputTokens) - estimateCostMicrousd(b, inputTokens, maxOutputTokens)
    || a.id.localeCompare(b.id));
  if (!eligible.length) throw new RuntimeFailure('no_qualified_model');
  return eligible[0];
}
