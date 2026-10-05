# Website Studio Speed + Premium Design Audit — 2026-10-05

## Status

**Archived planning evidence. Superseded by the implemented canonical architecture.**

The active architecture contract is:

- `Docs/website-studio/website-studio-north-star-creative-workspace-20261005.md`
- the executable contracts under `Infrastructure/WebsiteEditing/`
- the shared runtime `SHARED/WebsitePlatform/legend-public-cms.js`

Do **not** implement route names, patch strategies, compatibility writers, whole-document authoring flows, or browser repair behaviors from earlier revisions of this audit. They were planning notes and are intentionally removed from this file so they cannot become a second engineering authority.

## Canonical implemented state

Website Studio has one persisted writable website authority: `WebsiteContentDocument v3`.

Normal Canvas, GPT, page, theme, media, recipe, whole-site design-plan, reusable-component, undo/redo, and deterministic quality-repair work resolves to typed mutations against that same v3 document.

The server owns protected execution and platform invariants. Creative authority cannot rewrite protected form execution, `SystemKey`, protected `SystemBinding`, preset signal/field-signal mappings, verified outcome identity, attribution lineage, provider delivery, booking, checkout, or Protect runtime execution.

The shared browser runtime is a client/editor projection only. It does not silently repair persisted canonical v3, synchronize separate form instances, normalize stale shell copies into saved state, or maintain a shadow document. Render-only responsive safety does not write back into v3.

`GET manage/source` is read-only inspection. There is no `manage/source/validate` write flow and no writable Master Source surface. Selected Source resolves to scoped node mutations through the same mutation service.

`POST manage` is retired for ordinary canonical v3 authoring. It exists only at the explicit one-way pre-v3 → v3 materialization boundary. After materialization, all ordinary creative writes use typed mutations.

Undo/redo stores reversible mutation batches rather than full-site snapshots. Structural canvas updates use targeted rendering rather than a second structural persistence path.

The server-owned `WebsiteSystemTemplateAuthority` is the single authority for protected route/template bindings and shared shell invariants. Platform runtime classes cannot be invented or removed by creative mutations.

Persisted canonical v3 fails closed when sanitizer output would differ from stored canonical state. Duplicate node identities fail closed rather than being silently renamed.

Direct non-creative persistence surfaces that still update the same document—such as store settings, canonical signal configuration, import, publish, and the one-time migration boundary—must pass through the same server canonicalization/protection contracts before persistence.

## Performance architecture retained from the audit

The useful conclusions from the original audit were implemented without introducing alternate authorities:

- compact GPT site/workspace projections,
- lazy catalogs and focused node/page reads,
- semantic design themes and art-direction presets,
- premium section/page recipes,
- transient whole-site design plans resolved into typed mutations,
- operation-based history,
- targeted DOM updates,
- design-quality inspection with deterministic safe repair plans,
- scoped mutation responses and performance telemetry.

## Release state

This document is not a release certificate. Source-level implementation and cleanup may be complete on the isolated branch, but validation, PR review, merge, release, and deployment remain separate governed steps.

