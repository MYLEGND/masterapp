import { authenticateRequest } from './authenticate.mjs';
import { createGovernanceClient } from './governance.mjs';
import { createToolBroker } from './tool-broker.mjs';

/** The Worker must call this before retrieval, model inference or tools. */
export async function createSecuritySession(request, env, options = {}) {
  const { envelope, context } = await authenticateRequest(request, env, options);
  const governance = createGovernanceClient(env, context);
  await governance.claim();
  const budget = Object.freeze({ reserve: governance.reserve, settle: governance.settle });
  return Object.freeze({ envelope, context, budget,
    toolBroker: createToolBroker({ env, context, budget }), close: governance.close });
}
