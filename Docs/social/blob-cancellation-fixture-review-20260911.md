# Independent cancellation fixture review — 2026-09-11

Reviewed by the integration lead before amending the fixture. The combined focused run executed 178 cases: 177 passed and one failed, BlobVideo_CancellationDuringDownloadCleansOnlyOwnWorkspace.

The test calls ProcessAsync with no cancellation token. Its fake download stream throws a bare OperationCanceledException while the caller token remains CancellationToken.None. The recorded Azure RetriableStream stack shows repeated attempts ending in AggregateException. This fixture represents a transport interruption, not the caller cancellation required by the assertion. The runtime's unchanged cancellation contract must not be weakened to accept AggregateException.

The approved correction is to create a CancellationTokenSource in the test, pass its token to ProcessAsync, and cancel that same source from the first fake body read. The fake stream throws cancellation carrying that token. Preserve the assertion that caller cancellation propagates as OperationCanceledException, zero upload attempts, owned-workspace cleanup and preservation of the unrelated file. Add an assertion that the supplied source was actually cancelled. No runtime change or held-out expectation change is authorized by this correction.

The lead inspected both the fixture call site and fake handler, and compared them with the actual failing trace. The fixture owner separately identified the same cause. Azure's implementation distinguishes a non-customer-cancelled exception from cancellation requested on the operation token; the proposed fixture supplies the missing prerequisite rather than bypassing the intended behavior.

This review does not count the failing case as passed. It must be rerun after the amendment.
