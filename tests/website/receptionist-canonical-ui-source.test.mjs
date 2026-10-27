// Source-level contract checks. These complement, and do not replace, rendered MVC/browser tests.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
const read = path => readFileSync(fileURLToPath(new URL(`../../${path}`, import.meta.url)), 'utf8');
const setup = read('SHARED/Views/Shared/ReceptionistSetup.cshtml');
const management = read('SHARED/Views/Shared/ReceptionistManagement.cshtml');
const content = read('SHARED/Views/Shared/ReceptionistContentReview.cshtml');
const model = read('SHARED/Analytics/ReceptionistManagement.cs');
const owner = read('AgentPortal/Controllers/WebsiteAnalyticsController.ReceptionistManagement.cs');
const business = read('Infrastructure/Businesses/BusinessWorkspaceControllerBase.ReceptionistManagement.cs');

test('shared task hub uses scoped generated destinations and distinguishes saved, permission and scheduled closure', () => {
  for (const fragment of ['receptionist-call-status', 'conversation', 'hours-limits', 'appointments']) assert.match(setup, new RegExp(`id="${fragment}"`));
  assert.match(setup, /Model\.ManagementUrl \+ "#voice-connection"/);
  assert.match(setup, /Model\.ManagementUrl \+ "#operation-reviews"/);
  assert.match(setup, /Model\.ContentReviewUrl/);
  assert.match(setup, /does not change the operating configuration or call permission/);
  assert.match(setup, /outside_reception_hours/);
  assert.match(setup, /does not itself block approved calls and caller-confirmed inquiries/);
  assert.match(setup, /name="MonthlyCashLimit"/);
  assert.match(setup, /name="MonthlyConsumptionLimit"/);
  const advancedAppointments = setup.match(/<details id="appointment-review-details"[\s\S]*?<\/details>/)?.[0];
  assert.ok(advancedAppointments);
  for (const field of ['BookingCalendarConfigurationSha256', 'BookingCalendarTargetSha256', 'BookingPrivacyAcceptanceReference', 'BookingAgentProfileId']) assert.ok(advancedAppointments.includes(`name="${field}"`), field);
  assert.match(advancedAppointments, /Advanced reviewed appointment mapping/);
  assert.match(advancedAppointments, /do not automatically follow calendar changes/);
  assert.doesNotMatch(advancedAppointments, /name="BookingEnabled"|name="BookingQaOnly"|CalendarSetupUrl/);
});

test('both plan and review handlers use fixed, scoped prerequisite and stale projections', () => {
  for (const [controller, scope] of [[owner, 'agentProfileId'], [business, 'businessId']]) {
    assert.equal((controller.match(/catch \(ReceptionistCurrentProjectRequiredException\)/g) ?? []).length, 2);
    assert.equal((controller.match(/ReceptionistPlanFailure\.StaleOrUnavailable/g) ?? []).length, 2);
    assert.match(controller, new RegExp(`ReceptionistManagementFailureAsync\\(owner, ${scope}, ReceptionistPlanFailure.CurrentProjectRequired, 400, cancellationToken`));
    assert.match(controller, new RegExp(`Url.Action\\(nameof\\(Receptionist\\), new \{ ${scope} \}\\)`));
    assert.doesNotMatch(controller, /ViewData\["ReceptionistPlanFailure"\]\s*=\s*error\.Message/);
  }
  assert.match(model, /CurrentProjectRequired/); assert.match(model, /StaleOrUnavailable/);
  assert.match(management, /role="alert"/); assert.match(management, /#@failure\.TargetFragment/);
});

test('credential input never receives a returned value and guarded operation fields remain', () => {
  const secretInput = management.match(/<input[^>]*name="Secret"[^>]*>/)?.[0];
  assert.ok(secretInput); assert.match(secretInput, /type="password"/); assert.doesNotMatch(secretInput, /\bvalue=/);
  for (const field of ['ExpectedConnectionRevision', 'BudgetPolicyMode', 'AcceptDelayedBudgetOverrun', 'ObservedProviderMonthlyBudget', 'ProviderBudgetPeriodStartUtc', 'ProviderBudgetPeriodEndUtc', 'BudgetPolicyExpiresUtc', 'Costs[0].CashFixedEstimate', 'Costs[0].CashPerSecondEstimate', 'Costs[0].ConsumptionFixedEstimate', 'Costs[0].ConsumptionPerSecondEstimate']) assert.ok(management.includes(`name="${field}"`), field);
  assert.match(management, /@Html\.AntiForgeryToken\(\)/);
  assert.match(content, /name="ReviewHash"/); assert.match(content, /name="ExpectedRevision"/);
});

test('shared views use the canonical shell without replacing privacy-restricted view', () => {
  for (const view of [setup, management, content]) assert.match(view, /dashboard-page-shell/);
  assert.doesNotMatch(management, /<main class="container/);
  const restricted = read('AgentPortal/Views/FounderEngineering/ReceptionistStatus.cshtml');
  assert.match(restricted, /Layout\s*=\s*null/);
  assert.match(restricted, /dashboard-page-shell/);
  assert.doesNotMatch(restricted, /<script|_Layout|PortalShellIdentityResolver|BusinessWorkspaceQuickFind/);
  assert.match(read('AgentPortal/Views/FounderEngineering/Receptionist.cshtml'), /asp-controller="WebsiteAnalytics" asp-action="Receptionist"/);
  assert.match(read('SHARED/Views/Shared/_BusinessWorkspaceQuickFind.cshtml'), /business\.CanCustomize[\s\S]*@root\/receptionist/);
});


test('failed forms retain original choices and revisions without redisplaying secrets or silently rebasing', () => {
  for (const controller of [owner, business]) {
    assert.match(controller, /EndsWith\("\.Secret", StringComparison.OrdinalIgnoreCase\)/);
    assert.match(controller, /IsSavedIdentity\(key, sha256\)/);
    const withdraw = controller.slice(controller.indexOf('public async Task<IActionResult> RevokeReceptionistCredential('));
    assert.match(withdraw, /!ModelState.IsValid \|\| credentialId == Guid.Empty \|\| expectedConnectionRevision == Guid.Empty/);
    assert.match(withdraw, /ReceptionistPlanFailure.InvalidWithdrawal, 400/);
    assert.match(withdraw, /ReceptionistPlanFailure.StaleWithdrawal, 409/);
    assert.ok(withdraw.indexOf('return Forbid()') < withdraw.indexOf('!ModelState.IsValid'));
    assert.match(controller, /if \(!await AuthorizeReceptionistManagementAsync\(owner, ct\)\) return Forbid\(\);/);
  }
  assert.match(management, /Previously selected credential \(currently unavailable\)/);
  assert.match(management, /Previously selected funding \(currently unavailable\)/);
  assert.match(management, /submitted\.ExpectedConnectionRevision/);
  assert.match(content, /Model\.SubmittedApply\?\.ExpectedRevision \?\? Model\.SubmittedRollback\?\.ExpectedRevision/);
  assert.match(content, /disabled="@\(Model\.Failure is not null\)"/);
  assert.match(content, /Previously selected version \(currently unavailable\)/);
  assert.doesNotMatch(content, /Html\.Raw|ErrorMessage/);
});

test('unconfirmed results retain exact same-operation inspection without an apply action or invented confirmation', () => {
  for (const controller of [owner, business]) {
    assert.match(controller, /receipt\.ReadBackVerified \? null : new\(receipt\.PackageKey, receipt\.PackageSha256\)/);
    assert.match(controller, /reconciliation: reconciliation/);
  }
  assert.match(management, /name="key" value="@unresolved.Key"/);
  assert.match(management, /name="sha256" value="@unresolved.Sha256"/);
  assert.match(management, /name="reconcile" value="true"/);
  assert.match(management, /review.CanApply && review.ExpectedStateToken is not null && Model.Reconciliation is null/);
  assert.match(management, /does not confirm completion or apply it/);
  assert.match(management, /Model.Summary.ConnectionRevision == Guid.Empty && !detailsUnavailable/);
  assert.match(management, /Model.Summary.Operations.Count == 0 && !detailsUnavailable/);
  assert.match(management, /This does not mean they are missing/);
  assert.match(read('Infrastructure/Reception/ReceptionistOperatorService.cs'), /catch \(ReceptionistCurrentProjectRequiredException\)[\s\S]*\[ReceptionistPlanFailure.CurrentProjectRequired.Message\], \[\], null/);
});
