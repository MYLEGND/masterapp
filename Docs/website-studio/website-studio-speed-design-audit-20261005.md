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

---

# Final audit completion pass — verified 2026-10-05

The following findings were verified directly against the current branch head after the initial audit above. They complete the speed/quality architecture picture and refine the implementation plan.

## 28. There is no single client mutation authority

The editor currently has roughly fifty direct \`checkpoint()\` / \`markDirty()\` mutation call sites.

That means text editing, page metadata, layout controls, media insertion, duplication, source edits, theme controls, and other authoring actions mutate \`documentState\` directly and only converge later through autosave/normalization.

Consequences:

- there is no compact operation journal,
- undo has to snapshot the whole document,
- autosave cannot know the minimum changed scope,
- DOM rendering cannot reliably patch only the changed subtree,
- shared-component synchronization has to rediscover relationships by scanning,
- browser-agent operations have no deterministic command surface to call,
- conflict handling operates at whole-site revision granularity instead of the actual changed scope.

**Canonical replacement:** every authorable browser action must dispatch a typed mutation operation through one client command dispatcher. The dispatcher may optimistically update the local view, but persistence must flow through one server mutation authority over \`WebsiteContentDocument v3\`.

## 29. One ordinary save repeats global work on both sides of the wire

The current browser save path:

1. synchronizes shared presentation globally,
2. serializes the complete \`documentState\`,
3. POSTs the complete document,
4. receives the complete protected document,
5. normalizes that complete document again in the browser.

The server save path then:

1. sanitizes the complete candidate,
2. applies system template authority,
3. sanitizes the complete current baseline,
4. rebuilds the CTA catalog,
5. serializes the complete candidate through \`WebsiteSiteSource\`,
6. parses that complete source back against the baseline to restore protected authority,
7. scans the complete document for media references,
8. serializes the complete document for persistence.

This is correct-by-brute-force, but expensive for a one-field visual edit.

**Canonical replacement:** mutation commands should change only bounded authorable fields while invoking the same protected-semantic restoration service directly. Full-document validation remains mandatory at publish and can remain available as a periodic checkpoint, but it must not be the routine unit of work for every drag, text edit, spacing adjustment, or GPT refinement.

## 30. Shared presentation synchronization is repeatedly rediscovered by traversal

\`synchronizeCanonicalSharedPresentation()\` walks every page composition to rebuild groups every time it runs. \`markDirty()\` invokes it, normalization invokes it, and save invokes it again.

Today the only canonical shared-presentation group handled here is the canonical inquiry form, so a global traversal is disproportionate to the mutation.

**Canonical replacement:** maintain a transient sync index:

\`sync key → stable node IDs / locations\`

Update that index only when structure changes. Presentation mutations then copy to the indexed peers directly. The index is derived runtime state, never persisted authority.

## 31. Site-wide revision conflicts are too coarse for bounded edits

Selected Source and management writes reject on one global \`state.Revision\`. A change anywhere in the site can force a re-fetch/rebase even when the node being edited did not change.

Selected Source partly compensates by comparing the node after a conflict, but only after reloading the complete Source and rebuilding the complete proposed site.

**Canonical replacement:** keep the global persisted revision, but add scope fingerprints to mutation commands.

A mutation request carries:

- last known global revision,
- target stable IDs / page keys,
- expected fingerprint for each changed scope.

If the global revision changed but the target fingerprints are unchanged, the server can safely rebase the authorable mutation onto the latest canonical document. If the same target changed, return a precise conflict list.

This preserves optimistic concurrency without making unrelated edits block each other.

## 32. The authoring Source projection is still token-heavy

\`WebsiteSiteSource\` omits protected signals, but it serializes the typed model with \`DefaultIgnoreCondition.WhenWritingNull\`. Empty/default collections and objects are still emitted.

A simple node can therefore carry repeated boilerplate such as empty style dictionaries, breakpoint dictionaries, default layout objects, empty animation arrays, presentation maps, and child arrays.

That makes both Master Source and Selected Source larger than necessary even before the whole-site expansion problem.

**Canonical replacement:** keep \`WebsiteContentDocument v3\` unchanged and introduce a **compact reversible Site Source projection** that omits canonical defaults and empty collections.

Examples:

- omit \`style\` when empty,
- omit \`breakpointStyles\` when empty,
- omit default \`layout\`,
- omit \`breakpointLayouts\` when empty,
- omit \`animations\` when empty,
- omit empty field presentation maps,
- omit \`children\` for leaf nodes.

The parser supplies the defaults. This is a projection-format optimization, not another website source.

## 33. Source protection validates a complete reconstructed graph for a one-node change

Selected Source currently reconstructs the complete projected document in the browser, POSTs it to \`manage/source/validate\`, parses/protects the complete graph on the server, validates media for the complete graph, then returns a complete proposed document. The browser then POSTs that complete proposed document through the ordinary save path, which protects it again.

**Canonical replacement:** Selected Source becomes a node projection over the mutation service:

- GET selected node projection + node fingerprint,
- PATCH selected node authorable fields,
- one server protection/mutation transaction,
- return protected changed node + new revision/fingerprint.

Master Source remains a diagnostic/audit surface only.

## 34. The browser-agent workspace is descriptive, not operational

The GPT Workspace currently exposes the full operating contract plus buttons that open Master Source, Selected Source, Media, Quality, and Publish.

The agent still has to:

- navigate panels,
- inspect large source,
- type low-level JSON,
- click apply,
- wait for full validation/save,
- manually repeat section/page construction.

There is no compact deterministic browser-agent command surface for high-level design work.

**Canonical replacement:** expose a browser-safe command surface backed by the same mutation/query services. Recommended commands:

- \`getSiteSummary()\`
- \`getPageOutline(page)\`
- \`getNode(id)\`
- \`listRecipes(filter)\`
- \`listMedia(query,cursor)\`
- \`applyMutationBatch(batch)\`
- \`applyDesignPlan(plan)\`
- \`runDesignQuality(scope)\`

The browser workspace remains human-readable, but agents should not need to manually operate the low-level Source editor for normal builds.

## 35. Media discovery is bounded but not design-aware

The current media library returns at most 200 records and exposes roughly:

- ID,
- display name,
- URL,
- content type,
- bytes,
- created time.

The persisted media entity itself has no design metadata such as width, height, aspect ratio, orientation, focal point, semantic role, alt description, or usage index.

For GPT, that means choosing the right hero/logo/gallery asset still requires extra visual browsing.

**Canonical replacement:** preserve the same owner-scoped media authority, but add derived design metadata. At minimum:

- pixel width/height where known,
- aspect ratio/orientation,
- canonical media kind,
- optional user/GPT-authored semantic tags,
- optional focal point,
- alt/description suggestion,
- current usage locations.

Do not infer trust-sensitive identity from images. These fields are composition metadata only.

Media listing should be cursor-paged and filterable rather than a fixed \`Take(200)\`.

## 36. Quality inspection is structurally useful but visually shallow

The saved-draft inspector currently checks metadata, navigation labels, collection/data bindings, duplicate IDs, alt text, destinations, canonical form authority, and reusable-component references.

The live canvas adds only a small set of rendered checks.

It does not currently provide the visual evidence needed for premium autonomous refinement.

**Canonical replacement:** add deterministic design-quality checks for:

- text/background contrast,
- minimum readable text,
- heading hierarchy,
- maximum readable line length,
- touch target size,
- section rhythm,
- inconsistent card/button/input treatments,
- empty/placeholder sections,
- hero CTA visibility,
- breakpoint clipping/overflow,
- image crop resilience,
- mobile navigation usability,
- excessive animation,
- oversized media,
- competing/repeated CTAs,
- excessive per-node overrides compared with theme tokens.

The inspector should return stable node IDs and safe-fix eligibility so GPT can repair only the affected scopes.

## 37. New pages and new blocks start too close to zero

Business page creation produces one section containing one H1. The Add menu is atom-oriented.

That is maximally flexible but forces repeated low-value construction work.

**Canonical replacement:** keep low-level atoms for freeform authoring, but make semantic recipes the fast path. A newly requested page should be constructible from a page recipe plus section recipes in one batch.

## 38. Full-document response bodies create unnecessary client normalization work

Routine save returns the complete protected document. The browser compares the full serialized local document, then can normalize the full returned document again.

Once typed mutation responses exist, routine writes should return only:

- new global revision,
- changed scopes,
- protected changed nodes/page metadata,
- updated fingerprints,
- any canonical corrections/warnings.

A full document refresh becomes an explicit recovery/checkpoint operation, not the normal response.

## 39. Protection and media validation should be scope-aware during editing

The existing protection boundary is correct: protected signals, forms, action authority, runtime template identity, and media ownership must remain server-owned.

The inefficiency is traversal granularity.

For routine mutations:

- protected semantic restoration should operate on changed nodes plus the relevant protected baseline nodes,
- media ownership should check newly referenced/changed media IDs,
- global unique-ID/navigation invariants should be maintained through indexes/targeted checks,
- a complete whole-site validation should still execute before publish.

This preserves safety while removing repeated whole-site scans from the interactive loop.

## 40. Build-speed telemetry is missing

The platform cannot enforce a 10–15 minute build target if it does not measure the expensive boundaries.

Add instrumentation for:

- management/bootstrap response bytes,
- agent-context characters,
- Site Source characters,
- mutation request/response bytes,
- mutation operation count,
- protection/sanitize milliseconds,
- DB load/save milliseconds,
- changed node count,
- rendered node count,
- full-page render count,
- quality pass duration,
- publish compile duration,
- browser-agent command count per completed site.

Log aggregates only; never log customer website bodies or protected form payloads.

---

# Canonical replacement architecture

## Architectural invariant

**There remains exactly one writable website source: \`WebsiteContentDocument v3\`.**

Everything added below is one of:

- a read projection of v3,
- a typed command that mutates v3,
- a code-owned constructor that produces v3 nodes,
- transient derived runtime state,
- final immutable publish output.

There is no second website document, no parallel JSON store, no page-specific override database, no duplicate signal authority, and no GPT-owned backend wiring.

## Layer 1 — Protected platform authority (existing, hardened)

Protected runtime semantics stay outside authorable mutation fields.

The command layer must never allow GPT/browser edits to directly create or rewrite:

- \`signals\`,
- \`fieldSignals\`,
- protected runtime form execution,
- protected form field keys/validation/submission routing,
- system-template identity,
- provider delivery wiring,
- analytics destination semantics,
- store enable/remove authority,
- server-owned commerce/lead/booking execution.

Allowed presentation/content remains free:

- copy,
- visual styling,
- layout,
- responsive presentation,
- media selection,
- motion,
- free content structure,
- approved CTA/action selection,
- field labels/presentation where the current protected form contract allows it.

Protected semantics continue to be restored/validated by stable identity.

## Layer 2 — One mutation engine over v3

Introduce one internal service, conceptually:

\`WebsiteDocumentMutationService\`

It receives a canonical document + actor scope + typed mutation batch and returns:

- protected next document,
- changed scope list,
- inverse operations for transient undo,
- warnings/corrections,
- fingerprints.

All Canvas, Selected Source, GPT, page manager, theme editor, media assignment, recipes, and design-plan generation use this same service.

Recommended operation family:

- \`setText\`
- \`setNodePresentation\`
- \`setBreakpointPresentation\`
- \`setLayout\`
- \`setMedia\`
- \`setApprovedAction\`
- \`insertNode\`
- \`removeNode\`
- \`moveNode\`
- \`duplicateNode\`
- \`insertRecipe\`
- \`createPage\`
- \`duplicatePage\`
- \`updatePageMetadata\`
- \`movePageRoute\`
- \`setThemeTokens\`
- \`setDataBinding\`
- \`saveReusableComponent\`
- \`insertReusableReference\`

Do not create one endpoint/service per UI button. Buttons dispatch operations.

## Layer 3 — Compact read projections

Add read-only projections designed around what the agent/user is doing.

### \`site.summary\`

Typical contents:

- site key + revision,
- business/brand facts required for copy,
- design token summary,
- page list + roles,
- shell summary,
- protected component IDs/types,
- action catalog summary/hash,
- media summary,
- quality summary,
- capabilities.

### \`page.outline\`

For one page:

- page metadata,
- section order,
- section IDs,
- semantic roles,
- recipe identity if applicable,
- key CTA/media references,
- warnings.

### \`node.source\`

For one node/subtree:

- compact reversible authoring projection,
- location,
- stable ID,
- scope fingerprint,
- applicable protected constraints.

These projections are not persisted.

## Layer 4 — Compact agent contract

Split the current large contract into:

1. **small immutable core rules** — always available,
2. **capability/version hashes** — prove which rule/catalog versions apply,
3. **on-demand schemas** — node/recipe/mutation details fetched only when needed,
4. **scoped inventories** — only the actions/media/data records required by the current task.

The operating contract should stop embedding the complete action/signal inventory inside \`promptTemplate\` when those records are already returned structurally.

## Layer 5 — Premium recipe constructors, not templates that constrain creativity

Recipes are accelerators, not a mandatory visual template system.

Each recipe is a code-owned constructor that produces ordinary v3 nodes with fresh stable IDs. Once inserted, the nodes remain normally authorable.

Recommended section recipes:

- cinematic hero,
- editorial split hero,
- product/service hero,
- trust/logo strip,
- metric/proof band,
- service grid,
- bento feature grid,
- editorial feature split,
- product showcase,
- process/timeline,
- comparison,
- social proof/testimonials,
- case-study proof,
- offer/pricing,
- FAQ,
- gallery,
- team,
- contact/inquiry,
- closing conversion band.

Each recipe declares:

- semantic slots,
- allowed content types,
- responsive layout defaults,
- theme-token references,
- optional media slots,
- optional approved CTA slots,
- quality expectations.

**Creativity rule:** GPT may freely combine recipes, heavily restyle them, or build freeform typed node batches from atoms. Recipes remove repetitive scaffolding; they do not define the only allowed look.

## Layer 6 — Page recipes

Page recipes compose section constructors.

Examples:

- Home,
- Services,
- Service Detail,
- About,
- Contact,
- Landing/Lead Gen,
- Offer,
- Team,
- FAQ,
- Gallery/Portfolio,
- Case Study.

A page recipe defines semantic sequence, not final copy or fixed visual branding.

## Layer 7 — Rich semantic theme system

Extend \`WebsiteDesignTheme\` into a compact token system.

Recommended categories:

### Typography
- display,
- h1,
- h2,
- h3,
- body,
- small,
- weights,
- line-height,
- tracking.

### Space
- spacing scale,
- section block spacing,
- content gap scale,
- desktop/mobile gutters.

### Geometry
- narrow/standard/wide content widths,
- card radius,
- button radius,
- input radius,
- border strength.

### Surface
- canvas,
- elevated surface,
- muted surface,
- glass/scrim,
- elevation/shadow levels.

### Components
- primary/secondary/ghost button geometry,
- card treatment,
- input treatment,
- navigation density,
- footer density.

### Motion
- fast/standard/slow durations,
- easing families,
- bounded entrance distance.

Prefer fluid typography/layout where possible so GPT does not need duplicated breakpoint values for every node.

Nodes should store intentional exceptions only.

## Layer 8 — Art-direction presets as optional token constructors

For fast premium setup, code-owned presets may initialize theme tokens without becoming persisted template authority.

Examples:

- **Roadster Precision** — high contrast, deep surfaces, disciplined whitespace, large cinematic type, thin technical borders, restrained gold/accent, fast clean motion.
- Editorial Luxe.
- Modern Minimal.
- Warm Craft.
- Clinical Precision.
- High-Energy Performance.

A preset resolves into ordinary theme tokens. GPT may change any authorable token afterward.

## Layer 9 — Transient \`WebsiteDesignPlan\`

For a full build, GPT should be able to emit one concise plan containing:

- art direction,
- theme/preset + token overrides,
- page list,
- page roles,
- section recipe sequence,
- copy/content slots,
- media IDs,
- approved action keys,
- data bindings,
- responsive emphasis.

The server validates the plan and resolves it through recipe constructors + the mutation service into \`WebsiteContentDocument v3\` in one transaction.

**Do not persist the plan as a second website source.**

## Layer 10 — Operation-based undo + targeted rendering

The client dispatcher records inverse operations transiently.

Undo/redo then applies inverse operations rather than parsing 80 whole-document JSON snapshots.

Stable IDs allow targeted DOM changes:

- text/style mutation → update one mounted element,
- node insertion/removal → patch one parent subtree,
- section move → move mounted section,
- theme mutation → reapply token CSS,
- page load/switch → full page render,
- explicit recovery → full render.

Full \`main.replaceChildren()\` should not be routine after a bounded structural mutation.

## Layer 11 — Derived indexes

Maintain transient indexes rebuilt on initial load and structural mutations:

- \`nodeId → location\`,
- \`syncKey → node IDs\`,
- \`mediaId → usage locations\`,
- \`page → section IDs\`,
- \`reusable component → instances\`.

These indexes are derived from v3 and never become competing persisted state.

## Layer 12 — Final publication remains strict and whole-site

Do not weaken the publish boundary.

Publish should still:

1. load the authoritative draft,
2. run complete sanitizer/protection/invariant checks,
3. run complete media ownership checks,
4. validate protected forms/actions/signals,
5. run full quality/publication gates,
6. compile immutable public output through the canonical renderer,
7. commit the published version atomically.

The optimization is to remove final-publish work from the iterative edit loop, not to make publication less strict.

---

# Canonical API shape

Exact naming can change during implementation, but the architecture should converge on this behavior.

## Read projections

- \`GET manage/agent/summary\`
- \`GET manage/agent/page-outline?page=/...\`
- \`GET manage/agent/node?id=...\`
- \`GET manage/recipes?kind=...\`
- \`GET manage/media?cursor=...&q=...&kind=...\`
- \`GET manage/quality?scope=...\`

## Writes

Primary write:

- \`POST manage/mutations\`

Request concept:

- ticket,
- known revision,
- target fingerprints,
- array of typed authorable operations.

Response concept:

- new revision,
- changed scopes,
- protected changed values,
- new fingerprints,
- warnings/corrections,
- precise conflicts when present.

High-level design-plan endpoint may exist as a thin facade:

- \`POST manage/design-plan\`

but it must resolve internally into the same mutation service. It cannot own separate persistence or protection logic.

Selected Source PATCH should also be a facade over the same mutation service.

---

# Target agent context budgets

These are engineering targets, not security limits.

For a normal build turn, aim for:

- core agent rules: **≤ 8 KB**,
- site summary: **≤ 12 KB typical**,
- one page outline: **≤ 6 KB typical**,
- selected node/subtree: **bounded and compact; no whole-site source**,
- requested recipe schemas only,
- requested media/action records only.

Master Source is allowed for diagnostics but should contribute **zero characters** to routine build turns.

A normal agent write should return changed scopes, not the entire site.

---

# Target mutation behavior

For an ordinary one-node edit:

- zero Master Source GETs,
- zero whole-site Source reconstruction,
- one mutation request,
- one protected server mutation transaction,
- one DB save,
- one changed-node response,
- one targeted DOM patch.

For an initial 4–6 page premium build:

- one compact summary read,
- one plan,
- one or two large bounded mutation batches,
- one responsive visual review,
- one quality pass,
- one repair batch,
- user review.

This is the path to a repeatable 10–15 minute build. The target must be measured in production-like test fixtures before claiming it as achieved.

---

# Protection matrix for the new mutation engine

| Domain | GPT/browser may author | Server must own/protect |
| --- | --- | --- |
| Copy | visible content, headings, labels | protected execution messages where required |
| Layout/style | full allowed presentation | sanitizer bounds, shell safety rules |
| Responsive | allowed breakpoint presentation | global mobile shell/navigation geometry rules |
| Media | choose owned asset, crop/focal presentation | ownership, byte/type validation |
| CTA | visible text/style, approved action selection | action destination/behavior catalog |
| Inquiry/protected forms | container/field presentation, allowed labels | fields, keys, validation, submission, lead/event routing |
| Analytics/signals | review approved mappings; allowed explicit catalog selection where policy permits | canonical event identities, provider delivery, server outcomes |
| Commerce | presentation and approved navigation placement | store enable/remove, checkout authority |
| System templates | presentation around stable protected nodes | template identity/runtime execution |
| Publish | request publish | complete validation/compiler/version transaction |

---

# Implementation map — no duplicate authorities

## New internal services/classes

Names are illustrative:

- \`WebsiteDocumentMutationService\`
- \`WebsiteMutationContracts\`
- \`WebsiteAgentProjectionService\`
- \`WebsiteRecipeCatalog\`
- \`WebsiteDesignPlanResolver\`
- \`WebsiteDesignQualityInspector\`
- \`WebsiteDocumentIndex\` (transient/derived)

## Refactor existing authorities

### \`WebsitePlatformController\`

Keep the route authority, but delegate:

- manage bootstrap/query,
- mutation,
- source/node projection,
- media,
- quality,
- publish.

Do not duplicate controller routes in application-specific controllers.

### \`WebsiteSiteSource\`

Keep Master Source as a deterministic audit projection.

Add compact omission of defaults.

Selected Source should use the node projection/mutation authority instead of rebuilding Master Source.

### \`WebsiteStudioAgentContract\`

Reduce to compact invariant rules + capability versions. Move scoped records out of duplicated prompt text.

### \`legend-public-cms.js\`

Source-split for maintainability, then ship one bundled runtime.

The important behavioral refactor is:

\`direct document mutations → client mutation dispatcher → server mutation service → targeted DOM patch\`.

### \`WebsiteDraftQualityInspector\`

Keep structural checks. Add a separate or extended design-quality layer that can consume rendered measurements across breakpoints.

### \`WebsitePageCompiler\`

Leave as the strict final compiler first. Measure after P1/P2/P3 before adding a warm process or worker pool.

---

# Revised implementation order

## P0 — branch correctness before validation

1. Remove the stale default \`legend-cms-image\` insertion.
2. Reconcile current mobile-shell/media regression contracts with current implementation.
3. Sweep stale reserved runtime class paths.
4. Do not touch analytics/lead/provider authority.

## P1 — establish one mutation path

5. Add typed mutation contracts.
6. Add \`WebsiteDocumentMutationService\` using the current protection authorities.
7. Add transient node/location fingerprints and derived indexes.
8. Route Canvas/page/theme/media/reusable authoring through a client mutation dispatcher.
9. Replace full-document autosave with coalesced mutation batches.
10. Replace routine full-document responses with changed-scope responses.
11. Replace whole-document undo snapshots with inverse operations.

**Stop condition:** all ordinary editor writes use one mutation service before recipes/design plans are added.

## P2 — remove GPT/source waste

12. Compact \`WebsiteStudioAgentContract\`.
13. Add \`site.summary\`, \`page.outline\`, and node projections.
14. Make Site Source omit defaults/empty structures.
15. Convert Selected Source to node GET/PATCH over the mutation service.
16. Expose deterministic browser-agent query/mutation commands.
17. Lazy-load advanced catalogs/history/collections/media.

**Stop condition:** a normal GPT edit never needs Master Source.

## P3 — premium generation system

18. Expand semantic theme tokens.
19. Add optional art-direction presets.
20. Add section recipe catalog.
21. Add page recipes.
22. Add \`WebsiteDesignPlan\` resolver that emits mutation batches.
23. Add semantic media metadata/index.

**Stop condition:** a complete multi-page first draft can be constructed in one or two mutation transactions without hand-building every node.

## P4 — visual refinement efficiency

24. Add multi-breakpoint design-quality checks.
25. Add deterministic safe-fix mutations.
26. Add targeted DOM insert/replace/remove/move.
27. Replace global shared-presentation traversal with derived sync index.
28. Add build telemetry.

## P5 — structural cleanup after behavior is proven

29. Batch/server-side legacy materialization.
30. Source-split/bundle the CMS runtime.
31. Split controller internals.
32. Split website regression tests by behavior domain.
33. Measure compiler latency; only then decide whether a warm compiler worker is justified.

---

# Acceptance gates

The replacement is not complete until these are true.

## Authority

- \`WebsiteContentDocument v3\` is still the only writable persisted website source.
- no mutation API accepts direct provider/lead/event delivery configuration,
- protected form execution remains server-owned,
- stable protected node identities survive every authorable mutation,
- publish still runs complete authority validation.

## Efficiency

- normal Selected Source edit sends no whole-site Source,
- normal Canvas edit sends no whole-site document,
- routine mutation response does not return the whole site,
- autosave coalesces operations rather than serializing the full graph,
- unrelated revisions can rebase when target fingerprints did not change,
- initial management/GPT context no longer eagerly includes every catalog/history payload.

## Design speed

- premium section/page recipes exist but freeform typed authoring remains available,
- a 4–6 page first draft can be generated through one plan + one/two batches,
- theme tokens eliminate most repeated per-node style fields,
- media selection exposes enough metadata for fast composition,
- GPT can operate through deterministic commands rather than manual low-level Source editing.

## Visual quality

- quality checks run across desktop/mobile and return stable node IDs,
- deterministic fixes can be batched,
- no routine structural edit requires a complete page rebuild,
- mobile shell safety remains canonical and cannot be displaced by ordinary content edits.

## Measurement

Performance evidence must report at least:

- context characters,
- request/response bytes,
- mutation count,
- changed-node count,
- server mutation duration,
- rendered-node count,
- quality duration,
- final compile duration,
- end-to-end first-draft and final-review time.

The desired 10–15 minute build target is accepted only when measured repeatedly on representative 4–6 page business websites.

---

# Final architecture decision

Do **not** improve the current system by adding another GPT-specific website representation that becomes state.

The correct design is:

\`GPT/user intent → compact projections → optional design plan/recipes → typed mutation batch → ONE v3 document authority → protected semantic restoration → targeted preview → design-quality repair → strict whole-site publish\`

That architecture removes double work while increasing GPT freedom: the model spends its context on art direction, hierarchy, copy, visual composition, and refinement instead of repeatedly reconstructing the same JSON plumbing.

