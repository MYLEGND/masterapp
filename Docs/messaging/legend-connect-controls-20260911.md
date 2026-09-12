# Legend Connect control consolidation

Candidate extends repair/call-experience-20260911 based on production b3eeef29. The main Mac production checkout and its unpublished auto-save commit remain untouched.

## Shared responsibilities

- Existing Legend Connect controller, Founder service, translation entitlement authority, capacity authority and Azure resource reader remain authoritative. Translation limits are a partial inside Connect, using the existing save actions and concurrency token.
- A single live-metrics response refreshes both internal translation and provider panels. On failed refresh, previous values are not represented as current. Limits are fetched again on opening/search.
- Azure SKU synchronization is not Azure usage synchronization. Production `masterapp-translator-1221` is F0 (read-only Azure inspection). F0 usage is a canonical application ledger estimate, not an Azure-verified balance. Paid resource character observations are delayed Azure Monitor data, shown separately without double counting.
- Remaining safe capacity now deducts its reserved live allowance. Future-dated usage does not enter current windows. Provider observation timestamps describe retrieval, not complete billing coverage.
- The call authority sends one adaptive policy and optional expiring TURN credentials to native clients. Native implementations use measured bandwidth/RTT, preserve audio priority and reduce screen capture as well as camera quality. Missing measurements reset recovery; old connection/capture callbacks cannot change a newer session.

## Relay hosting and activation boundary

No relay VM exists in the connected subscription. No hosting purchase, new credential, role grant, resource provisioning or production deployment was performed.

The new Founder relay management action operates only the configured Azure VM, through the existing host managed identity. GET does not equate a running VM with working TURN. Start/deallocate require explicit confirmation and antiforgery; Azure acceptance is distinguished from completion. Unknown state prevents action; ambiguous mutation results are not retried automatically. TURN endpoint validation is shared between calling and activation.

First-time provisioning/subscription purchase is not implemented by these lifecycle controls. It remains blocked pending an approved hosting plan and budget. The UI must say setup required, not offer a fictional successful subscription.

Minimum setup to enable the implemented controls:

1. Provision and verify a TURN host under the existing protected infrastructure/release authority, with an approved region, VM size, bandwidth budget, public endpoint and TLS plan. Preserve end-to-end encrypted WebRTC media. Verify authenticated relay allocation, blocked private/metadata destinations and bounded allocation capacity before publishing its endpoint.
2. Keep the TURN shared secret in the existing Key Vault. Configure the same `Calling:Relay:Urls` and Key Vault-backed `Calling:Relay:SharedSecret` on both hosts. Set `Calling:Relay:ResourceId` to that one VM resource. Neither UI nor repository accepts a raw secret.
3. The observed portal managed identity is currently granted Key Vault Secrets User, Key Vault Crypto User, Storage Blob Data Contributor and Translator-resource Reader. It has no VM control role. Grant only `Microsoft.Compute/virtualMachines/instanceView/read`, `Microsoft.Compute/virtualMachines/start/action` and `Microsoft.Compute/virtualMachines/deallocate/action` at the relay VM scope for lifecycle controls. No subscription-wide Contributor grant is needed by this interface.
4. Verify start/status/stop against that resource and then a forced-relay physical-device call. Stopping affects active relayed calls and does not eliminate disk/IP billing. A spending alert is not a hard cost cap.

## Verification limits

Native: Android adaptive policy unit test and compile passed; iOS seven direct-calling tests passed, including two-peer WebRTC negotiation/restart. These do not prove physical weak-Wi-Fi performance or relay connectivity.

UI: syntax, diff checks and jsdom coordinated refresh/failure behavior passed. Supported browser discovery returned no browser; rendered desktop/mobile layout and interactive modal QA are unexecuted.

Backend focused/full combined results are recorded after the final integration run. Do not reuse the earlier 2473-test result as evidence for this expanded candidate. Exact nine known failures/four skipped remain the inherited release boundary, not permission to conceal new failures.

Review corrections: independent native specialist review identified malformed TURN endpoint acceptance, different activation validation, post-mutation configuration reload, repeated-invite authorization and management resource reload races. Lead replaced the duplicate activation check with the shared relay validator, snapshots one policy per command and one VM identity per management action, and rechecks conversation eligibility on repeat invites. Added revoked-membership and billable-start rejection tests.

UI contract change: `LegendConnectPage_KeepsFounderIntelligenceOpenAndCollapsesEveryOtherPanel` asserted seven HTML details/summary rows. The explicit current request replaces those rows with modal summary buttons, so this presentation-only test was replaced by `LegendConnectPage_KeepsHeroVisibleAndOpensSectionsInAccessibleModals`, requiring the same seven sections, matching accessible modal targets, visible hero, integrated translation limits and no expanded accordion. Lead reviewed this change independently; no native held-out prompt, expected answer or threshold changed. The first expanded suite's obsolete UI assertion failure is retained in `/private/tmp/legend-connect-final.log`.

## Dated initial hosting proposal (not subscribed)

Official Azure Retail Prices API lookup on 2026-09-11: East US Linux Standard_D2as_v5 $0.086/hour, Standard static IPv4 $0.005/hour, E4 LRS 32-GiB OS disk $2.40/month. At 730 hours, fixed subtotal is $68.83/month, plus disk transactions, bandwidth, optional logs and taxes. This is a dated planning quote, not a live balance or guaranteed invoice. A non-burstable dedicated 2-vCPU/8-GiB relay avoids depending on accumulated CPU credits; capacity and failure recovery still require load testing.

Example: 1,000 two-party relayed call-hours at 1 Mbps each direction, with 15% overhead, gives about 1,035 GB outbound. At $0.087/GB, estimated egress is $81.35–$90.05 depending on unused first-100-GB allowance. Estimated total $150.18–$158.88; a $175 planning budget would cover that scenario but is not a hard cap. No such budget has been approved or configured. One VM is not a high-availability design.

Sources: https://learn.microsoft.com/en-us/rest/api/cost-management/retail-prices/azure-retail-prices ; https://learn.microsoft.com/en-us/azure/virtual-machines/sizes/general-purpose/dasv5-series ; https://azure.microsoft.com/en-us/pricing/details/bandwidth/ . Initial deployment permissions belong to the existing protected deployment identity, not the front-end caller or a second deployment workflow.
