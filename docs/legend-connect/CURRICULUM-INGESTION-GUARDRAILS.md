# Legend Connect Curriculum Ingestion Guardrails

## Purpose

This note is the permanent maintenance boundary for the Founder **Teach an English semantic family** workflow.

## Non-negotiable architecture

- A curriculum submission represents **one deliberate semantic family**, not an arbitrary bulk corpus.
- Each family must contain **2–100 distinct English examples**. Do not raise or bypass this bound to accommodate a large paste.
- The 100-example limit protects controlled within-family comparison. It is not a UI inconvenience or provider quota.
- Do not split an oversized family into numbered chunks merely to pass validation. Split only when the examples are genuinely different semantic families.
- Do not route structured curriculum through generic Founder training ingestion as a workaround.
- Do not add a second curriculum engine, parser, corpus, structural-learning authority, production gate, or direct database write path.
- All accepted families must continue through the existing `LegendConnectCurriculumBatchSubmission` / `SubmitFounderCurriculumAsync` authority and its existing language-isolation, evidence, maturity, contradiction, Azure-expansion, and production gates.
- Duplicate submissions must remain idempotent/reuse canonical knowledge; duplicates must never manufacture support or confidence.

## Founder UI contract

The Founder curriculum UI must make these facts explicit before submission:

1. **One semantic family per submission.**
2. **2–100 distinct examples per family.**
3. Every example must use the existing controlled syntax: `English text | dimension=value; dimension=value`.
4. Unrelated conversational functions (for example greeting, apology, scheduling, disagreement, closing) belong in separate semantic families even if they are part of the same broader conversation curriculum.
5. A large curriculum should be prepared as multiple deliberate families, never pasted into one family and never silently truncated.

## Failure behavior

- Oversized, undersized, malformed, or semantically invalid family submissions must fail closed before mutation.
- Never accept the first 100 and silently discard the remainder.
- Never partially report an oversized single-family paste as successful.
- The Founder-facing error must state the actual family limit and tell the Founder to separate genuinely distinct semantic families.

## Future bulk-import rule

If a one-action multi-family import is added later, it must be an **orchestration boundary above the existing curriculum authority**, not a replacement for it. It must:

- require explicit family boundaries and semantic categories from the Founder;
- preflight the complete manifest before claiming success;
- enforce 2–100 examples independently for every family;
- delegate each family to the existing curriculum authority;
- preserve the raw import envelope/hash so interrupted work can be resumed idempotently;
- provide a receipt showing accepted/reused/rejected families and examples;
- never infer or merge semantic families automatically;
- never bypass production, maturity, evidence, contradiction, language-isolation, or Azure fallback gates.

## Regression expectations

Tests around this workflow should prove at minimum:

- 1 example is rejected;
- 2 examples are accepted when otherwise valid;
- 100 examples are accepted when otherwise valid;
- 101 examples are rejected with **zero partial mutation**;
- malformed controlled-variation syntax is rejected before mutation;
- duplicate rows do not create duplicate canonical support;
- unrelated semantic functions are not automatically merged;
- any future multi-family import preserves each family's independent 2–100 boundary.

## Historical incident — 2026-08-15

A large conversation-oriented curriculum was prepared as hundreds of examples under a single family. The server correctly rejected it because the existing curriculum authority permits at most 100 examples per semantic family. The mistake was treating the curriculum textarea as a generic bulk-ingestion surface instead of a single controlled semantic-family command.

**Do not solve this incident by increasing the limit or adding an override.** The correct prevention is explicit UI guidance, preflight validation, deliberate family separation, and—only if needed later—a multi-family orchestration layer that preserves the existing authority.
