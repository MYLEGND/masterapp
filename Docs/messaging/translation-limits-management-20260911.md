# Founder translation limits

The Founder AgentPortal menu links to `/founder/translation-limits` directly below Legend Connect. The page uses the existing dashboard theme and Bootstrap modal behavior for global and individual allowance changes. The prior embedded allowance editor links here instead of maintaining a second editor.

The existing `TranslationEntitlementAuthority` owns every decision. `LegendTranslationGlobalPolicies` holds one versioned default; accounts without a custom entitlement, or explicitly returned to `GlobalPolicy`, read that current value. Existing custom allowances remain independent. Changing a policy retains consumed and reserved usage. The configured Founder remains unlimited and metered. Translation access, provider capacity, retention and consent remain separate existing controls.

Search extends the existing account directory with active Agent profiles and preserves existing current-paying Client eligibility and closed-account exclusions. Results remain bounded to eight; users refine searches for further matches. No private messages are displayed. Both profile types use the existing entitlement and access mutation authorities. Invalid allowance input is validated before any access-grant mutation.

Global changes require the configured Founder and an expected version; stale forms cannot overwrite a newer value. Controller mutations require Founder authorization and anti-forgery validation. A missing global record uses the existing configured default until the Founder saves one. No paid allowance is silently selected on behalf of the owner.

Validation so far: focused quota/identity/global-policy tests passed; Release build has zero warnings and errors; JavaScript syntax checked; generated migration matches EF model. The migration only adds the singleton policy table and its non-negative constraint. It has not been applied to production by this local work. The final full regression result is recorded separately. Browser visual and authenticated live checks remain pending because the browser connection exposes no browsers.
