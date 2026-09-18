import { RuntimeFailure } from './registry.mjs';

// This is an isolate-local health hint, never authorization, budget or global concurrency state.
export class CircuitBreaker {
  constructor({ threshold = 3, cooldownMs = 30000, now = Date.now } = {}) {
    this.threshold = threshold; this.cooldownMs = cooldownMs; this.now = now; this.failures = new Map();
  }
  unavailable() {
    return [...this.failures.entries()].filter(([, state]) => state.count >= this.threshold && state.until > this.now()).map(([id]) => id);
  }
  success(id) { this.failures.delete(id); }
  failure(id) {
    const previous = this.failures.get(id);
    const count = previous?.until > this.now() ? previous.count + 1 : 1;
    this.failures.set(id, { count, until: this.now() + this.cooldownMs });
  }
}

export function requestSignal(parent, deadlineUnixMs) {
  const controller = new AbortController();
  const cancel = () => controller.abort(new RuntimeFailure('cancelled'));
  if (parent?.aborted) cancel();
  else parent?.addEventListener('abort', cancel, { once: true });
  const remaining = deadlineUnixMs - Date.now();
  let timer;
  if (remaining <= 0) controller.abort(new RuntimeFailure('deadline_exceeded'));
  else timer = setTimeout(() => controller.abort(new RuntimeFailure('deadline_exceeded')), remaining);
  return { signal: controller.signal, dispose() { clearTimeout(timer); parent?.removeEventListener('abort', cancel); } };
}

export async function abortable(operation, signal) {
  if (signal.aborted) throw signal.reason ?? new RuntimeFailure('cancelled');
  let listener;
  try {
    return await Promise.race([operation(), new Promise((_, reject) => {
      listener = () => reject(signal.reason ?? new RuntimeFailure('cancelled'));
      signal.addEventListener('abort', listener, { once: true });
      if (signal.aborted) listener();
    })]);
  } finally { signal.removeEventListener('abort', listener); }
}
