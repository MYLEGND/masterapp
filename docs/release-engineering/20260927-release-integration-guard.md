# Exact validation and scoped release integration

## Production observations

The lifecycle bot merged PR #250 at 22:40 UTC, PR #251 at 23:03 UTC and PR #252 at 23:09 UTC before their full CI completed. The previous integrate path trusted only repository merge permissions and always dispatched a release. Maintenance changes selected automatic mode, which expanded to all five apps. This caused unwanted release #265, replaced queued #266 with #267, and invalidated an earlier report that the workflow-only change did not redeploy apps. Existing successful deployments are not rolled back.

## Corrections

- Require successful latest exact-head pull-request workflow runs, identified by workflow path rather than duplicate check names. Require architecture validation and full-suite comparison for application changes; extend that comparison trigger to all application hosts, Domain, Infrastructure and SHARED.
- Repeat that evidence check before any release discovery/build/migration/deploy. A prematurely merged candidate or manual dispatch fails closed.
- Only a changed approved release request authorizes publication. Maintenance merges do not dispatch apps. An automatic input cannot replace the explicitly committed target list.
- Detect release-request changes against the first parent so ordinary and merge commits behave consistently. Unrelated later maintenance commits cannot inherit release authorization.
- Retained branch corrections wait for fresh CI. Successful production merge-back does not itself authorize another app release.
- Completed CI can resume eligible integration. Explicitly dispatch the existing read-only diagnostic after a reviewed diagnostic-workflow change because bot merges do not emit ordinary push workflows.

## Consent regression correction

PR #251 removed a boolean Range attribute to fix Protect browser consent validation, retaining controller enforcement. Architecture CI correctly caught that property-level model validation also requires affirmative consent. Use built-in CustomValidation referencing a boolean validator: false remains invalid for model consumers, while HTML retains its required checkbox without incompatible numeric range metadata. Do not weaken the failing test.

## Release scope and safety

The new request selects Portal, Client, Protect and Parfait for the changed shared marketing context and reporting/consent repairs. No force merge, policy exception, secret extraction, setting override, rollback or manual restart is introduced. The retained immutable ZIP deployment reconciliation from PR #250 remains in place. Lifecycle/selection/reconciliation tests cover missing/pending/failed/cancelled/skipped CI, duplicate workflow names, stale successes, merged request identity, non-release maintenance and automatic scope preservation.
