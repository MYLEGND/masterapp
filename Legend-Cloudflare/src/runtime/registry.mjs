// Operator-owned registry. Catalog presence does not qualify an engine for use.
export const REGISTRY_VERSION = '2026-10-01.1';

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

export const FOUNDER_BASELINE_MODEL_IDS = Object.freeze(candidates.map(([id]) => id));
export const FOUNDER_BASELINE_PRIMARY_MODEL = '@cf/openai/gpt-oss-120b';
export const FOUNDER_BASELINE_POLICY_VERSION = 'legend-founder-baseline.v3';
const FOUNDER_BASELINE_MAX_COST_MICROUSD = 3_000_000;

function readFounderBaselineConfiguration(env) {
  let policy; let budget;
  try {
    policy = JSON.parse(env.LEGEND_FOUNDER_BASELINE_POLICY_JSON);
    budget = JSON.parse(env.LEGEND_BUDGET_POLICY_JSON);
  } catch { throw new RuntimeFailure('founder_baseline_configuration_missing'); }
  const fields = ['version', 'accountId', 'tenantId', 'founderUserId', 'serviceKeyId',
    'requiredRole', 'environment', 'modelIds', 'lifetimeCostMicrousd'];
  if (!policy || typeof policy !== 'object' || Array.isArray(policy)
    || Object.keys(policy).length !== fields.length || Object.keys(policy).some(key => !fields.includes(key))
    || policy.version !== FOUNDER_BASELINE_POLICY_VERSION
    || !Array.isArray(policy.modelIds) || policy.modelIds.length !== FOUNDER_BASELINE_MODEL_IDS.length
    || policy.modelIds.some((id, index) => id !== FOUNDER_BASELINE_MODEL_IDS[index])
    || !['accountId', 'tenantId', 'founderUserId', 'serviceKeyId', 'environment'].every(key => identifier(policy[key]))
    || policy.accountId !== env.LEGEND_ACCOUNT_ID || policy.environment !== env.LEGEND_DEPLOYMENT_ENVIRONMENT
    || policy.requiredRole !== 'Founder' || !positiveCost(policy.lifetimeCostMicrousd)
    || policy.lifetimeCostMicrousd > FOUNDER_BASELINE_MAX_COST_MICROUSD)
    throw new RuntimeFailure('founder_baseline_configuration_invalid');
  if (budget?.period !== 'lifetime' || !positiveCost(budget.accountMicrousd)
    || budget.accountMicrousd > policy.lifetimeCostMicrousd)
    throw new RuntimeFailure('founder_baseline_lifetime_budget_required');
  return policy;
}

function resolveFounderBaselinePolicy(env, envelope, context) {
  const policy = readFounderBaselineConfiguration(env);
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
    throw new RuntimeFailure('founder_baseline_scope_denied');
  // The production Founder baseline itself does not expire. Every signed request
  // still has the normal short request/deadline bounds, while the Durable Object
  // lifetime ledger remains the hard spend authority.
  if (envelope.task.tools?.length && env.LEGEND_TOOL_CALLBACK_ENABLED !== 'true')
    throw new RuntimeFailure('founder_baseline_signed_tools_required');
  return Object.freeze({ mode: 'founder_baseline', modelIds: FOUNDER_BASELINE_MODEL_IDS,
    primaryModelId: FOUNDER_BASELINE_PRIMARY_MODEL, policyVersion: FOUNDER_BASELINE_POLICY_VERSION,
    lifetimeCostMicrousd: policy.lifetimeCostMicrousd, accountId: policy.accountId });
}

function validateQualificationConfiguration(env, policy, budget, now, permitExpired = false) {
  if (policy?.version !== 'legend-qualification.v1' || !identifier(policy.accountId) || policy.accountId !== env.LEGEND_ACCOUNT_ID
    || !MODEL_REGISTRY.some(model => model.id === policy.modelId)
    || !identifier(policy.tenantId) || !identifier(policy.serviceKeyId) || policy.requiredRole !== 'LegendQualification'
    || !Array.isArray(policy.allowedUserIds) || !policy.allowedUserIds.length || policy.allowedUserIds.length > 32
    || !policy.allowedUserIds.every(identifier) || new Set(policy.allowedUserIds).size !== policy.allowedUserIds.length
    || !/^[a-f0-9]{64}$/.test(policy.suiteSha256 ?? '') || !positiveCost(policy.lifetimeCostMicrousd)
    || !Number.isSafeInteger(policy.expiresAt) || policy.expiresAt <= 0
    || (!permitExpired && policy.expiresAt <= now) || policy.expiresAt > now + 86400000) throw new RuntimeFailure('qualification_configuration_invalid');
  if (budget?.period !== 'lifetime' || !positiveCost(budget.accountMicrousd)
    || budget.accountMicrousd > policy.lifetimeCostMicrousd) throw new RuntimeFailure('qualification_lifetime_budget_required');
}

function resolveQualificationPolicy(env, envelope, context, now, policy, budget) {
  validateQualificationConfiguration(env, policy, budget, now);
  if (!context || context.accountId !== policy.accountId || context.tenantId !== policy.tenantId
    || !policy.allowedUserIds.includes(context.userId) || context.keyId !== policy.serviceKeyId
    || !Array.isArray(context.roles) || !context.roles.includes(policy.requiredRole) || context.requestId !== envelope.requestId
    || ['accountId', 'tenantId', 'userId', 'sessionId', 'conversationId'].some(key => context[key] !== envelope.scope[key])) throw new RuntimeFailure('qualification_scope_denied');
  if (envelope.limits.deadlineUnixMs > policy.expiresAt) throw new RuntimeFailure('qualification_deadline_exceeded');
  if (envelope.task.tools?.length) throw new RuntimeFailure('qualification_tools_disabled');
  return Object.freeze({ mode: 'qualification', modelId: policy.modelId, accountId: policy.accountId,
    suiteSha256: policy.suiteSha256, expiresAt: policy.expiresAt });
}

// Optional operator-only qualification scopes share this Worker's existing
// authenticated session and account ledger. No request field selects a policy.
function resolveColocatedQualificationPolicy(env, envelope, context, now) {
  if (env.LEGEND_QUALIFICATION_POLICIES_JSON === undefined) return null;
  const baseline = readFounderBaselineConfiguration(env);
  let config; let budget;
  try {
    config = JSON.parse(env.LEGEND_QUALIFICATION_POLICIES_JSON);
    budget = JSON.parse(env.LEGEND_BUDGET_POLICY_JSON);
  } catch { throw new RuntimeFailure('qualification_configuration_invalid'); }
  if (!config || typeof config !== 'object' || Array.isArray(config)
    || Object.keys(config).length !== 2 || config.version !== 'legend-qualification-policies.v1'
    || !Array.isArray(config.policies) || !config.policies.length || config.policies.length > MODEL_REGISTRY.length)
    throw new RuntimeFailure('qualification_configuration_invalid');
  const fields = ['version', 'accountId', 'modelId', 'tenantId', 'allowedUserIds', 'requiredRole',
    'serviceKeyId', 'suiteSha256', 'expiresAt', 'lifetimeCostMicrousd'];
  const keys = new Set(); const models = new Set();
  for (const policy of config.policies) {
    if (!policy || typeof policy !== 'object' || Array.isArray(policy)
      || Object.keys(policy).length !== fields.length || Object.keys(policy).some(key => !fields.includes(key)))
      throw new RuntimeFailure('qualification_configuration_invalid');
    // An expired unrelated grant is inert, not a shutdown of the Founder baseline
    // or another candidate. The selected grant is checked strictly below.
    validateQualificationConfiguration(env, policy, budget, now, true);
    if (keys.has(policy.serviceKeyId) || models.has(policy.modelId)
      || policy.serviceKeyId === baseline.serviceKeyId || policy.tenantId === baseline.tenantId
      || policy.allowedUserIds.includes(baseline.founderUserId)
      || policy.lifetimeCostMicrousd > baseline.lifetimeCostMicrousd)
      throw new RuntimeFailure('qualification_configuration_invalid');
    keys.add(policy.serviceKeyId); models.add(policy.modelId);
  }
  const policy = config.policies.find(item => item.serviceKeyId === context?.keyId);
  const hasTestRole = Array.isArray(context?.roles) && context.roles.includes('LegendQualification');
  if (!policy && !hasTestRole) return null;
  if (!policy || !hasTestRole || context.roles.length !== 1
    || !Array.isArray(envelope.scope?.roles) || envelope.scope.roles.length !== 1 || envelope.scope.roles[0] !== 'LegendQualification'
    || ['accountId', 'tenantId', 'userId', 'sessionId', 'conversationId', 'authorizationVersion']
      .some(key => !identifier(context[key]) || context[key] !== envelope.scope[key]))
    throw new RuntimeFailure('qualification_scope_denied');
  return resolveQualificationPolicy(env, envelope, context, now, policy, budget);
}

/** Only deployment bindings and the authenticated session may grant exceptional access. */
export function resolveExecutionPolicy(env, envelope, context, now = Date.now()) {
  const mode = env.LEGEND_RUNTIME_MODE ?? 'production';
  // Production does not inspect or honor qualification flags in either env or request.
  if (mode === 'production') return Object.freeze({ mode });
  if (mode === 'founder_baseline') return resolveColocatedQualificationPolicy(env, envelope, context, now)
    ?? resolveFounderBaselinePolicy(env, envelope, context);
  if (mode !== 'qualification' || env.LEGEND_DEPLOYMENT_ENVIRONMENT !== 'qualification') throw new RuntimeFailure('runtime_mode_invalid');
  let policy; let budget;
  try {
    policy = JSON.parse(env.LEGEND_QUALIFICATION_POLICY_JSON);
    budget = JSON.parse(env.LEGEND_BUDGET_POLICY_JSON);
  } catch { throw new RuntimeFailure('qualification_configuration_missing'); }
  return resolveQualificationPolicy(env, envelope, context, now, policy, budget);
}

export function estimateCostMicrousd(model, inputTokens, outputTokens) {
  if (![inputTokens, outputTokens].every(n => Number.isSafeInteger(n) && n >= 0)) throw new RuntimeFailure('invalid_usage');
  // USD / million tokens is numerically equal to micro-USD / token.
  return Math.ceil(inputTokens * model.inputUsdPerMillion + outputTokens * model.outputUsdPerMillion);
}

export function routeModel({ task, accountId, inputTokens, maxOutputTokens, remainingCostMicrousd, registry = MODEL_REGISTRY, now = Date.now(), excluded = [], executionPolicy = { mode: 'production' } }) {
  const capabilities = new Set(['text', ...(task.requiredCapabilities ?? []), ...(task.tools?.length ? ['tools'] : [])]);
  const qualified = model => executionPolicy.mode === 'founder_baseline'
    ? executionPolicy.accountId === accountId
      && Array.isArray(executionPolicy.modelIds) && executionPolicy.modelIds.includes(model.id)
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
