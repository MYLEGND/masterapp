import { SecurityError } from './errors.mjs';

/**
 * No cloud image/toolchain, paid ceiling or non-root/egress isolation has been
 * verified for this account. Do not expose a shell or run repository scripts on
 * the Mac or inside Azure while these release gates remain unmet.
 */
export const CLOUD_EXECUTION_STATUS = Object.freeze({ enabled: false, code: 'cloud_sandbox_unverified' });

export async function executeGeneratedCode() {
  throw new SecurityError(CLOUD_EXECUTION_STATUS.code, 503);
}
