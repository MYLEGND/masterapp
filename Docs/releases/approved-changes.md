# LEGEND Approved Changes

`legend/approved-changes` is the sole protected Git integration and web-release branch. Git branch identity is source authority; deployed state is proven separately by immutable direct-release receipts and live runtime provenance. There is no second promotion branch.

1. Start bounded work from the exact current approved head on an isolated task branch. Preserve unrelated work and never reset, rebase, force-push, or auto-stash another session's changes.
2. Open a same-repository PR to `legend/approved-changes`. The lifecycle requires the exact owning validation workflows for the changed paths; successful unaffected gates are preserved by the canonical resume authority.
3. Merge only through the protected approved-branch lifecycle. A merge authorizes source integration, not deployment.
4. Deployment requires an explicit reviewed `Docs/releases/direct-release-request.json` with `releaseMode: approved-only` and an explicit target list. The only web deployment authority is `.github/workflows/all-intentional-direct-release-20260918.yml`.
5. Direct release binds authorization scope separately from immutable application evidence. The application revision is the exact validated PR head; architecture validation builds and hashes its package set once, and release restores those exact bytes. Target scope/routing remain governed by the reviewed release request. Migrations execute from the validated migration bundle against the separately proven Portal database baseline, exact-live targets are preserved, and final live-provenance enforcement remains mandatory.
6. `.github/workflows/approved-release-security-validation.yml` is validation-only. It owns migration-artifact checks, skipped-security-test rejection, vulnerability audit, committed-secret scanning, composition invariants, key-ring invariants, and diff integrity. It cannot deploy or mutate production.
7. Native binaries keep their signed distribution processes. Android internal distribution may be invoked manually only from the protected approved branch. Git synchronization never installs or publishes a native binary.
8. Delete task branches only when their entire tip is preserved in approved changes, no open PR or active workflow references them, their latest workflow has not failed/cancelled, and every live web revision plus direct-release receipts prove the work is safely retained. Deletion uses an expected-SHA lease.

## Dynamic validation preservation

`scripts/validation-resume.py` is the one canonical resume authority. A successful gate is reused while its inputs, workflow definition, and required dependencies remain valid. A failure invalidates only the failed/affected gate and genuine prerequisites. Step 5 reuses content-identical candidate/baseline evidence by exact Git tree identity. Architecture validation also produces the sole immutable deployable package set for the exact validated PR head through `scripts/release-package.py`; direct release is forbidden from rebuilding production bytes. Release mutations remain current-state/idempotence checks, and every direct-release attempt retains an exact step-state receipt.

Uncertainty fails closed: missing/expired evidence, a changed validation authority, changed gate inputs, or dynamic live/external state can require fresh proof. It does not justify replaying unrelated successful gates.

## Workspace synchronization

The existing `com.mylegnd.native-checkout-sync` LaunchAgent uses the canonical sync script. The normal published checkout tracks `legend/approved-changes`; native testing may use an explicitly configured `legend.nativeTestingRef`. Synchronization is fast-forward only, pauses around active editors/builds, and never resets, rebases, force-pushes, or auto-stashes.

This is inbound source synchronization, not deployment. Review, commit, and push local work explicitly. Runtime deployment state is determined by release receipts and provenance endpoints, never by assuming that a local or remote branch name is live.
