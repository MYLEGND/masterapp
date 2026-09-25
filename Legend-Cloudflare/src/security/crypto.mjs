import { requireSecurity } from './errors.mjs';

const encoder = new TextEncoder();
export const utf8 = value => encoder.encode(value);

export function base64ToBytes(value) {
  requireSecurity(typeof value === 'string' && /^[A-Za-z0-9+/]+={0,2}$/.test(value) && value.length % 4 === 0,
    'signing_key_invalid', 503);
  let bytes;
  try { bytes = Uint8Array.from(atob(value), char => char.charCodeAt(0)); }
  catch { requireSecurity(false, 'signing_key_invalid', 503); }
  requireSecurity(bytes.length >= 32 && bytes.length <= 128, 'signing_key_invalid', 503);
  return bytes;
}

export function bytesToHex(bytes) {
  return [...new Uint8Array(bytes)].map(byte => byte.toString(16).padStart(2, '0')).join('');
}

export async function sha256(value) {
  return bytesToHex(await crypto.subtle.digest('SHA-256', typeof value === 'string' ? utf8(value) : value));
}

export async function hmacSign(secret, message) {
  const key = await crypto.subtle.importKey('raw', base64ToBytes(secret), { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
  return bytesToHex(await crypto.subtle.sign('HMAC', key, utf8(message)));
}

export async function hmacVerify(secret, message, signature) {
  requireSecurity(typeof signature === 'string' && /^[a-f0-9]{64}$/.test(signature), 'signature_invalid', 401);
  const key = await crypto.subtle.importKey('raw', base64ToBytes(secret), { name: 'HMAC', hash: 'SHA-256' }, false, ['verify']);
  const bytes = Uint8Array.from(signature.match(/../g), pair => parseInt(pair, 16));
  return crypto.subtle.verify('HMAC', key, bytes, utf8(message));
}

/** Stable JSON for exact action approvals. Arrays retain order; object keys sort. */
export function canonicalJson(value) {
  let remaining = 20000;
  function visit(item, depth) {
    requireSecurity(depth <= 32 && --remaining >= 0, 'action_too_complex', 400);
    if (item === null || typeof item === 'string' || typeof item === 'boolean') return JSON.stringify(item);
    if (typeof item === 'number') {
      requireSecurity(Number.isFinite(item), 'action_invalid', 400);
      return JSON.stringify(item);
    }
    requireSecurity(typeof item === 'object', 'action_invalid', 400);
    if (Array.isArray(item)) return `[${item.map(child => visit(child, depth + 1)).join(',')}]`;
    requireSecurity(Object.getPrototypeOf(item) === Object.prototype || Object.getPrototypeOf(item) === null,
      'action_invalid', 400);
    return `{${Object.keys(item).sort().map(key => `${JSON.stringify(key)}:${visit(item[key], depth + 1)}`).join(',')}}`;
  }
  return visit(value, 0);
}

export async function readBoundedBody(body, maximumBytes) {
  requireSecurity(body, 'body_required', 400);
  const reader = body.getReader();
  let size = 0;
  const chunks = [];
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      size += value.byteLength;
      if (size > maximumBytes) {
        await reader.cancel();
        requireSecurity(false, 'body_too_large', 413);
      }
      chunks.push(value);
    }
  } finally { reader.releaseLock(); }
  const bytes = new Uint8Array(size);
  let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
  return bytes;
}

export function parseJsonBytes(bytes) {
  try { return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes)); }
  catch { requireSecurity(false, 'json_invalid', 400); }
}

export function deepFreeze(value) {
  if (value && typeof value === 'object') {
    Object.freeze(value);
    for (const child of Object.values(value)) deepFreeze(child);
  }
  return value;
}
