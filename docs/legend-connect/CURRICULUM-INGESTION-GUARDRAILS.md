# Legend Connect Curriculum Ingestion and Authoring Guardrails

## Purpose

This is the permanent maintenance and authoring boundary for Founder-controlled **English semantic curriculum**. The objective is not to memorize canned sentences. The objective is to accumulate reusable, evidence-backed linguistic knowledge that can support sophisticated conversation, structural understanding, cross-language learning, contradiction detection, and eventually safe internal composition.

Curriculum quality therefore matters as much as curriculum quantity. Author examples to expose meaning, realization, contrast, composition, discourse function, and context—not merely labels.

## Non-negotiable architecture

- A manifest may contain **multiple explicitly declared semantic families**.
- Every family remains an independent controlled-learning unit with **2–100 distinct English examples**.
- The 100-example limit protects controlled within-family comparison. It is not a UI inconvenience or provider quota.
- Never silently split, merge, truncate, or infer semantic families.
- Never split one coherent family into numbered chunks merely to bypass the limit. Split only on a genuine semantic distinction.
- Never route structured curriculum through generic Founder training ingestion as a workaround.
- Never add a second curriculum engine, parser, corpus, structural-learning authority, production gate, provider router, or direct database write path.
- Every accepted family continues through the existing `LegendConnectCurriculumBatchSubmission` / `SubmitFounderCurriculumAsync` authority and its existing language isolation, corpus, Azure expansion, structural evidence, contradiction, maturity, correction, and production gates.
- Duplicate submissions remain idempotent. Repetition must never manufacture support, independence, maturity, or confidence.
- Provider-derived observations never become Founder truth merely because a provider repeats them.

## The authoring standard: teach meaning and realization

A strong curriculum example should encode two complementary kinds of evidence when they are known.

### Semantic and discourse dimensions

These describe what the utterance means or does even when the value is not literally present as a surface substring. Useful dimensions include `function`, `intent`, `polarity`, `modality`, `tense`, `aspect`, `mood`, `certainty`, `register`, `tone`, `discourse_role`, `reference_time`, and `condition_type`.

Use semantic dimensions only when the Founder actually intends to assert that distinction. Do not add decorative labels that are not controlled evidence.

### Surface-realized semantic components

When a semantic value is literally realized in the English utterance, include the exact surface value so the existing structural-learning authority can bind meaning to observed spans rather than merely store an abstract label.

Useful dimensions include `agent`, `predicate`, `object`, `recipient`, `location`, `time_expression`, `condition_marker`, `negation_marker`, `modal_marker`, `connector`, `phrase`, and other exact multi-word constituents when their semantic identity is deliberately controlled.

Values intended as surface components must match the utterance text exactly after normal normalization. Multi-word components are valuable; do not reduce phrases and idioms to isolated words when the phrase carries the meaning.

## Controlled contrast is the core learning instrument

Do not create a family as a random bag of sentences. Design examples so comparisons expose deliberate distinctions: polarity, tense, aspect, modality, person, condition, pragmatic force, register, discourse function, and other purposeful contrasts.

Across the full curriculum, vary vocabulary and surface form so a pattern cannot mature only because the same sentence frame was repeated with trivial substitutions.

## Conversation curriculum must model interaction, not isolated textbook prose

Prioritize language people actually use to coordinate meaning with one another. Cover openings and re-entry; questions and answers; acknowledgments and backchannels; clarification, repetition, paraphrase, confirmation, correction and misunderstanding repair; requests, commands, permissions, offers, invitations, suggestions, advice, commitments, promises and refusals; agreement, disagreement, qualification, concession and negotiation; temporal coordination and rescheduling; causality, purpose, contrast, alternatives, conditions and consequences; modality and certainty; reference and coreference; multi-clause and embedded constructions; reported speech; phrasal verbs, idioms, collocations and lexical senses; politeness, formality, warmth, urgency, softening and indirectness; topic continuation and shift; interruption recovery, summaries, recaps and closings; fragments, contractions, discourse markers and self-correction when their meaning is controlled.

Do not create universal claims from slang, dialect, sarcasm, humor, figurative language, or culturally specific pragmatics unless the curriculum explicitly scopes and contrasts those phenomena.

## Sophisticated family design

A semantic family should have one coherent learning objective. Within it, deliberately vary dimensions that help isolate that objective. Good families may contain many examples and several controlled dimensions, but every dimension must earn its place.

For complex constructions, teach prerequisites across independent families before expecting composition. A conditional conversation, for example, may require independent evidence for participants, predicates, modality, tense/aspect, condition markers, clause relationships, and pragmatic function. Do not expect one giant sentence family to teach the entire language at once.

Favor breadth of independent evidence: different lexical material, participants, predicates, objects, sentence lengths, conversational contexts, multiple surface realizations of the same semantic distinction, and multiple semantic distinctions realized through comparable structures.

## Canonical manifest syntax

```text
@@family conversation.request.action | Action request
Could you send the document today? | function=request; intent=request_action; mood=interrogative; register=polite; agent=you; predicate=send; object=the document; time_expression=today; modal_marker=Could
Please send the document today. | function=request; intent=request_action; mood=imperative; register=polite; agent=you; predicate=send; object=the document; time_expression=today
Send the document today. | function=request; intent=request_action; mood=imperative; register=direct; agent=you; predicate=send; object=the document; time_expression=today
@@end

@@family conversation.clarification.repair | Clarification and repair
What do you mean by that? | function=clarification_request; intent=clarify_meaning; mood=interrogative; agent=you
I mean the meeting tomorrow. | function=clarification_response; intent=clarify_meaning; mood=declarative; agent=I; object=the meeting; time_expression=tomorrow
Do you mean the meeting tomorrow? | function=confirmation_request; intent=confirm_understanding; mood=interrogative; agent=you; object=the meeting; time_expression=tomorrow
Yes, I mean the meeting tomorrow. | function=confirmation_response; intent=confirm_understanding; polarity=affirmative; mood=declarative; agent=I; object=the meeting; time_expression=tomorrow
No, I mean the call tomorrow. | function=correction; intent=correct_misunderstanding; polarity=negative; mood=declarative; agent=I; object=the call; time_expression=tomorrow
@@end

@@family conversation.condition.commitment | Conditional commitment
If you call, I will answer. | function=commitment; intent=conditional_commitment; condition_type=real; condition_marker=If; agent=I; predicate=answer; modal_marker=will
If you send the address, I will come. | function=commitment; intent=conditional_commitment; condition_type=real; condition_marker=If; agent=I; predicate=come; object=the address; modal_marker=will
If you called, I would answer. | function=hypothetical_commitment; intent=conditional_commitment; condition_type=hypothetical; condition_marker=If; agent=I; predicate=answer; modal_marker=would
@@end
```

These examples demonstrate the standard; they are not a closed ontology. Add a dimension when it represents a deliberate semantic distinction or an exact surface component the existing authority can learn from. Do not invent redundant synonyms for dimensions casually; stable dimension naming improves cross-family evidence accumulation.

## Authoring checklist before submission

1. The family has one coherent semantic purpose.
2. It contains 2–100 distinct examples.
3. Every annotation is deliberate evidence, not decorative metadata.
4. Surface-realized values use exact utterance wording where appropriate.
5. Important multi-word phrases remain intact.
6. Examples contain controlled contrasts that isolate useful distinctions.
7. Vocabulary and contexts vary enough to support generalization rather than memorization.
8. Conversation families represent interactional functions and responses, not only isolated statements.
9. Complex grammar is decomposed across reusable prerequisite families as well as exercised in realistic multi-clause examples.
10. Register, tone, discourse role, or cultural scope is labeled only when intentionally controlled.
11. The family does not assert a target-language rule; target languages learn from their own evidence.
12. The manifest does not depend on provider output becoming trusted automatically.

## Validation and failure behavior

- The entire multi-family manifest is preflighted before curriculum mutation is claimed successful.
- Oversized, undersized, malformed, duplicate-conflicting, or semantically invalid families fail closed.
- Never accept the first 100 and silently discard the remainder.
- Never silently repair malformed controlled syntax into a different Founder assertion.
- Never infer missing family boundaries.
- Existing family keys must not be repurposed for an unrelated semantic category.
- A failed manifest must not partially report unrelated families as successfully committed.

## Runtime and evidence boundaries

Curriculum is evidence, not an instruction to bypass production safety.

- English Founder evidence remains language-specific.
- Azure-expanded target examples remain provider-derived observations until independently supported or Founder-verified through existing authorities.
- Structural maturity requires independent support; duplicate or provider-only repetition does not count as independent Founder evidence.
- Contradictions and corrections remain durable evidence and must flow through the existing correction/evaluation path.
- Exact trusted memory remains distinct from structural composition.
- Structural composition remains bounded and gated. Current runtime source understanding and shadow composition intentionally enforce component/relationship limits; do not raise them merely to make a test pass. Any future expansion must preserve bounded evaluation, ambiguity rejection, evidence provenance, and Azure fallback.
- Unknown, ambiguous, unsupported, or over-complex unseen input must fail closed to the existing fallback path rather than guess.

## Regression expectations

Tests should prove the 2–100 family boundary; zero partial mutation on invalid manifests; explicit multi-family orchestration; category-conflict rejection; malformed syntax rejection; duplicate idempotency; no automatic family merging; distinction between abstract semantics and surface-realized anchors; reusable multi-word components; lexical-sense separation; cross-family evidence accumulation without automatic production opening; provider-only evidence cannot mature trusted patterns; correction history and maturity recalculation; held-out composition only with independently supported semantics and relationships; ambiguity and missing evidence fail closed; and Azure fallback/production gates remain intact.


## Bulk execution boundary

Large valid manifests must never execute complete curriculum learning synchronously inside the Founder HTTP request.

The permanent separation is:

1. parse and preflight the complete manifest;
2. durably accept the exact Founder-authored manifest and its progress state;
3. return a truthful **Accepted / Processing** receipt promptly;
4. process one bounded family at a time through the existing `LegendConnectCurriculumService`;
5. retain resumable progress after every completed family;
6. let the existing corpus/candidate/Azure-expansion/evidence/maturity authorities perform their normal work.

The orchestration queue is not a curriculum engine and must never contain language rules, semantic inference, corpus persistence logic, provider routing, evidence evaluation, or production gating.

A process recycle, cancellation, or transient failure must resume from durable progress. Reprocessing must remain safe because canonical examples, target candidates, semantic anchors, structural evidence, and relationships retain their existing idempotency/uniqueness boundaries.

Never solve bulk scale by:
- asking the Founder to manually submit valid families one at a time;
- weakening the curriculum;
- raising semantic-family limits;
- removing structural analysis;
- skipping target-language expansion;
- holding one web request/transaction open for full-manifest intelligence processing;
- or adding a second learning authority.

## Historical incident — 2026-08-15

A large conversation-oriented curriculum was originally prepared as hundreds of examples under a single family. The server correctly rejected it because a semantic family permits at most 100 examples. The deeper lesson is not merely to split a paste: curriculum must declare genuine semantic boundaries and deliberately expose reusable contrasts.

The supported Founder surface is now a multi-family manifest above the existing curriculum authority. It may orchestrate many explicitly declared families in one action, but it does not merge them, infer them, bypass their independent 2–100 limits, or replace any existing learning authority.

## Permanent principle

**Do not train LEGEND to remember what a sentence looked like. Train it to accumulate evidence for what people mean, how that meaning is realized, how meanings combine, how conversation changes meaning, and when the evidence is not strong enough to know.**
