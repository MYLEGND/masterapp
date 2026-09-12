# LEGEND billing audit — 12 September 2026

All unused experiment spending authorization is withdrawn. New quota and support requests are paused. Production remains the goal, but does not authorize spending.

## Inventory and commitments

| Item | Verified result |
|---|---|
| Subscriptions | One accessible subscription, LEGEND; existing PAYG subscription remains enabled |
| Existing ARM resources | 24; production resources preserved |
| VMs and managed disks | None |
| Model endpoints and Azure Automation resources | None |
| Shutdown schedules | None |
| Marketplace agreements | None |
| Reservation and savings-plan orders | None |
| Support tickets | None |
| Active deployments | None; one unrelated failed deployment from 2 April remains |
| Experiment compute needing deallocation | None |
| Pending experiment purchase or commitment | None found |

No subscription, support plan, paid Marketplace offer, reservation or savings commitment was purchased or upgraded by this experiment. No experiment VM, disk, network or model endpoint was provisioned.

## Actions recorded

The explicit UTC-day activity query returned 123 events. These include multiple lifecycle events for individual operations, not 123 separate changes. Confirmed experiment actions:

- Registered Microsoft.Compute, Microsoft.Quota and Microsoft.Capacity. Provider registration does not purchase resources.
- Requested East US 2 regional quota from 10 to 24 vCPUs: approved.
- Requested A100-family quota of 24 vCPUs and H100-family quota of 40 vCPUs: both rejected with `QuotaNotAvailableForResource`.
- Attempted the same quota-only support request twice. Both attempts have terminal Failed activity records. The final operation returned `InvalidSupportPlan`: the existing plan is Free. No upgrade was accepted and no ticket was created.
- Used the existing storage account to preserve and verify experiment evidence. Account-key reads did not regenerate keys or change billing arrangements.

The day window also contains earlier appsettings writes at 00:43, 04:46 and 09:33 UTC. This audit did not modify or revert them. No experiment resource creation appears in the control-plane activity. Blob creation is separately verified through storage inventory and the preservation receipts. Activity records may lag.

## Retained storage and charges

The private container `legend-foundation-experiments` in existing account `masterappstorage1221` retains **16 files totaling 218,847 bytes**, under `20260912/historical-local-rejected-4b`. This is the only experiment-created storage found. It remains preserved as instructed; automatic deletion is not configured.

At the previously verified Hot LRS rate of $0.0184/GB-month, capacity costs approximately $0.000004/month—below one cent—plus transaction charges. Exact invoice attribution is unmeasured because this account is shared with existing services. Provisioned experiment VM compute cost is $0; this does not imply all historical storage or provider operations were free.

No experiment model, training or download process remains on the laptop. The three previously cleaned model/checkpoint locations remain absent. No experiment auto-start, scheduler or automatic provisioning was installed.

## Continuing work and evidence

Only local code repairs and checks continue. The controlled foundation and training remain disabled and unconfigured; Qwen3.8-27B has not been deployed. Future paid actions require explicit approval of the exact resource, billing arrangement, total and recurring costs, shutdown behavior and cancellation terms.

Detailed inventories, timestamps, request IDs and activity records: `/private/tmp/legend-pretrained-results/BILLING-AUDIT.json` and sibling `billing-audit-*.json` files. Contact details, credentials and signed polling URLs are excluded. No push, merge or deployment occurred.
