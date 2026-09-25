import { createSecuritySession } from './security/session.mjs';
import { securityErrorResponse } from './security/errors.mjs';
import { orchestrate, createEventStream } from './runtime/orchestrator.mjs';
export { LegendGovernance } from './security/governance.mjs';

const headers = { 'Cache-Control': 'no-store, private', 'X-Content-Type-Options': 'nosniff' };

export default {
  async fetch(request, env, execution) {
    const url = new URL(request.url);
    if (url.pathname !== '/v1/legend/respond') return new Response(null, { status: 404, headers });
    if (request.method !== 'POST') return new Response(null, { status: 405, headers: { ...headers, Allow: 'POST' } });
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
