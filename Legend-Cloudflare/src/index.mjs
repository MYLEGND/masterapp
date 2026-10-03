import { createSecuritySession } from './security/session.mjs';
import { authenticateRequest, CONTROL_PATH, RESPOND_PATH, STATUS_PATH } from './security/authenticate.mjs';
import { createGovernanceClient } from './security/governance.mjs';
import { requireSecurity, SecurityError, securityErrorResponse } from './security/errors.mjs';
import { FOUNDER_BASELINE_POLICY_VERSION, MODEL_REGISTRY, RuntimeFailure, resolveExecutionPolicy } from './runtime/registry.mjs';
import { orchestrate, createEventStream } from './runtime/orchestrator.mjs';
export { LegendGovernance } from './security/governance.mjs';

const headers = { 'Cache-Control': 'no-store, private', 'X-Content-Type-Options': 'nosniff' };

function founderStatusPayload(envelope, policy, budget) {
  const models = MODEL_REGISTRY.filter(model => policy.modelIds?.includes(model.id)).map(model => ({
    id: model.id, role: model.role, contextTokens: model.contextTokens,
    inputUsdPerMillion: model.inputUsdPerMillion, outputUsdPerMillion: model.outputUsdPerMillion,
  }));
  return {
    version: 'legend-cloudflare.v1', requestId: envelope.requestId, status: 'ready',
    provider: { name: 'cloudflare-workers-ai', hosting: 'cloudflare' },
    billing: 'Cloudflare Workers AI',
    executionMode: policy.mode,
    policyVersion: policy.policyVersion ?? FOUNDER_BASELINE_POLICY_VERSION,
    policyPersistence: 'persistent',
    primaryModelId: policy.primaryModelId,
    models,
    budget,
  };
}

async function founderControlContext(request, env, targetPath) {
  const { envelope, context } = await authenticateRequest(request, env, { targetPath });
  let policy;
  try { policy = resolveExecutionPolicy(env, envelope, context); }
  catch (error) {
    if (error instanceof RuntimeFailure)
      throw new SecurityError(error.code, error.code.includes('configuration') || error.code.endsWith('_required') ? 503 : 403);
    throw error;
  }
  requireSecurity(policy.mode === 'founder_baseline' &&
    Array.isArray(context.roles) && context.roles.length === 1 && context.roles[0] === 'Founder',
  'founder_control_scope_denied', 403);
  return { envelope, context, policy, governance: createGovernanceClient(env, context) };
}

export default {
  async fetch(request, env, execution) {
    const url = new URL(request.url);
    if (![RESPOND_PATH, STATUS_PATH, CONTROL_PATH].includes(url.pathname))
      return new Response(null, { status: 404, headers });
    if (request.method !== 'POST')
      return new Response(null, { status: 405, headers: { ...headers, Allow: 'POST' } });

    if (url.pathname === STATUS_PATH) {
      try {
        const session = await founderControlContext(request, env, STATUS_PATH);
        return Response.json(founderStatusPayload(session.envelope, session.policy, await session.governance.status()),
          { headers });
      } catch (error) { return securityErrorResponse(error); }
    }

    if (url.pathname === CONTROL_PATH) {
      try {
        const session = await founderControlContext(request, env, CONTROL_PATH);
        const control = session.envelope.control;
        requireSecurity(control && typeof control === 'object' && !Array.isArray(control), 'founder_control_invalid', 400);
        const keys = Object.keys(control);
        if (control.action === 'set_pause') {
          requireSecurity(keys.length === 2 && keys.includes('action') && keys.includes('paused') &&
            typeof control.paused === 'boolean', 'founder_control_invalid', 400);
        } else if (control.action === 'set_spend_cap') {
          requireSecurity(keys.length === 2 && keys.includes('action') && keys.includes('spendCapMicrousd') &&
            Number.isSafeInteger(control.spendCapMicrousd) && control.spendCapMicrousd >= 0,
          'founder_control_invalid', 400);
        } else requireSecurity(false, 'founder_control_invalid', 400);
        const budget = await session.governance.control(control);
        return Response.json({ ...founderStatusPayload(session.envelope, session.policy, budget), controlApplied: control.action },
          { headers });
      } catch (error) { return securityErrorResponse(error); }
    }

    let session;
    try {
      session = await createSecuritySession(request, env);
      const run = async ({ signal, onEvent } = {}) => {
        try {
          return await orchestrate({ ...session, env, signal: signal ?? request.signal, onEvent });
        } finally {
          // Closing the lease does not forgive unsettled provider reservations.
          await session.close();
        }
      };
      if (session.envelope.stream) {
        const stream = createEventStream(run, request.signal);
        execution.waitUntil(stream.completion);
        return new Response(stream.readable, { headers: { ...headers, 'Content-Type': 'text/event-stream' } });
      }
      const response = await run();
      return Response.json(response, { status: response.status === 'completed' ? 200 : 503, headers });
    } catch (error) {
      return securityErrorResponse(error);
    }
  },
};
