# Measured cost and limits — 2026-09-18

This evidence is a small transport-level test, not production workload qualification.

- Hosted engine: Cloudflare Workers AI `@cf/openai/gpt-oss-120b`; provider-default reasoning setting, 4096 output-token ceiling, concurrency one.
- Latest round: 20 responses / 12 scenarios, $0.007127 measured model charges, five frozen objective passes, one objective failure, six qualitative reviews pending.
- Model cost per currently demonstrated successful scenario, allocating the whole round including failures/pending cases: $0.0014254. If reviews later pass, recompute from their actual disposition; do not pre-credit them.
- Across all rounds: $0.011414 known model charges plus $0.045568 retained unknown-usage reservation = $0.056982 conservative model debit. Failures are retained.
- Confirmed subscription arrangement: user reports $5/month. Current unbilled Worker/DO/storage charges are not available to the current OAuth scope. There is no provider-guaranteed invoice cap.
- Qualification envelope remains $10 total: allocate $5 fixed, retain $2 platform/execution reserve and $3 lifetime inference/tools allowance. Remaining inference/tools allowance is $2.943018. All-in remaining allowance is at most $4.943018 before actual platform usage.
- No cloud build/test jobs, source retrieval operations or artifact storage charges have been measured. No inference retry is free by assumption.

## Workload interpretation

Hours alone do not determine cost. Request frequency, context length, tool loops, retry rate and build duration are necessary inputs. These short tasks cannot establish 8–12 hours/day of coding capability or cost.

For illustration ONLY, at one successfully completed task/minute for 30 days and the current conservative model cost per demonstrated success: eight hours/day implies 14,400 tasks and $20.52576 model cost; twelve hours/day implies 21,600 tasks and $30.78864 model cost. Adding the $5 subscription and $5 platform/execution reserve gives $30.52576 and $40.78864 respectively. These are arithmetic scenarios, not measured production forecasts; larger contexts and cloud execution can cost substantially more. No extra spending is authorized by these estimates, and no budget increase is requested from this tiny sample.

Production remains inactive. No selected model is yet qualified against all required privacy, tool, language, canonical-memory and cloud-workspace gates. The existing $30/month ceiling is not evidence that adequate daily operation fits inside it.
