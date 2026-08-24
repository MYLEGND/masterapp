# LEGEND production convergence repair gates

This branch does not override curriculum, replay, evidence, promotion, or provider authorities.

Production merge gates:

1. AgentPortal.Tests restores and builds on .NET 10.
2. `dotnet test --list-tests` discovers the LEGEND SQL E2E tests; zero-test success is rejected.
3. Historical reevaluation, versioning, runtime-policy, meaning-graph, and curriculum regression suites pass.
4. The existing production read-only diagnostic must report the live replay/curriculum state before any replay mutation is considered.
5. Do not manually mark replay or Founder manifest rows Completed, change evaluator versions, skip failed work, fabricate evidence, or bypass production eligibility.
6. If live work is Pending/Processing with healthy leases, allow the existing durable authority to converge.
7. If work is Failed or repeatedly lease-expired, repair the specific canonical processing defect and validate it on this branch before production deployment.
8. Provider/account failures (for example HTTP 429/no credits) remain separate from code correctness and cannot be hidden by fallback or fabricated success.
