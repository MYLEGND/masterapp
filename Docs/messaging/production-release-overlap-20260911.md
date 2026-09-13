# Production release overlap

Run 223 (34656084353) successfully deployed AgentPortal at 23:33:29 UTC and ClientApp at 23:46:32 UTC. Its ClientApp unauthenticated shared-content routing check then timed out repeatedly and failed at 23:52:03 UTC. Later verification jobs were skipped. The failure did not roll back the deployed packages.

Run 224 (34657690811) began its deploy job at 23:41:19 UTC and changed both hosts' runtime settings at 23:41:41–23:41:54 UTC, while run 223 was still deploying ClientApp. This demonstrates overlapping production mutations. It is a plausible source of startup interference, not proof that it alone caused every timeout. A subsequent direct ClientApp probe returned the required 302 in 4.50 seconds; its static favicon returned 200 in 1.58 seconds.

The workflow previously used a concurrency group containing the PR number, permitting different PRs to deploy simultaneously. It now uses one repository-wide production group with cancel-in-progress false. The running release and its verification complete before a later release starts. The existing branch-base check, package identity checks, migration gate, response requirement, retry budget and post-deployment evidence remain unchanged.

Eight focused workflow regression cases passed, including the new cross-PR serialization guard. The active run 224 is not cancelled or overwritten with run 223's older package. Live validation and that run's final outcome remain pending. This configuration change applies to future workflow runs after its protected release.
