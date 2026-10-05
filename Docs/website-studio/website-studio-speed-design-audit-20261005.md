# Website Studio Speed + Premium Design Audit — 2026-10-05

## Scope and release state

Working branch: `repair/canonical-mobile-public-nav-shell-20261005`

This audit traces the canonical website workflow end to end:

`GPT/browser workspace → management bootstrap → canonical source → canvas/source edits → shared shell/components/forms/media → draft persistence → quality → publish → immutable compiler → public runtime`.

This document is planning evidence only. No validation, PR, merge, release, or deployment is authorized by this audit.

## Executive finding

The current system has the right core authority — `WebsiteContentDocument v3` — but too much of the workflow still moves, serializes, reparses, renders, and explains the **entire website** for operations that affect only one node or section.

The biggest build-time problem is not model creativity. It is workflow granularity.

A premium 4–6 page business site is currently built from low-level atoms, while GPT repeatedly receives oversized contracts and full-site source/state. To reach a repeatable 10–15 minute design cycle, the system needs:

1. compact task-scoped GPT context,
2. atomic node/section/batch mutations against the same v3 authority,
3. a code-owned premium section recipe catalog,
4. richer semantic design tokens,
5. a transient whole-site design-plan command that resolves into v3,
6. targeted quality inspection and targeted DOM updates.

None of those require a second website source, database, renderer, or backend authority.

---

## Measured architectural pressure points

### 1. Shared CMS/editor runtime is monolithic

- `SHARED/WebsitePlatform/legend-public-cms.js`: ~420k characters / ~7,416 lines.
- `Infrastructure/WebsiteEditing/WebsitePlatformController.cs`: ~121k characters / ~2,541 lines.
- `WebsiteContentSanitizer.cs`: ~58k characters / ~1,188 lines.
- `WebsiteStudioAgentContract.cs`: ~40k characters / ~415 lines.
- `WebsiteSiteSource.cs`: ~37k characters / ~761 lines.

One canonical runtime does **not** require one enormous source file. The current shape increases inspection cost, regression risk, and the amount of source GPT/humans must reason over for small changes.

### 2. GPT contract is oversized and duplicates scoped inventory

`WebsiteStudioAgentContract.ForScope` embeds the scoped actions + signal catalog inside `promptTemplate` and also returns the same data separately as `availableActions` and `signalCatalog`.

The browser then injects the full prompt into the GPT workspace.

Result: rules that rarely change and inventories needed only for some tasks are paid for repeatedly in context.

### 3. Management bootstrap eagerly returns unrelated data

`GET manage` currently returns the complete document plus business facts, collection catalog/data, CTA catalog, store state, media usage, import report, named drafts, history, signal catalog, full agent contract, capabilities, schedule, and readiness.

Additional inefficiencies:
- repeated `CanPublishAsync` calls for several flags,
- history query has no explicit result cap,
- media bytes and media count use separate queries.

The editor should bootstrap with only current-page/site essentials and lazy-load advanced domains.

### 4. Normal autosave sends the complete website

`markDirty()` schedules save after ~900 ms.

`save(false)` serializes the entire `documentState` and posts it to `/manage`.

Server save then:
- reads the persisted baseline,
- sanitizes the proposed document,
- applies system templates,
- sanitizes the baseline,
- rebuilds CTA authority,
- serializes the candidate through `WebsiteSiteSource`,
- parses/protects it again against the baseline,
- validates media ownership,
- serializes the complete document again for persistence.

A text change, drag, color change, or spacing adjustment therefore pays full-document costs.

### 5. Selected Source expands a one-node edit into multiple whole-site transactions

Current Selected Source flow is the largest token/network inefficiency:

1. save any dirty canvas state,
2. GET complete Master Source,
3. parse complete Source in browser,
4. extract one selected node,
5. after edit, clone/rebuild complete Source with replacement node,
6. POST complete Source to `manage/source/validate`,
7. server parses/protects complete document,
8. server returns complete normalized source **and** complete proposed document **and** source map,
9. browser posts complete proposed document to `/manage`,
10. server repeats the canonical full-document protection/save path.

Conflict recovery can repeat the full Source fetch/validation up to three times.

A selected-node edit should never require shipping the entire site twice.

### 6. Undo history stores up to 80 complete site snapshots

`checkpoint()` uses `JSON.stringify(documentState)` and stores up to 80 snapshots.

Large sites therefore duplicate the entire graph many times in browser memory and must parse/normalize the entire document on undo.

### 7. Structural edits rebuild the complete current page DOM

`renderCanonicalCompositionPage()` clears `main`, rebuilds every node, then reapplies presentation across the page.

Adding one image/block or duplicating one element can therefore rebuild the whole page.

### 8. Shared-presentation synchronization scans globally on dirty edits

`markDirty()` invokes canonical shared-presentation synchronization.

The correctness objective is valid, but a global traversal on frequent mutations should be replaced by an indexed sync group keyed by canonical shared source.

### 9. Master/Selected Source highlighting is not virtualized

Source highlighting recreates a span per line on every source input.

Master Source is read-only and does not need per-keystroke highlighting; Selected Source should be small enough that the whole-site cost disappears once the node API is introduced.

### 10. Legacy materialization is serial page-by-page browser work

`materializeCanonicalSite()` loops every route and awaits `requestMaterializedPage(route)` sequentially before one final save.

This makes legacy conversion a design-time tax and scales directly with page count.

### 11. Publish always performs a full external compiler transaction

Business publishing launches a fresh Node process, serializes the entire website + business facts + collections into stdin, compiles the entire site, and accepts up to 32M output characters.

This is appropriate as a final immutable publication boundary, but it should stay completely outside normal iterative design work.

### 12. Publishing immediately after edits requires two calls

`save(true)` first saves the dirty draft and then recursively publishes.

A final atomic save-and-publish command could avoid redundant client/server round trips while preserving revision and publication validation.

---

## Premium-design deficiencies

### 13. GPT is designing from atoms instead of semantic sections

Native Add currently provides:
- Text
- Button/link
- Image
- Video
- Inquiry form
- Interactive experience
- Code/embed
- Section

There is no canonical premium recipe system for:
- hero / split hero / cinematic hero,
- trust/logo band,
- proof/stat band,
- service grid,
- feature showcase,
- comparison,
- process,
- testimonial/proof,
- case study,
- pricing/offer,
- FAQ,
- gallery,
- team,
- CTA band,
- contact band,
- conversion close.

GPT has to reconstruct professional layout grammar from low-level nodes every time.

### 14. New page creation is intentionally bare

A newly created page contains one section and one H1.

That is technically clean, but inefficient for production design. A Services, About, Contact, Landing, FAQ, Team, Offer, or Service Detail page should begin from a canonical semantic recipe arrangement.

### 15. Theme authority is too shallow

`WebsiteDesignTheme` currently provides essentially:
- palette colors,
- one font family,
- one base font size,
- border radius.

Missing first-class semantic design tokens include:
- display/H1/H2/H3/body/small type scale,
- font weights,
- line height/tracking,
- spacing scale,
- section vertical rhythm,
- page/content max widths,
- mobile/desktop gutters,
- surface/elevation system,
- border strength,
- card system,
- button sizes/shape,
- form/input system,
- hero sizing,
- nav/footer density,
- responsive density,
- motion duration/easing scale.

Without these, premium styling becomes repeated per-node fields, increasing tokens and visual drift.

### 16. Reusable components exist, but no code-owned recipe catalog exists

Site-owned reusable components are correct and useful.

What is missing is a **server/code-owned recipe catalog** that constructs v3 nodes. Recipes must not become another persistence source: they are constructors that clone fresh stable IDs into `WebsiteContentDocument v3`.

### 17. No transient whole-site design plan command exists

There is no high-level command for GPT to state:

- design system,
- page list,
- page roles,
- section recipe sequence,
- content slots,
- media selections,
- CTA selections,
- data bindings,
- responsive intent.

A transient `WebsiteDesignPlan` can resolve into the same canonical v3 document in one protected mutation transaction.

### 18. No compact site summary exists for GPT

GPT choices are effectively:
- inspect full Master Source / DOM,
- or inspect one selected node.

The missing middle layer is a compact site summary containing:
- brand/business facts,
- pages,
- page section outline,
- theme tokens,
- CTA/action keys,
- media metadata,
- protected component identities,
- quality warnings.

That should be the default agent context.

### 19. Media metadata is insufficient for fast visual composition

Owner-scoped media is canonical, but GPT lacks a compact design-facing index such as:
- semantic role/tag,
- orientation/aspect ratio,
- focal position,
- alt description,
- logo/hero/gallery classification,
- usage locations.

This forces more browsing/inspection than necessary.

### 20. Quality inspector verifies integrity more than design quality

Server quality currently covers core document integrity, metadata, alt text, destinations, form authority, reusable refs, and collection bindings.

Live canvas quality covers H1 count, duplicate IDs, alt, links, labels, and basic horizontal overflow.

Missing premium checks:
- contrast,
- readable minimum type,
- touch target size,
- line-length limits,
- heading hierarchy,
- section rhythm,
- excessive override/style drift,
- empty/placeholder sections,
- hero CTA visibility,
- mobile/tablet/desktop clipping,
- image crop/aspect resilience,
- mobile nav usability,
- footer/header presence,
- oversized media,
- excessive animation,
- repeated/competing CTAs,
- visual density and consistency.

### 21. Current branch exposes one source/test contradiction

`addCompositionBlock()` currently assigns `legend-cms-image` as the default class for a newly added image, while the regression contract explicitly forbids inventing reserved `legend-cms-*` runtime classes.

This should be corrected before eventual validation.

---

## Canonical cleanup opportunities

### 22. Split source modules while preserving one shipped runtime

Keep one public runtime artifact if desired, but source-split into:
- document model/normalization,
- renderer,
- responsive/theme,
- editor state/mutations,
- source/agent transport,
- media,
- quality,
- panels,
- public runtime.

Bundle those into the same `legend-public-cms.js` artifact. This is maintainability cleanup, not a second runtime.

### 23. Split the controller behind one route authority

Keep one website API authority, but extract internal services:
- bootstrap/query,
- mutation/protection,
- selected-source,
- media,
- publishing,
- signals,
- collaboration.

No duplicate routes or persistence.

### 24. Move starter layouts onto canonical recipes

Static starter HTML currently has its own handcrafted layout helpers such as hero/section/cards.

The long-term clean architecture is:
`canonical starter v3 document → shared renderer/compiler`,
not a separate HTML design grammar that is later materialized into v3.

### 25. Bound and lazy-load history/catalogs

- cap initial history,
- page older revisions,
- lazy-load collections and data,
- lazy-load signal catalog,
- lazy-load media,
- resolve permissions once,
- combine usage aggregates.

### 26. Keep compiler optimization lower priority until measured

A warm Node worker/pool may reduce final publish latency, but iterative build speed will improve much more from node/batch mutation and recipes. Do not optimize the compiler before measuring after P1/P2.

### 27. Split the large website test file by behavior domain

Keep tests executing the real shipped editor, but split into:
- editor core,
- source,
- responsive,
- navigation,
- forms,
- media,
- reusable components,
- quality,
- publish contracts.

This shortens developer/reviewer context without weakening coverage.

---

## Target architecture for 10–15 minute premium builds

### Canonical rule

`WebsiteContentDocument v3` remains the only writable website source.

New APIs/recipes/plans are commands and projections over v3 — never alternate state.

### A. Compact GPT bootstrap

Add a task-focused `site.summary` projection:
- revision + site key,
- business facts summary,
- theme token summary,
- page outline,
- protected component list,
- action keys,
- media index summary,
- quality warnings,
- capability hashes.

Do not return the full document unless explicitly requested.

### B. Selected-node atomic mutation

Add:
- `GET manage/source/node?id=...`
- `POST/PATCH manage/source/node`

GET returns:
- projected selected node,
- revision,
- node fingerprint,
- location/scope.

PATCH sends:
- expected revision,
- expected node fingerprint,
- replacement or bounded operations.

Server:
1. loads authoritative v3 baseline,
2. resolves node by stable ID,
3. applies authorable change,
4. restores protected semantics,
5. validates the affected authority,
6. persists once,
7. returns changed node + new revision.

Do not serialize/POST/return the complete Master Source for a selected-node edit.

### C. Canonical batch mutation endpoint

Introduce one mutation transaction over v3 with typed operations such as:
- set text/content,
- set style fields,
- set breakpoint style,
- set layout,
- insert recipe,
- insert/remove/move node,
- set media asset,
- set CTA selection,
- set page metadata,
- create/duplicate page,
- apply theme tokens.

All operations go through one protection/sanitize authority and commit once.

### D. Code-owned premium Section Recipe Catalog

Recipes are immutable code definitions/constructors, not stored website state.

Recommended initial premium families:
- cinematic hero,
- split editorial hero,
- service/feature grid,
- proof/stat strip,
- trust band,
- feature split,
- process timeline,
- comparison,
- testimonial/proof,
- offer/pricing,
- FAQ,
- gallery,
- team,
- contact/inquiry,
- closing CTA.

Each recipe should provide:
- semantic slot schema,
- responsive defaults,
- theme-token references,
- optional media slot,
- optional approved CTA slot,
- deterministic composition shape.

### E. Rich semantic design tokens

Extend `WebsiteDesignTheme` rather than spreading hard-coded styles.

Add bounded semantic tokens for:
- typography scale,
- spacing scale,
- content widths,
- gutters,
- surfaces/elevation,
- borders,
- cards,
- buttons,
- inputs/forms,
- hero rhythm,
- navigation/footer density,
- motion.

Nodes store only intentional exceptions.

### F. Transient WebsiteDesignPlan

GPT emits a compact plan, for example:

- visual direction,
- chosen theme preset/token values,
- pages,
- per-page recipe sequence,
- content slots,
- media asset references,
- action keys,
- responsive emphasis.

Server resolves it into v3 in one atomic batch.

The plan is not persisted as a second website source.

### G. Design-focused quality inspector

Run one multi-breakpoint quality pass after a design batch.

Return:
- severity,
- page,
- stable node ID,
- issue,
- safe auto-fix eligibility.

Use targeted auto-fix batches only for deterministic presentation issues.

### H. Operation-based undo

Record reversible mutations rather than 80 full-document JSON snapshots.

### I. Targeted DOM rendering

Stable IDs should allow insert/replace/remove of affected subtrees.

Reserve full `main` rebuild for page load, route switch, and explicit full reset.

---

## Proposed 10–15 minute GPT workflow

For a normal 4–6 page business site with facts/media already available:

1. **0–1 min — Inspect**
   - load compact site summary, facts, action catalog hashes, media index.

2. **1–3 min — Plan**
   - GPT selects design tokens, pages, section recipes, CTA flow, and media.

3. **3–6 min — Build**
   - one or two mutation batches generate the initial complete v3 website.

4. **6–9 min — Visual refinement**
   - inspect desktop + mobile renders.
   - apply only targeted section/node batches.

5. **9–12 min — Quality**
   - run design-quality inspector across breakpoints.
   - apply deterministic safe fixes.

6. **12–15 min — Final review**
   - verify conversion path, CTA/action selection, copy, media, mobile.
   - save draft for user review.
   - publish remains a separate authorized action.

This is a realistic architecture target; actual timing must be measured after implementation.

---

## Context/token strategy

The default GPT turn should consume only:
- compact core operating contract,
- site summary,
- current page outline,
- selected section/node,
- requested recipe schemas,
- needed action/media records.

Do **not** automatically include:
- entire Master Source,
- full signal catalog,
- all collections,
- complete history,
- full 40k-character operating contract every time.

Master Source remains available for diagnostics and audit, not as the routine editing payload.

---

## Priority implementation sequence

### P0 — Correctness before any validation
1. Remove the stale `legend-cms-image` runtime-class insertion contradiction.
2. Reconcile current branch tests/contracts with the new mobile shell/media authorities.
3. Perform source-level stale-path sweep.

### P1 — Largest time/token reduction
4. Compact/lazy GPT contract.
5. Add compact `site.summary` and `page.outline`.
6. Add selected-node GET + atomic selected-node PATCH.
7. Add canonical typed `mutation.batch`.
8. Replace 900 ms full-document autosave with mutation batching/checkpoints.
9. Return changed scopes, not complete documents, from routine mutations.

### P2 — Premium generation speed
10. Expand semantic theme tokens.
11. Add premium Section Recipe Catalog.
12. Add page recipes.
13. Add transient `WebsiteDesignPlan` → v3 resolver.
14. Expose deterministic browser-agent operations for plan/batch/recipe/theme/quality.

### P3 — Visual quality + editor efficiency
15. Add multi-breakpoint Design Quality Inspector.
16. Add targeted DOM updates.
17. Replace full-document undo snapshots with reversible operations.
18. Index synchronized shared component/form instances.
19. Improve media semantic metadata/indexing.
20. Lazy-load management panels/history/catalogs.

### P4 — Structural/developer performance
21. Batch/server-side legacy materialization.
22. Source-split the CMS runtime and bundle one public artifact.
23. Split WebsitePlatformController internal responsibilities.
24. Split large website regression suites by domain.
25. Measure final publish compiler; only then consider warm Node workers.

---

## Non-goals / boundaries

Do not:
- add a second website database/source,
- allow GPT to write protected signals/endpoints/provider wiring,
- replace protected forms with custom HTML,
- create page-specific CSS patches for global behavior,
- persist recipe definitions as a competing authority,
- weaken publication validation to gain speed,
- move final live compilation into the iterative editing loop.

The speed objective must come from **smaller context, smaller mutations, stronger reusable design primitives, and fewer full-site passes**, not bypassing canonical protection.
