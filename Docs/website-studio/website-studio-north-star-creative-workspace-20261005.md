# Website Studio North-Star GPT Creative Workspace — 2026-10-05

Working branch: `repair/canonical-mobile-public-nav-shell-20261005`

This document defines the preferred end-state for Website Studio if the workspace is optimized specifically for an AI website designer that must maximize design quality, conversion effectiveness, speed, and freedom without ever being able to break protected backend event/form/tracking authority.

## 1. Core principle

**GPT owns the creative surface. The platform owns executable business truth.**

GPT should freely control:

- site architecture,
- page hierarchy,
- section hierarchy,
- copy,
- art direction,
- typography,
- spacing,
- layout,
- media composition,
- responsive behavior,
- motion,
- proof/trust placement,
- CTA placement and visual emphasis,
- selection of approved platform capabilities.

GPT must not directly control:

- lead creation mechanics,
- protected form submission execution,
- protected field identity,
- canonical analytics event identity,
- attribution lineage,
- provider dispatch,
- checkout execution,
- booking execution,
- protected system-template execution.

The creative system should therefore consume backend behavior as **immutable capability ports**, not editable implementation fields.

## 2. One writable website source

`WebsiteContentDocument v3` remains the only writable persisted website source.

Everything else is either:

- a read projection of v3,
- a typed command that mutates v3,
- a code-owned constructor that creates v3 nodes,
- transient derived state,
- or final immutable published output.

Do not introduce:

- a second GPT website document,
- a second database,
- a parallel renderer,
- page-specific patch stores,
- duplicated form authority,
- duplicated analytics/event authority.

## 3. Immutable Capability Manifest

Add a read-only projection such as `WebsiteCapabilityManifest`, resolved server-side for the authorized site/owner.

Example semantic capabilities:

- `contact.inquiry.submit`
- `lead.create`
- `quote.life.start`
- `quote.life.submit`
- `booking.open`
- `phone.call`
- `email.contact`
- `store.open`
- `checkout.start`
- `purchase.complete`

A capability exposes only what the creative system needs:

- stable semantic capability key,
- user-facing purpose,
- allowed placement types,
- approved action key where applicable,
- protected component identity where applicable,
- availability in the current scope,
- required visual/container constraints,
- read-only outcome description,
- version/hash.

It must not expose mutable provider wiring, credentials, delivery configuration, protected field routing, or raw conversion-dispatch internals.

### Protected event rule

For protected platform components and preset conversion events, the general creative mutation schema contains **no operation capable of writing their signal/event bindings**.

This is stronger than prompting GPT not to change them.

The field is simply outside creative authority.

Existing advanced custom-signal functionality may remain for explicitly non-protected custom interactions, but it stays a separate bounded authority and is not reachable as a side effect of normal generation, recipes, theme edits, Selected Source, or design-plan execution.

## 4. The site is the unit of GPT work

The editor should stop making GPT think page-by-page.

On entry, GPT should understand the complete project:

- business,
- audience,
- offer,
- differentiators,
- service area,
- trust/proof assets,
- primary conversion,
- secondary conversions,
- protected capabilities,
- site map,
- section outlines,
- current visual system,
- media inventory,
- current quality warnings.

Then GPT designs the whole conversion narrative before touching individual pages.

Target workflow:

`inspect site once → architect full funnel → generate whole site graph → preview any page instantly → refine only deficient scopes`

A page becomes one view of the site graph, not a separate editing job.

## 5. Canonical Creative Workspace Manifest

The default agent context should be a compact `WebsiteCreativeWorkspaceManifest`.

It should contain:

### Business intent
- business name,
- category,
- target customer,
- primary offer,
- differentiators,
- approved business facts,
- conversion priority.

### Site architecture
- route/page list,
- role of each page,
- section outlines,
- shared shell,
- reusable components.

### Design system
- semantic theme tokens,
- current art direction,
- responsive policy,
- component variants.

### Protected capabilities
- compact Capability Manifest,
- approved CTA/action choices.

### Media
- semantic media index.

### Evidence
- quality warnings,
- conversion-path warnings,
- placeholder/stale-content warnings.

### Versioning
- site revision,
- per-scope fingerprints,
- capability/catalog hashes.

This manifest should be cacheable. Subsequent GPT turns should receive only changed scopes/deltas wherever possible.

## 6. Intent-first whole-site design plan

GPT should first produce a compact transient `WebsiteDesignPlan` containing:

1. **Conversion architecture**
   - primary action,
   - secondary action,
   - trust/proof strategy,
   - objection handling,
   - protected capability placements.

2. **Information architecture**
   - page list,
   - page purpose,
   - navigation hierarchy.

3. **Art direction**
   - visual language,
   - typography behavior,
   - section rhythm,
   - surfaces,
   - imagery treatment,
   - motion restraint.

4. **Page narratives**
   - ordered section roles,
   - message/purpose of each section,
   - proof/media/CTA requirements.

5. **Protected capability placements**
   - capability key,
   - page/section,
   - presentation role.

6. **Responsive intent**
   - mobile priority,
   - reflow strategy,
   - hero/media emphasis.

The plan is never persisted as another website source. It resolves into ordinary typed mutations against v3.

## 7. Three construction levels

For maximum speed without constraining creativity, GPT should have three interchangeable levels.

### Level 1 — Page recipes

Fast page-family constructors:

- Home,
- Services,
- Service Detail,
- About,
- Contact,
- Landing,
- Offer,
- FAQ,
- Team,
- Gallery,
- Case Study.

### Level 2 — Section recipes

Premium semantic constructors:

- cinematic hero,
- editorial split hero,
- trust/logo strip,
- metric/proof band,
- service grid,
- bento feature grid,
- feature split,
- process/timeline,
- comparison,
- testimonials,
- case-study proof,
- offer/pricing,
- FAQ,
- gallery,
- team,
- inquiry/contact,
- closing conversion band.

### Level 3 — Freeform v3 composition

GPT can build ordinary typed nodes directly when a genuinely unique layout is needed.

Recipes are accelerators, not restrictions. Every recipe resolves into normal v3 nodes with fresh stable IDs.

## 8. Protected capability slots inside recipes

Recipes should support semantic slots such as:

- `primaryCta`,
- `secondaryCta`,
- `leadForm`,
- `bookingAction`,
- `storeAction`.

A recipe may request a slot, but only the server can resolve the slot from the current Capability Manifest.

Example:

`closing-conversion-band.leadForm = capability(contact.inquiry.submit)`

The recipe controls presentation around the capability.

The platform supplies the protected executable component.

GPT never copies backend behavior into ordinary website JSON.

## 9. One mutation engine

Introduce one canonical internal write authority such as `WebsiteDocumentMutationService`.

Canvas, GPT, Selected Source, themes, page management, media placement, reusable components, recipes, and design-plan execution all use it.

Typed operations should include:

- set text/content,
- set node presentation,
- set breakpoint presentation,
- set layout,
- set media,
- select approved action/capability,
- insert/remove/move/duplicate node,
- insert recipe,
- create/duplicate/update page,
- set theme tokens,
- set data binding,
- save/insert reusable component.

Protected backend/event fields are not writable operations.

## 10. Scope hierarchy

Every authorable decision should explicitly target one scope:

1. global design tokens,
2. site shell,
3. page,
4. section/component,
5. node,
6. breakpoint override.

Prefer the highest valid scope.

Examples:

- changing all primary-button radius = one component/theme mutation,
- changing one unique hero button = one node override.

This keeps both documents and GPT reasoning compact.

## 11. Conversion-aware design without mutable tracking

GPT should know the semantic meaning of protected capabilities:

- this action creates a lead,
- this starts a quote,
- this opens booking,
- this enters checkout.

That is enough to optimize:

- CTA hierarchy,
- visual emphasis,
- placement,
- repetition,
- trust before conversion,
- mobile accessibility,
- objection handling.

GPT does not need writable access to tracking implementation.

Add a read-only Conversion Path Inspector that reports:

- primary conversion capability,
- where it appears,
- whether it is above the fold,
- whether a strong closing conversion exists,
- whether competing primary CTAs exist,
- whether protected conversion components are visually usable,
- whether mobile preserves the conversion path.

The inspector reads protected authority and v3 presentation. It never changes analytics.

## 12. Semantic theme system

The site should expose strong semantic tokens rather than forcing node-by-node styling.

Recommended token categories:

- display/H1/H2/H3/body/small typography,
- weights/line-height/tracking,
- spacing scale,
- section rhythm,
- content widths,
- gutters,
- surfaces/elevation,
- border strength,
- card system,
- button system,
- input/form system,
- hero sizing,
- navigation/footer density,
- motion durations/easing.

Nodes store only intentional exceptions.

Optional art-direction presets can initialize these tokens, e.g. **Roadster Precision**: deep high-contrast surfaces, large cinematic type, disciplined whitespace, thin technical geometry, restrained accent/gold, and clean restrained motion.

Presets resolve into normal tokens and never become a competing runtime.

## 13. Design-aware media index

Keep the same owner-scoped media authority, but expose compact composition metadata:

- media ID,
- type,
- width/height,
- aspect ratio,
- orientation,
- semantic tags/role,
- focal point where available,
- alt/description suggestion,
- usage locations.

This lets GPT choose hero/logo/gallery assets without repeatedly browsing opaque filenames.

Media remains validated by actual bytes and ownership.

## 14. Global creative operations

GPT should be able to issue high-leverage site-wide operations such as:

- apply/change art direction,
- tune typography hierarchy,
- tune section rhythm,
- normalize primary/secondary buttons,
- normalize cards/inputs,
- strengthen primary CTA emphasis,
- reduce mobile density,
- change repeated media treatment,
- place proof before conversion points,
- apply a responsive policy to matching sections.

These expand into ordinary bounded mutations.

GPT should not have to visit every matching node manually.

## 15. Persistent whole-site workspace

The creative session should stay loaded.

Normal v3 page switching should not reload the entire editor/bootstrap.

The workspace should preserve:

- design-plan context,
- capability manifest,
- media index,
- theme state,
- undo journal,
- page outlines,
- safe rendered page caches.

A full site should feel like one design canvas with multiple routes, not six independent editor sessions.

## 16. Targeted preview and review

After generation, GPT should inspect rendered outcomes rather than re-reading the whole source.

Useful evidence:

- desktop/mobile previews,
- overflow measurements,
- contrast/type/touch-target findings,
- section-level quality summaries,
- conversion-path visibility,
- mobile navigation state.

Then GPT fetches exact nodes only for the scopes that need repair.

## 17. Operation-based undo and targeted DOM updates

Undo/redo should store inverse mutations instead of 80 complete site JSON snapshots.

Stable IDs allow targeted rendering:

- text/style edit → one element,
- node insert/remove → one parent subtree,
- section move → move mounted section,
- theme update → reapply tokens,
- page switch → render selected page,
- explicit recovery → full render.

Routine mutations should not rebuild the whole page.

## 18. Derived indexes

Maintain transient indexes derived from v3:

- node ID → location,
- sync key → node IDs,
- media ID → usage locations,
- page → section IDs,
- reusable component → instances.

These improve speed but are never persisted as competing authority.

## 19. Canonical multi-system reuse

All scoped systems should use the same creative engine:

`scope → capability manifest → workspace manifest → recipes/mutations → WebsiteContentDocument v3`

### LEGEND
Gets LEGEND-scoped capabilities and route restrictions.

### Protect
Gets protected quote/form/template capabilities and protected route/runtime rules.

### Business
Gets inquiry, booking, contact, store, commerce, and business-data capabilities according to configuration.

### Agent
Gets only capabilities authorized for that agent.

### Future site types
Add new capabilities/policies to the resolver instead of forking the editor.

Recipes should be shared where visual grammar is the same, filtered by required/optional capabilities.

## 20. Ideal end-to-end build flow

### Step 1 — Understand once
Load Creative Workspace Manifest + media index + Capability Manifest.

No Master Source required.

### Step 2 — Architect once
Choose site map, funnel, page roles, art direction, conversion hierarchy, trust strategy.

### Step 3 — Generate whole site
Submit one `WebsiteDesignPlan`.

Server resolves:

`plan → recipes/freeform nodes → typed mutation batch → protected capability slots → one v3 document`

and persists atomically.

### Step 4 — Render changed pages
Return changed scopes and render them without page-by-page construction/navigation.

### Step 5 — Inspect
Run responsive design quality + conversion-path inspection.

### Step 6 — Refine in batches
Repair grouped issues with one/few mutation batches.

### Step 7 — Human direction
High-level feedback like “more premium,” “less dense,” or “stronger hero” maps to global/section/node mutations at the correct scope.

### Step 8 — Strict publish
Final publish alone performs complete whole-site protection, media, forms/actions/signals, quality, compiler, and immutable-version validation.

## 21. Performance target

Representative 4–6 page business site:

- one initial compact project-context fetch,
- one whole-site design plan,
- one or two build mutation transactions,
- zero Master Source reads required,
- zero page reloads required for construction,
- one responsive quality cycle,
- one/few repair batches,
- one continuous creative session.

For a single-node edit:

- one compact mutation,
- no full-site source,
- no full-document response,
- no unrelated media scan,
- no full-page rerender.

The 10–15 minute target is accepted only after repeated measured runs on representative sites.

## 22. Non-negotiable invariants

The architecture is unacceptable if any of the following can happen:

- changing button text changes event identity,
- moving/restyling a protected form changes submission routing,
- replacing a visual container creates a second form authority,
- a recipe copies protected behavior into ordinary JSON,
- GPT can invent an action/event and make it executable,
- design-plan execution can rewrite AnalyticsEvents/provider mappings,
- page duplication duplicates protected executable identity incorrectly,
- responsive editing changes protected backend semantics,
- custom embeds can impersonate protected events,
- naming a capability makes it executable without server authorization.

Executable capability resolution always happens server-side from the current authorized Capability Manifest.

## 23. Preferred hard boundary

The writable creative schema should effectively be:

`content + presentation + structure + media references + approved capability references`

not:

`content + presentation + raw analytics/provider/form execution configuration`.

This allows the creative engine to become dramatically more capable without increasing its power to break conversion tracking.

## Final north-star

The ideal AI website workspace gives GPT the complete business, conversion intent, site architecture, semantic design system, media inventory, and read-only executable capability map up front.

GPT can then design and mutate the entire site graph coherently and in batches, with maximum freedom over persuasion, layout, art direction, copy, responsiveness, media, motion, and visual hierarchy.

Every lead, analytics, attribution, provider, protected-form, checkout, booking, and protected runtime behavior remains behind immutable server-resolved capability references.

That is the setup that maximizes **creativity, speed, conversion quality, scalability, and backend safety simultaneously**.
