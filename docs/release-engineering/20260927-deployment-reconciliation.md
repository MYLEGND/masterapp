# Approved release deployment reconciliation

Release 36353322503 deployed application revision `0b67814aa4a76a68af0f37c93bf1e548f548a88f` successfully to all five selected apps. No completed app is being redeployed to install this workflow repair. The release request manifest is unchanged.

## Observed cause

The synchronous CLI upload (`--async false`) conflated a gateway/transport timeout with a failed Azure operation. Each failure blindly submitted the package again with `--restart true`.

- Protect: first request 22:11:28 UTC, HTTP 500 request timeout 22:18:33, second upload 22:18:53, completion 22:26:25. Total step 14m59s.
- Website: first request 22:34:41, HTTP 504 22:36:28, second upload 22:36:58, explicit `Another deployment is in progress` 22:37:00, third upload 22:38:00, completion 22:38:33.
- Historical release 36332699867 also reproduced Website 504 -> another deployment in progress -> third submission.
- Protect analytics-secret synchronization and Protect/Parfait editor-key synchronization unconditionally wrote app settings even when equal. Azure setting writes can recycle workers.

The logs establish transport/retry defects, not an application exception or proof of an Azure capacity problem. Protect recovered and its public runtime and tracking asset returned HTTP 200 after deployment. No claim is made that future Azure upload/startup time is eliminated.

## Repair

All five selected targets consume the retained SHA256-verified ZIP through `scripts/deploy-approved-app.py`. Embedded package provenance must equal the approved release SHA. Existing selection, migration, authorization, non-cancelling concurrency, and final inventory gates remain in place.

Before upload, read Azure deployment history and runtime revision. Wait for active operations; preserve an already live candidate. Unavailable status or runtime never authorizes a new upload. Submit at most once, asynchronously. Reconcile ambiguous responses through read-only deployment history and runtime probes. Terminal failure stays failed; unknown state stays unverified. A successful new deployment requires a new terminal-success Azure deployment record, no active operations, and two consecutive exact runtime revision reads. No force option, rollback, automatic restart, alternate transport, or competing authority is introduced.

CLI runtime-status polling is replaced by exact runtime-revision verification, not bypassed. The status preflight contacts SCM before upload; CLI warmup fallback is disabled because its exception handler can replay the upload. CLI child-process timeouts do not cancel the Azure operation.

Settings synchronization compares each canonical/legacy key before writing. Matching values produce no Azure writes; changed aliases still converge to the same existing authority.

## Validation

17 focused Python tests cover ambiguous/504 response with active deployment, preserving already live candidates, pending operations, unavailable status/runtime, terminal failures, stable runtime verification, unexpected concurrent publication, old deployment records, checksum/provenance rejection, and execution of the actual workflow settings scripts against a stateful Azure stub. The latter proves zero settings writes across repeated matching runs and exactly one write for each changed key. Seven existing release-policy tests pass. Workflow YAML parsing and whitespace validation pass. The architecture CI gate now executes this suite, and the existing .NET workflow contract requires all five targets to use the shared helper.

## References

- https://github.com/MYLEGND/masterapp/actions/runs/36353322503
- https://learn.microsoft.com/en-us/cli/azure/webapp#az-webapp-deploy
- https://learn.microsoft.com/en-us/azure/app-service/configure-common
- https://github.com/Azure/azure-cli/blob/dev/src/azure-cli/azure/cli/command_modules/appservice/custom.py
