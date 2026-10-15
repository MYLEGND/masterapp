# Work and native engineering execution

Both callers use `LegendEngineeringOrchestrator` for context validation, source inspection,
repair preparation, and role completion. The native Responses adapter owns only native
model transport and its provider readiness. A missing native client registration does not
supply evidence that a Work browser session is unavailable or connected.

The authenticated Founder catalog projects the single executable tool registry:

1. Read `legend_engineering_status` and select an existing server-assigned work item.
2. Call `legend_engineering_bootstrap` with that work item and its assigned role. Consume
   the returned context, bounded task packet, shared directive, and role directive.
3. Pass `engineering_context_id` to `legend_inspect_repository`; use `live` or `candidate`
   as `git_reference`. Keep the exact lease alive with `legend_engineering_renew_turn`
   every 60 seconds. Renewal cannot extend the context lifetime.
4. Only Codex may call `legend_prepare_software_repair`, with the context ID and exact
   bounded replacement proposal. Source text is preserved verbatim.
5. Call `legend_engineering_complete_turn` with the role decision. Then obtain a fresh
   context for the next server-assigned role. Independent review inspects the candidate;
   validation and release remain owned by the existing deterministic lifecycle. Tier B
   approval and required live browser proof are unchanged.

Work and native bootstraps use distinct owners under the same durable lease authority.
A completed lease cannot be replayed as a new successful role completion. Work attempts
are recorded without invented token usage. Neither transport can mint a different role,
ignore Founder pause, or bypass a stale contract, source classification, or evidence check.

The Engineering page reports native execution separately from browser tool registration.
“Tools connected” means the required tools registered, not that a role is executing or a
repair is qualified. A browser without `document.modelContext.registerTool` reports that
transport as unavailable. Repository code cannot enable a capability absent from its host.
