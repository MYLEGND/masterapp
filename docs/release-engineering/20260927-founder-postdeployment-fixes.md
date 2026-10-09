# Founder post-deployment reporting and consent fixes

Release 36353322503 completed successfully at application revision `0b67814aa4a76a68af0f37c93bf1e548f548a88f`. Deployment reconciliation PR #250 is merged at `64c9f7b71a2b6fa88709d81a5d3fffd148f0c918`; it did not redeploy applications.

## Confirmed production defects

- Protect assessment step 6 displayed a consent error with its checkbox checked. A boolean `Range(true,true)` generated jQuery numeric range attributes `True`/`True`. Remove that incompatible attribute; the view retains its required checkbox and the controller retains its explicit server consent rejection before persistence or notifications.
- Founder Growth Economics and Channel Outcomes received OpenAI HTTP 400 because the reporting end contained minutes/seconds. Resolve timezone through the same owner-bound `/ad_account` authority, request only completed account-local hours inside the selected range, and never request future hours. Use canonical campaign field selectors and disclose effective provider boundaries while retaining the selected canonical-outcome range. No account timezone is guessed or hardcoded.

UTC-minute boundary traversal handles fractional timezone offsets and DST repeated/skipped hours without fabricating a local timestamp. Missing timezone or a range without a complete hour produces an explicit unavailable reason rather than a fabricated zero report.

## Validation

Focused execution, consent, owner-isolation and growth contracts: 31 cases pass. Five source contracts initially could not locate the worktree because compiled output is isolated under `/tmp`; rerunning only those five with the standard `GITHUB_WORKSPACE` set passes. An additional projection regression checks canonical requested fields, disclosed hour boundaries and unchanged canonical analytics range. Exact candidate CI remains the merge gate.

## Deployment scope

Portal and Client host the shared reporting controllers; Protect needs the consent fix. Parfait and the static Website have no changed active path in this patch and remain at their successful deployed revisions. There is no database migration, routing change, provider-setting mutation, test-policy exception or deployment override. The established retained-ZIP reconciliation path deploys only the selected three hosts.

## Live evidence and remaining tests

A single labelled synthetic LEGEND inquiry was accepted by the public form and appeared in Founder CRM and analytics (`lead_632`). Meta accepted receipts increased from 1 to 2; OpenAI recorded 1 attempt and 1 accepted receipt, with no pending/retrying/failed receipts. OpenAI raw browser diagnostics also observed LEGEND contact and Protect assessment page views. Direct QA traffic carries no claim of paid campaign credit.

Protect submission remains uncompleted until this fix is live. The Store link returns resource unavailable and requires separate routing diagnosis. CRM detail interaction hit an organization browser-policy block; no alternate path was used. Payment, booking, downstream stage and paid attribution proof must not be represented as complete. OpenAI brand review remains in progress; conversion receipt acceptance does not prove ad activation.
