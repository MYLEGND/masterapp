# Five-model candidate verification

Checked 2026-09-19 00:21 UTC (2026-09-18 Arizona). This is public documentation
and source inspection, with no inference calls, provisioning, configuration
changes, or deployment. Source inspected: integration revision
`feea9b81324312167ca9fdb310cbd7c0972c4b29`, registry `2026-09-18.3`.

## Exact identities, hosting, capabilities, and published prices

Prices below are USD per million tokens, before any account-specific allowance.
Every exact ID below is explicitly labelled Cloudflare-hosted on its model page.
All five document text generation, reasoning, function calling, and streaming
usage. These are provider capabilities, not proof that the application has
qualified them.

| Exact Workers AI ID | Context tokens | Input / output | Cached input | Additional documented capability | Primary evidence |
| --- | ---: | ---: | ---: | --- | --- |
| `@cf/qwen/qwen3-30b-a3b-fp8` | 32,768 | 0.0509 / 0.335 | Not listed | Batch | [Qwen model page](https://developers.cloudflare.com/workers-ai/models/qwen3-30b-a3b-fp8/) |
| `@cf/openai/gpt-oss-120b` | 128,000 | 0.35 / 0.75 | Not listed | Batch | [GPT-OSS model page](https://developers.cloudflare.com/workers-ai/models/gpt-oss-120b/) |
| `@cf/zai-org/glm-5.3-flash` | 1,310,720 | 0.15 / 0.50 | 0.03 | Vision | [GLM Flash model page](https://developers.cloudflare.com/workers-ai/models/glm-5.3-flash/) |
| `@cf/zai-org/glm-5.3` | 1,310,720 | 1.40 / 4.40 | 0.26 | No vision claim used here | [GLM model page](https://developers.cloudflare.com/workers-ai/models/glm-5.3/) |
| `@cf/deepseek-ai/deepseek-v4-pro-0813` | 1,048,576 | 1.32 / 3.96 | 0.044 | No vision claim used here | [DeepSeek model page](https://developers.cloudflare.com/workers-ai/models/deepseek-v4-pro-0813/) |

The platform price table rounds Qwen input pricing to **$0.051**, while its model
page gives **$0.0509**. The registry currently uses the latter. For a conservative
reservation, use the higher published value until billing precision is verified.
The platform table otherwise agrees with the listed input/output/cache rates.
GLM, GLM Flash, and DeepSeek Pro require Workers Paid or prepaid AI Gateway
credits; a catalog entry alone does not establish billing eligibility.
[Workers AI pricing](https://developers.cloudflare.com/workers-ai/platform/pricing/)

Do not substitute a similarly named catalog product. In particular,
`deepseek/deepseek-v4-pro` is a separate third-party entry described as served on
Fireworks, with a different context limit. It is not the requested
`@cf/deepseek-ai/deepseek-v4-pro-0813`.
[Separate third-party entry](https://developers.cloudflare.com/ai/models/deepseek/deepseek-v4-pro/)

## Wire compatibility and limits of the evidence

The current Qwen and GPT model pages show `max_tokens`; neither exposes
`reasoning_effort` in the inspected input parameters. The current adapter uses
that output cap and records `provider_default` reasoning for these two. Their
reasoning capability does not establish a supported per-request effort knob.
The GLM/Flash/DeepSeek schemas expose `max_completion_tokens` and
`reasoning_effort` with low/medium/high values. Their adapter defaults to high,
sets `store:false`, and disables parallel function calls. This agrees with the
currently documented chat-shaped inputs on the model pages linked above.

The retained authenticated schema snapshot at
`Legend-Cloudflare/tests/runtime/fixtures/account-schema-snapshot.json` records
read-only `wrangler@4.135.0` schema inspection at 2026-09-18T21:20:59Z, including
the account hash and schema hashes. It explicitly records no inference. This
review did not refresh that account evidence. Public documentation, earlier
schema visibility, successful inference, tool compatibility, and independent
quality acceptance are separate claims.

Safe schema refresh, if separately authorized, uses normal encrypted Wrangler
OAuth without extracting its token:

```sh
npx --yes wrangler@4.135.0 ai models schema '@cf/zai-org/glm-5.3'
```

Select the existing account through the normal Wrangler account configuration.
This reads `GET /accounts/{account_id}/ai/models/schema?model={model}`; it is not an
inference canary. Do not infer account access from this document or silently
replace a rejected model with another provider/model.

## Current application gaps and precise proposed changes

No source changes are made by this evidence file.

1. **Identity/context metadata:** all five existing registry IDs, hosting labels,
   context sizes, and noncached prices match the model pages. Adjust only Qwen's
   conservative input reservation rate to 0.051 to cover the published rounding
   discrepancy; retain the original pricing evidence. Cached rates may be stored
   as metadata, but do not grant a cache discount without verified cached-token
   usage. The current uncached calculation is conservative.
2. **Five-model policy:** `MANUAL_TEST_MODEL`,
   `resolveFounderManualTestPolicy`, and the manual branch of `routeModel` are
   explicitly pinned to GPT-OSS-120B. Merely listing five registry rows does not
   make them available. A future reviewed policy must validate an operator-owned
   set drawn from exactly these five IDs, preserve the existing Founder/account/
   tenant/service-key/expiry checks, and keep the same account-named durable
   lifetime ledger. Model, suite, key, or deployment changes must not reset spend.
   Production qualification flags must remain independent of manual permission.
3. **Routing quality:** current role labels (`efficient`, `general`, `coding`,
   `architecture`, `reasoning`) are routing hints, not measured suitability.
   Selection currently prefers role match and then price. Five-model routing
   needs observed quality/capability eligibility before cost tie-breaking;
   cheaper fallback must not silently reduce an explicitly requested capability
   or reasoning setting. Record the actual selected model and settings.
4. **Wire adapters:** no justified chat-format switch was found in this read-only
   review. Preserve current per-model output caps and high/default reasoning
   settings. Refresh schemas and run bounded account canaries before admitting
   each candidate. Verify actual response identity, usage, terminal truncation,
   tool arguments, and tool-result continuation. Keep unknown/nonterminal usage
   charged at its full reservation; do not refund merely because a partial usage
   object exists. Do not invent generic model aliases to make a canary pass.
5. **Capability honesty:** the runtime validates message content as strings and
   therefore does not currently carry image inputs despite GLM Flash's catalog
   vision capability. Separate catalog capabilities from application-supported
   capabilities, or qualify an explicit multimodal protocol before routing image
   work. The adapter currently calls providers with `stream:false`; its outer
   event stream supplies progress/final events, not provider token streaming.
   Batch support is likewise catalog evidence, not an implemented application
   route. None of these labels establishes language or code quality acceptance.

## Budget feasibility without spending

The current orchestrator reserves the full model context at the uncached input
rate plus the requested output cap. These are conservative reservation amounts,
not estimated charges for a short prompt. For 4,096 output tokens, executing the
existing pure cost function locally produced:

| Candidate | Current reservation, USD | Fits a $0.15 request cap? |
| --- | ---: | --- |
| Qwen | 0.003041 | Yes |
| GPT-OSS-120B | 0.047872 | Yes |
| GLM Flash | 0.198656 | No |
| GLM | 1.853031 | No |
| DeepSeek Pro | 1.400341 | No |

Qwen becomes $0.003044 with the conservative 0.051 input rate. The other figures
do not change. Enabling the three larger-context candidates under a $0.15 cap
will currently fail before inference. Do not disguise that failure by lowering
reasoning/output quality or pretending the context reservation is an actual
invoice. Any future request-cap adjustment needs an explicit share of the
existing allowance, or a separately verified tighter input-cost bound. This
document authorizes neither.

The existing budget file records $10 total qualification authorization including
the user-confirmed $5 subscription, a $3 shared model/tool cap, $0.056982
conservative model debit, and $2.943018 remaining under that model/tool cap.
Those are the source ledger snapshot, **not a verified current bill**. Prior
unknown reservations remain charged. OAuth lacks billing-read visibility, so
unbilled platform charges and actual remaining all-in allowance are unverified.
Worker requests/CPU and durable storage can incur separate charges; the
inference ledger is not a provider invoice cap.
[Workers platform pricing](https://developers.cloudflare.com/workers/platform/pricing/)

The running GPT-only Founder baseline and its live configuration were not
altered. This evidence supports planning a bounded five-model qualification;
it does not claim that all five are live, account-accessible, or accepted.
