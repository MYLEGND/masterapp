/** Codes, rather than request bodies/provider exceptions, may cross the boundary. */
export class SecurityError extends Error {
  constructor(code, status = 403) {
    super(code);
    this.name = 'SecurityError';
    this.code = code;
    this.status = status;
  }
}

export function requireSecurity(condition, code, status = 403) {
  if (!condition) throw new SecurityError(code, status);
}

export function securityErrorResponse(error) {
  const trusted = error instanceof SecurityError;
  return Response.json({ error: trusted ? error.code : 'security_unavailable' }, {
    status: trusted ? error.status : 503,
    headers: {
      'Cache-Control': 'no-store, private',
      'Pragma': 'no-cache',
      'X-Content-Type-Options': 'nosniff',
    },
  });
}
