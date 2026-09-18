import { RuntimeFailure } from './registry.mjs';

function normalizeUsage(usage) {
  const inputTokens = usage?.prompt_tokens ?? usage?.input_tokens;
  const outputTokens = usage?.completion_tokens ?? usage?.output_tokens;
  if (![inputTokens, outputTokens].every(n => Number.isSafeInteger(n) && n >= 0)) return null;
  return { inputTokens, outputTokens };
}

export function parseProviderResponse(raw, model) {
  if (!raw || typeof raw !== 'object' || raw.error || raw.success === false) throw new RuntimeFailure('provider_invalid_response');
  if (raw.model && raw.model !== model.id && raw.model !== model.id.slice(4)) throw new RuntimeFailure('provider_model_mismatch');
  const choice = raw.choices?.[0];
  if (choice?.finish_reason === 'length' || raw.status === 'incomplete') throw new RuntimeFailure('provider_output_truncated');
  if (choice?.finish_reason === 'content_filter') throw new RuntimeFailure('provider_output_rejected');
  let text = choice?.message?.content ?? raw.response;
  let calls = choice?.message?.tool_calls ?? raw.tool_calls ?? [];
  // Accept the documented Responses-compatible variant without returning reasoning items.
  if (Array.isArray(raw.output)) {
    text = raw.output.filter(item => item.type === 'message').flatMap(item => item.content ?? [])
      .filter(item => item.type === 'output_text').map(item => item.text).join('');
    calls = raw.output.filter(item => item.type === 'function_call').map(item => ({ id: item.call_id, function: item }));
  }
  if (text != null && typeof text !== 'string') throw new RuntimeFailure('provider_invalid_response');
  if (!Array.isArray(calls) || calls.length > 16) throw new RuntimeFailure('provider_invalid_tools');
  const seen = new Set();
  const toolCalls = calls.map((call, index) => {
    const fn = call.function ?? call;
    const id = call.id ?? `call_${index}`;
    if (typeof id !== 'string' || id.length > 128 || seen.has(id) || typeof fn.name !== 'string' || !/^[a-zA-Z0-9_-]{1,128}$/.test(fn.name)) throw new RuntimeFailure('provider_invalid_tools');
    seen.add(id);
    let args;
    try { args = typeof fn.arguments === 'string' ? JSON.parse(fn.arguments) : fn.arguments; }
    catch { throw new RuntimeFailure('provider_invalid_tool_arguments'); }
    if (!args || typeof args !== 'object' || Array.isArray(args) || JSON.stringify(args).length > 32768) throw new RuntimeFailure('provider_invalid_tool_arguments');
    return { id, name: fn.name, arguments: args };
  });
  if (!text?.trim() && !toolCalls.length) throw new RuntimeFailure('provider_empty_response');
  if ((text?.length ?? 0) > 262144) throw new RuntimeFailure('provider_output_too_large');
  return { text: text ?? '', toolCalls, usage: normalizeUsage(raw.usage) };
}

export function buildProviderInput(model, messages, task, maxOutputTokens) {
  const input = { messages, stream: false, [model.outputLimitParameter]: maxOutputTokens };
  if (model.outputLimitParameter === 'max_completion_tokens') {
    input.store = false;
    input.reasoning_effort = 'low';
    input.parallel_tool_calls = false;
  }
  if (task.tools?.length) input.tools = task.tools.map(tool => tool.function ? tool : ({ type: 'function', function: { name: tool.name, description: tool.description, parameters: tool.parameters } }));
  return input;
}

export async function generate(env, model, messages, task, maxOutputTokens, signal) {
  if (!env.AI || typeof env.AI.run !== 'function') throw new RuntimeFailure('workers_ai_binding_missing');
  if (signal.aborted) throw new RuntimeFailure('cancelled');
  // Workers AI binding cancellation is not a documented guarantee. The caller
  // stops awaiting on cancellation, retains the full debit, and keeps its lease.
  const raw = await env.AI.run(model.id, buildProviderInput(model, messages, task, maxOutputTokens));
  if (signal.aborted) throw new RuntimeFailure('cancelled');
  return parseProviderResponse(raw, model);
}
