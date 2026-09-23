# Shared workspace implementation checkpoint

Date: 2026-09-23. This is an incomplete implementation checkpoint, not a release certificate.

## Implemented and verified in this checkpoint

- Added `WorkspaceKey` as the immutable shared owner identity used by the existing marketing owner and analytics scope contracts. Existing `agent:{trackingProfileId:N}`, `business:{businessId:N}` and `founder` persisted marketing keys remain byte-for-byte compatible. Actor object IDs remain distinct from agent tracking-profile IDs. Invalid, mixed and aggregate scopes do not yield a single marketing owner key.
- Extracted portal analytics ownership resolution into `WebsiteAnalyticsScopeResolver`, consumed by both the dashboard and AI controllers. Founder personal scope is now the same default in both. Explicit Founder team/global selection and fail-closed impersonation behavior are preserved. Removed the duplicate resolver implementations.
- Ordinary ClientApp profile saves no longer call Microsoft identity synchronization when the normalized email is unchanged. The same rule applies to the existing AgentPortal client-edit authority used by quick view and the mobile bridge. A directory outage therefore no longer rejects unrelated profile changes through those paths.
- Centralized email-change comparison across those entry points and subscription identity synchronization. Case/whitespace-only edits do not constitute a login change.
- Added structured diagnostic logging for ClientApp identity-update failures, keyed by profile ID and request trace ID.

## Verification

- Application/test build succeeded.
- 23 focused tests passed: profile saves, unchanged-email directory outage, changed-email authority invocation and stable account IDs, existing client/agent profile compatibility, workspace-key compatibility, analytics ownership and impersonation behavior.
- 32 additional regression tests passed: marketing-connection isolation, scoped Meta Ads/pixels, existing Meta conversion/dispatcher behavior, business actions and workspace behavior.
- `git diff --check` passed.
- No live Microsoft email replacement, provider acknowledgment or production browser flow was performed. Mocked identity tests verify controller invocation and ID preservation, not actual Microsoft identity changes.

## Remaining release-critical implementation

The full route/contract matrix and phases in `20260923-shared-workspace-backend-audit.md` remain the implementation plan. The shared owner key introduced here is a foundation, not certification that every CRM operation consumes a resolved workspace context.

1. Complete canonical operation/context adapters and the remaining CRM route families, including auxiliary controllers and native consumers; replace draft optional-business branches rather than add more copies.
2. Implement immutable workflow stage IDs and compatible ownership persistence/migrations.
3. Complete published event-binding execution, visitor/session separation, attribution, scoped OAuth/ads/AI coverage, and end-to-end Meta evidence through the existing Protect dispatcher.
4. Complete account login-address changes across agent and client paths. Current agent ManageProfile ignores submitted email and renders it read-only. Client identity synchronization patches Graph before the SQL transaction commits, so SQL rollback cannot undo directory changes. The synchronization result's redemption URL is currently ignored by profile saves. These issues require recoverable cross-system state and correct provider-specific completion, not a database-only email replacement.
5. Confirm all active account lookup/recipient projections release the old address using stable user/profile IDs. Preserve historical ownership and unrelated account/payment records; never perform an indiscriminate database-wide text replacement.
6. Verify exact approved-branch deployment targets and release receipts after the complete gates pass. No deployment was started for this checkpoint.

## Identity-provider requirements verified

Microsoft Entra guest sign-in email changes preserve the object ID through redemption reset; Microsoft requires the new address in mail/otherMails and the invitation. App-only reset is restricted for users with assigned directory roles. Member UPN changes must use a verified tenant domain. Do not bypass these restrictions, replace account IDs, or declare the login changed merely because a SQL field was updated.

Sources:
- https://learn.microsoft.com/en-us/entra/external-id/reset-redemption-status
- https://learn.microsoft.com/en-us/graph/api/user-update?view=graph-rest-1.0
