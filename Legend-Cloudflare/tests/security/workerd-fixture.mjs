// Local test entrypoint only. Never deploy this reservation-only fixture.
import { createSecuritySession } from '../../src/security/session.mjs';
import { securityErrorResponse } from '../../src/security/errors.mjs';
export { LegendGovernance } from '../../src/security/governance.mjs';

export default {
  async fetch(request, env) {
    try {
      const session = await createSecuritySession(request, env);
      const reservation = await session.budget.reserve(session.context, { requestId: session.context.requestId,
        reservationId: 'model-1', maxCostMicrousd: 20, deadlineUnixMs: session.context.deadlineUnixMs });
      await session.close();
      return Response.json(reservation);
    } catch (error) { return securityErrorResponse(error); }
  },
};
