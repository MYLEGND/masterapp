# Founder translation entitlement repair

The owner confirmed that Founder translation must be unlimited. Production trace aggregation over twelve hours recorded 83 English-target and 11 Haitian-Creole-target `translation_quota_exhausted` outcomes, plus 42 English-target `translation_request_in_progress` outcomes. These are aggregate observations, not account-specific proof. The deployed repository default is zero monthly characters; the inspected portal has no entitlement app-setting override.

`TranslationEntitlementAuthority.TryReserveAsync` recognized only persisted account entitlement records for unlimited usage. The existing controlled-resource authority already identifies the configured Founder, but its authority was not reflected in quota reservations or snapshots. Client profile canonicalization can also replace the Entra object ID with the stored member alias.

The repair uses the existing controlled-resource Founder authority for unlimited reservations and entitlement presentation. That authority resolves the existing database-backed client identity forms before checking the configured Founder object ID. No name, email, language pair, new entitlement record, or separate inference route grants unlimited access. Other accounts retain their current grants and limits. Provider capacity, policy, metering, reservation ownership, and retained translation reuse remain enforced.

Focused verification: 30 passed, zero failed or skipped across Founder entitlement, entitlement identity, quota/router, and mobile translation endpoint cases. The new cases cover Agent, Client via object ID, Client via stored alias, usage metering without a duplicate entitlement row, and denial for a non-Founder account with zero allowance.

Live affected-account balances and authenticated bidirectional translation remain unverified. This repair does not establish another recipient's paid allowance or claim that every observed provider failure is resolved. Release and live results must be recorded separately.
