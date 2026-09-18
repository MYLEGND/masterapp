import { MODEL_REGISTRY, REGISTRY_VERSION, RuntimeFailure, estimateCostMicrousd, routeModel, resolveExecutionPolicy } from './registry.mjs';
import { generate } from './adapter.mjs';
import { CircuitBreaker, abortable, requestSignal } from './reliability.mjs';

const health = new CircuitBreaker();
const encoder = new TextEncoder();

async function opaqueId(value) {
  const digest = await crypto.subtle.digest('SHA-256', encoder.encode(value));
  return Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('');
}

function validate(envelope, budget) {
  if (envelope.version !== 'legend-cloudflare.v1' || typeof envelope.requestId !== 'string' || !envelope.scope) throw new RuntimeFailure('invalid_envelope');
  if (!budget?.reserve || !budget?.settle) throw new RuntimeFailure('atomic_budget_unavailable');
  const l = envelope.limits;
  const bounds = { maxOutputTokens: 16384, maxIterations: 8, maxModelCalls: 8, maxToolCalls: 24, maxCostMicrousd: 1000000000 };
  if (!l || Object.entries(bounds).some(([key, max]) => !Number.isSafeInteger(l[key]) || l[key] < (key === 'maxToolCalls' ? 0 : 1) || l[key] > max)) throw new RuntimeFailure('invalid_limits');
  if (!Number.isSafeInteger(l.deadlineUnixMs) || l.deadlineUnixMs > envelope.expiresAt || l.deadlineUnixMs > Date.now() + 120000) throw new RuntimeFailure('invalid_deadline');
  const task = envelope.task;
  if (!task || !Array.isArray(task.messages) || !task.messages.length || task.messages.length > 128 || !Array.isArray(task.tools ?? []) || (task.tools?.length ?? 0) > 64) throw new RuntimeFailure('invalid_task');
  if (task.messages.some(message => !['system', 'user', 'assistant', 'tool'].includes(message.role) || typeof message.content !== 'string')) throw new RuntimeFailure('unsupported_message');
  if (encoder.encode(JSON.stringify(task)).length > 131072) throw new RuntimeFailure('input_too_large');
}

function failure(error) {
  const code = error instanceof RuntimeFailure || error?.name === 'SecurityError' ? error.code : 'runtime_failed';
  return { code, retryable: error instanceof RuntimeFailure ? error.retryable : false };
}

/** Only call after createSecuritySession has authenticated scope and durably claimed replay. */
export async function orchestrate({ envelope, context, env, signal: parentSignal, budget, toolBroker, onEvent,
  registry = MODEL_REGISTRY, circuit = health }) {
  const result = { version: 'legend-cloudflare.v1', requestId: envelope?.requestId ?? null, status: 'failed', text: '', toolResults: [],
    provider: null, registryVersion: REGISTRY_VERSION,
    usage: { inputTokens: 0, outputTokens: 0, costMicrousd: 0, costEvidence: 'provider_usage' }, error: null };
  let deadline;
  try {
    validate(envelope, budget);
    const executionPolicy = resolveExecutionPolicy(env, envelope, context);
    result.executionMode = executionPolicy.mode;
    if (executionPolicy.mode === 'qualification') result.qualificationSuiteSha256 = executionPolicy.suiteSha256;
    deadline = requestSignal(parentSignal, envelope.limits.deadlineUnixMs);
    const { signal } = deadline;
    const emit = event => onEvent ? abortable(() => onEvent(event), signal) : Promise.resolve();
    const messages = structuredClone(envelope.task.messages);
    const tools = envelope.task.tools ?? [];
    const permittedNames = new Set(tools.map(tool => tool.function?.name ?? tool.name));
    const completedCalls = new Set();
    let modelCalls = 0;
    let toolCalls = 0;
    for (let iteration = 0; iteration < envelope.limits.maxIterations; iteration++) {
      if (signal.aborted) throw signal.reason;
      if (++modelCalls > envelope.limits.maxModelCalls) throw new RuntimeFailure('model_call_limit');
      // UTF-8 bytes plus template allowance is deliberately conservative for context routing.
      const inputBound = encoder.encode(JSON.stringify({ messages, tools })).length + 1024;
      const remaining = envelope.limits.maxCostMicrousd - result.usage.costMicrousd;
      const model = routeModel({ task: envelope.task, accountId: envelope.scope.accountId, inputTokens: inputBound, maxOutputTokens: envelope.limits.maxOutputTokens,
        remainingCostMicrousd: remaining, registry, excluded: circuit.unavailable(), executionPolicy });
      const reserved = estimateCostMicrousd(model, model.contextTokens, envelope.limits.maxOutputTokens);
      if (reserved > remaining) throw new RuntimeFailure('request_budget_exhausted');
      result.provider = { name: model.provider, modelId: model.id, hosting: model.hosting };
      await emit({ type: 'progress', stage: 'model', iteration: iteration + 1 });
      const reservationId = await opaqueId(`${envelope.requestId}:model:${modelCalls}`);
      // Do not race an atomic reservation against cancellation: wait for its receipt,
      // then settle it if cancellation arrived before provider dispatch.
      await budget.reserve(context, { requestId: envelope.requestId, reservationId, maxCostMicrousd: reserved, deadlineUnixMs: envelope.limits.deadlineUnixMs });
      let output;
      let dispatched = false;
      let operationError;
      try {
        output = await abortable(() => {
          dispatched = true;
          return generate(env, model, messages, envelope.task, envelope.limits.maxOutputTokens, signal);
        }, signal);
        circuit.success(model.id);
      } catch (error) {
        operationError = error;
        if (!signal.aborted) circuit.failure(model.id);
      }
      const known = !dispatched || Boolean(output?.usage);
      const actual = output?.usage ? estimateCostMicrousd(model, output.usage.inputTokens, output.usage.outputTokens) : 0;
      if (output?.usage) {
        result.usage.inputTokens += output.usage.inputTokens;
        result.usage.outputTokens += output.usage.outputTokens;
      }
      // A provider reporting more than the reservation is an accounting incident,
      // never a reason to silently cap the observed charge or continue generation.
      let receipt;
      try {
        receipt = await budget.settle(context, { reservationId, actualCostMicrousd: actual, usageKnown: known });
      } catch (error) {
        const usage = error?.usage;
        if (Number.isSafeInteger(usage?.costMicrousd) && usage.costMicrousd >= actual
          && ['provider_usage', 'reserved_upper_bound'].includes(usage.costEvidence)) {
          result.usage.costMicrousd += usage.costMicrousd;
          if (usage.costEvidence === 'reserved_upper_bound') result.usage.costEvidence = 'reserved_upper_bound';
        } else {
          // Reservation was already debited. A lost settlement acknowledgment
          // cannot establish a refund, even for a cancelled pre-dispatch call.
          result.usage.costMicrousd += Math.max(reserved, actual);
          result.usage.costEvidence = 'reserved_upper_bound';
        }
        throw error;
      }
      result.usage.costMicrousd += receipt.chargedMicrousd;
      if (!known) result.usage.costEvidence = 'reserved_upper_bound';
      if (operationError) throw operationError;
      if (actual > reserved) throw new RuntimeFailure('provider_usage_exceeded_reservation');
      if (!known) throw new RuntimeFailure('provider_usage_unavailable');
      if (!output.toolCalls.length) {
        result.status = 'completed'; result.text = output.text;
        await emit({ type: 'final', response: result });
        return result;
      }
      if (!toolBroker?.execute) throw new RuntimeFailure('tools_unavailable');
      if (iteration + 1 >= envelope.limits.maxIterations || modelCalls >= envelope.limits.maxModelCalls) throw new RuntimeFailure('iteration_limit');
      // Validate the complete batch before executing any side effect.
      if (toolCalls + output.toolCalls.length > envelope.limits.maxToolCalls) throw new RuntimeFailure('tool_call_limit');
      if (output.toolCalls.some(call => !permittedNames.has(call.name) || completedCalls.has(call.id))) throw new RuntimeFailure('unpermitted_or_duplicate_tool');
      messages.push({ role: 'assistant', content: output.text, tool_calls: output.toolCalls.map(call => ({ id: call.id, type: 'function', function: { name: call.name, arguments: JSON.stringify(call.arguments) } })) });
      for (const call of output.toolCalls) {
        toolCalls++;
        completedCalls.add(call.id);
        const idempotencyKey = await opaqueId(`${envelope.requestId}:tool:${call.id}`);
        await emit({ type: 'progress', stage: 'tool', iteration: iteration + 1 });
        let toolReceipt;
        try {
          // Broker observes this signal itself and settles reserved execution
          // cost before returning/throwing; an outer race would lose that debit.
          toolReceipt = await toolBroker.execute({ context, call, idempotencyKey, signal });
        } catch (error) {
          if (Number.isSafeInteger(error?.usage?.costMicrousd) && error.usage.costMicrousd >= 0) {
            result.usage.costMicrousd += error.usage.costMicrousd;
            if (error.usage.costEvidence === 'reserved_upper_bound') result.usage.costEvidence = 'reserved_upper_bound';
          }
          throw error;
        }
        if (!Number.isSafeInteger(toolReceipt?.usage?.costMicrousd) || toolReceipt.usage.costMicrousd < 0
          || !['provider_usage', 'reserved_upper_bound'].includes(toolReceipt.usage.costEvidence)) throw new RuntimeFailure('tool_usage_unavailable');
        result.usage.costMicrousd += toolReceipt.usage.costMicrousd;
        if (toolReceipt.usage.costEvidence === 'reserved_upper_bound') result.usage.costEvidence = 'reserved_upper_bound';
        if (result.usage.costMicrousd > envelope.limits.maxCostMicrousd) throw new RuntimeFailure('request_budget_exhausted');
        const output = toolReceipt.output;
        const content = JSON.stringify(output);
        if (typeof content !== 'string' || encoder.encode(content).length > 32768) throw new RuntimeFailure('tool_output_too_large');
        result.toolResults.push({ id: call.id, name: call.name, output });
        messages.push({ role: 'tool', tool_call_id: call.id, content });
      }
    }
    throw new RuntimeFailure('iteration_limit');
  } catch (error) {
    result.status = 'failed'; result.text = ''; result.error = failure(error);
    return result;
  } finally { deadline?.dispose(); }
}

/** Backpressure-aware events; no token deltas are replayed after a disconnect. */
export function createEventStream(run, parentSignal) {
  const cancellation = new AbortController();
  const signal = parentSignal ? AbortSignal.any([parentSignal, cancellation.signal]) : cancellation.signal;
  const channel = new TransformStream();
  const writer = channel.writable.getWriter();
  // Reader cancellation rejects writer.closed even when no write is pending.
  // Propagate it immediately while inference is still awaiting its provider.
  writer.closed.catch(() => cancellation.abort());
  const write = event => writer.write(encoder.encode(`data: ${JSON.stringify(event)}\n\n`));
  const completion = (async () => {
    try {
      const response = await run({ signal, onEvent: event => event.type === 'final' ? Promise.resolve() : write(event) });
      await write({ type: response.status === 'completed' ? 'final' : 'error', response });
      await writer.close();
    } catch (error) { cancellation.abort(); await writer.abort(error).catch(() => {}); }
  })();
  return { readable: channel.readable, completion, cancel: () => cancellation.abort() };
}
