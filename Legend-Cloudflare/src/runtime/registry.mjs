// Operator-owned registry. Catalog presence does not qualify an engine for use.
export const REGISTRY_VERSION = '2026-09-18.3';

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
  reasoning: Object.freeze({
    parameter: outputLimitParameter === 'max_completion_tokens' ? 'reasoning_effort' : null,
    supportedEfforts: Object.freeze(outputLimitParameter === 'max_completion_tokens' ? ['low', 'medium', 'high', 'provider_default'] : ['provider_default']),
    defaultEffort: outputLimitParameter === 'max_completion_tokens' ? 'high' : 'provider_default',
  }),
  capabilities: Object.freeze(['text', 'tools', 'reasoning', 'streaming', ...(vision ? ['vision'] : [])]),
  enabled: false, qualification: null,
  documentation: `https://developers.cloudflare.com/workers-ai/models/${id.split('/').at(-1)}/`,
  verifiedCatalogAt: '2026-09-18',
})));

export class RuntimeFailure extends Error {
  constructor(code, retryable = false) { super(code); this.name = 'RuntimeFailure'; this.code = code; this.retryable = retryable; }
}

const identifier = value => typeof value === 'string' && /^[A-Za-z0-9_.:@-]{1,128}$/.test(value);
const positiveCost = value => Number.isSafeInteger(value) && value > 0 && value <= 1_000_000_000_000;

/** Operator-only settings, constrained to the authenticated schema's current wire format. */
export function resolveModelSettings(env, model) {
  let configured;
  if (env.LEGEND_MODEL_SETTINGS_JSON !== undefined) {
    let policy;
    try { policy = JSON.parse(env.LEGEND_MODEL_SETTINGS_JSON); }
    catch { throw new RuntimeFailure('model_settings_invalid'); }
    if (!policy || policy.version !== 'legend-model-settings.v1' || !policy.models || Array.isArray(policy.models)
      || typeof policy.models !== 'object' || Object.keys(policy).some(key => !['version', 'models'].includes(key))) throw new RuntimeFailure('model_settings_invalid');
    for (const [id, settings] of Object.entries(policy.models)) {
      const candidate = MODEL_REGISTRY.find(item => item.id === id);
      if (!candidate || !settings || typeof settings !== 'object' || Array.isArray(settings)
        || Object.keys(settings).length !== 1 || !Object.hasOwn(settings, 'reasoningEffort')) throw new RuntimeFailure('model_settings_invalid');
      if (!candidate.reasoning.supportedEfforts.includes(settings.reasoningEffort)) throw new RuntimeFailure('model_reasoning_unsupported');
    }
    configured = policy.models[model.id];
  }
  const reasoningEffort = configured?.reasoningEffort ?? model.reasoning.defaultEffort;
  return Object.freeze({ reasoningEffort,
    reasoningParameter: reasoningEffort === 'provider_default' ? null : model.reasoning.parameter,
    source: configured ? 'operator' : 'registry_default' });
}

const MANUAL_TEST_MODEL = '@cf/openai/gpt-oss-120b';
const MANUAL_TEST_MAX_COST_MICROUSD = 3_000_000;

function resolveFounderManualTestPolicy(env, envelope, context, now) {
  let policy; let budget;
  try {
    policy = JSON.parse(env.LEGEND_MANUAL_TEST_POLICY_JSON);
    budget = JSON.parse(env.LEGEND_BUDGET_POLICY_JSON);
  } catch { throw new RuntimeFailure('manual_test_configuration_missing'); }
  const fields = ['version', 'accountId', 'tenantId', 'founderUserId', 'serviceKeyId',
    'requiredRole', 'environment', 'modelId', 'expiresAt', 'lifetimeCostMicrousd'];
  if (!policy || typeof policy !== 'object' || Array.isArray(policy)
    || Object.keys(policy).length !== fields.length || Object.keys(policy).some(key => !fields.includes(key))
    || policy.version !== 'legend-founder-manual-test.v1' || policy.modelId !== MANUAL_TEST_MODEL
    || !['accountId', 'tenantId', 'founderUserId', 'serviceKeyId', 'environment'].every(key => identifier(policy[key]))
    || policy.accountId !== env.LEGEND_ACCOUNT_ID || policy.environment !== env.LEGEND_DEPLOYMENT_ENVIRONMENT
    || policy.requiredRole !== 'Founder' || !positiveCost(policy.lifetimeCostMicrousd)
    || policy.lifetimeCostMicrousd > MANUAL_TEST_MAX_COST_MICROUSD
    || !Number.isSafeInteger(policy.expiresAt) || policy.expiresAt <= now || policy.expiresAt > now + 86400000)
    throw new RuntimeFailure('manual_test_configuration_invalid');
  if (budget?.period !== 'lifetime' || !positiveCost(budget.accountMicrousd)
    || budget.accountMicrousd > policy.lifetimeCostMicrousd)
    throw new RuntimeFailure('manual_test_lifetime_budget_required');
  const scope = envelope.scope;
  if (!context || !scope || context.accountId !== policy.accountId || context.tenantId !== policy.tenantId
    || context.userId !== policy.founderUserId || context.keyId !== policy.serviceKeyId
    || !identifier(context.requestId) || context.requestId !== envelope.requestId
    || ['accountId', 'tenantId', 'userId', 'sessionId', 'conversationId', 'authorizationVersion']
      .some(key => !identifier(context[key]) || context[key] !== scope[key])
    || !Array.isArray(context.roles) || !Array.isArray(scope.roles)
    || !context.roles.includes('Founder') || !context.roles.every(identifier)
    || new Set(context.roles).size !== context.roles.length
    || context.roles.length !== scope.roles.length || context.roles.some(role => !scope.roles.includes(role)))
    throw new RuntimeFailure('manual_test_scope_denied');
  if (!Number.isSafeInteger(envelope.limits?.deadlineUnixMs) || envelope.limits.deadlineUnixMs <= now
    || envelope.limits.deadlineUnixMs > policy.expiresAt) throw new RuntimeFailure('manual_test_deadline_exceeded');
  // This mode adds no dispatcher or tool authority. The existing signed broker
  // still reauthorizes every tool on Azure; no model-authored approval is accepted.
  if (envelope.task.tools?.length && env.LEGEND_TOOL_CALLBACK_ENABLED !== 'true')
    throw new RuntimeFailure('manual_test_signed_tools_required');
  return Object.freeze({ mode: 'founder_manual_test', modelId: MANUAL_TEST_MODEL,
    accountId: policy.accountId, expiresAt: policy.expiresAt });
}

/** Only deployment bindings and the authenticated session may grant exceptional access. */
export function resolveExecutionPolicy(env, envelope, context, now = Date.now()) {
  const mode = env.LEGEND_RUNTIME_MODE ?? 'production';
  // Production does not inspect or honor qualification flags in either env or request.
  if (mode === 'production') return Object.freeze({ mode });
  if (mode === 'founder_manual_test') return resolveFounderManualTestPolicy(env, envelope, context, now);
  if (mode !== 'qualification' || env.LEGEND_DEPLOYMENT_ENVIRONMENT !== 'qualification') throw new RuntimeFailure('runtime_mode_invalid');
  let policy; let budget;
  try {
    policy = JSON.parse(env.LEGEND_QUALIFICATION_POLICY_JSON);
    budget = JSON.parse(env.LEGEND_BUDGET_POLICY_JSON);
  } catch { throw new RuntimeFailure('qualification_configuration_missing'); }
  if (policy?.version !== 'legend-qualification.v1' || !identifier(policy.accountId) || policy.accountId !== env.LEGEND_ACCOUNT_ID
    || !MODEL_REGISTRY.some(model => model.id === policy.modelId)
    || !identifier(policy.tenantId) || !identifier(policy.serviceKeyId) || policy.requiredRole !== 'LegendQualification'
    || !Array.isArray(policy.allowedUserIds) || !policy.allowedUserIds.length || policy.allowedUserIds.length > 32
    || !policy.allowedUserIds.every(identifier) || new Set(policy.allowedUserIds).size !== policy.allowedUserIds.length
    || !/^[a-f0-9]{64}$/.test(policy.suiteSha256 ?? '') || !positiveCost(policy.lifetimeCostMicrousd)
    || !Number.isSafeInteger(policy.expiresAt) || policy.expiresAt <= now || policy.expiresAt > now + 86400000) throw new RuntimeFailure('qualification_configuration_invalid');
  // The account-named durable ledger supplies atomic lifetime enforcement. This
  // deployment cap is the authorized metered remainder after nonmetered charges.
  if (budget?.period !== 'lifetime' || !positiveCost(budget.accountMicrousd)
    || budget.accountMicrousd > policy.lifetimeCostMicrousd) throw new RuntimeFailure('qualification_lifetime_budget_required');
  if (!context || context.accountId !== policy.accountId || context.tenantId !== policy.tenantId
    || !policy.allowedUserIds.includes(context.userId) || context.keyId !== policy.serviceKeyId
    || !Array.isArray(context.roles) || !context.roles.includes(policy.requiredRole) || context.requestId !== envelope.requestId
    || ['accountId', 'tenantId', 'userId', 'sessionId', 'conversationId'].some(key => context[key] !== envelope.scope[key])) throw new RuntimeFailure('qualification_scope_denied');
  if (envelope.limits.deadlineUnixMs > policy.expiresAt) throw new RuntimeFailure('qualification_deadline_exceeded');
  if (envelope.task.tools?.length) throw new RuntimeFailure('qualification_tools_disabled');
  return Object.freeze({ mode, modelId: policy.modelId, accountId: policy.accountId,
    suiteSha256: policy.suiteSha256, expiresAt: policy.expiresAt });
}

export function estimateCostMicrousd(model, inputTokens, outputTokens) {
  if (![inputTokens, outputTokens].every(n => Number.isSafeInteger(n) && n >= 0)) throw new RuntimeFailure('invalid_usage');
  // USD / million tokens is numerically equal to micro-USD / token.
  return Math.ceil(inputTokens * model.inputUsdPerMillion + outputTokens * model.outputUsdPerMillion);
}

export function routeModel({ task, accountId, inputTokens, maxOutputTokens, remainingCostMicrousd, registry = MODEL_REGISTRY, now = Date.now(), excluded = [], executionPolicy = { mode: 'production' } }) {
  const capabilities = new Set(['text', ...(task.requiredCapabilities ?? []), ...(task.tools?.length ? ['tools'] : [])]);
  const qualified = model => executionPolicy.mode === 'founder_manual_test'
    ? executionPolicy.accountId === accountId && executionPolicy.expiresAt > now
      && executionPolicy.modelId === MANUAL_TEST_MODEL && model.id === MANUAL_TEST_MODEL
    : executionPolicy.mode === 'qualification'
    ? executionPolicy.accountId === accountId && executionPolicy.expiresAt > now && model.id === executionPolicy.modelId
    : model.enabled === true && typeof accountId === 'string' && model.qualification?.accountId === accountId
      && model.qualification?.accountCanaryPassed === true && model.qualification?.heldOutPassed === true && model.qualification?.expiresAt > now;
  const eligible = registry.filter(model => qualified(model) && model.provider === 'cloudflare-workers-ai' && model.hosting === 'cloudflare'
    && !excluded.includes(model.id)
    && [...capabilities].every(capability => model.capabilities.includes(capability))
    && inputTokens + maxOutputTokens <= model.contextTokens
    && estimateCostMicrousd(model, inputTokens, maxOutputTokens) <= remainingCostMicrousd);
  eligible.sort((a, b) => Number(b.role === task.kind) - Number(a.role === task.kind)
    || estimateCostMicrousd(a, inputTokens, maxOutputTokens) - estimateCostMicrousd(b, inputTokens, maxOutputTokens)
    || a.id.localeCompare(b.id));
  if (!eligible.length) throw new RuntimeFailure('no_qualified_model');
  return eligible[0];
}
